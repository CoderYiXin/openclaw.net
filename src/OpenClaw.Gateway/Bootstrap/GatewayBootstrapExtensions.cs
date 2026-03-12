using System.Net.Http.Headers;
using Microsoft.Extensions.Configuration;
using OpenClaw.Core.Models;
using OpenClaw.Core.Security;
using OpenClaw.Core.Validation;
using OpenClaw.Gateway.Extensions;

namespace OpenClaw.Gateway.Bootstrap;

internal static class GatewayBootstrapExtensions
{
    public static async Task<BootstrapResult> AddOpenClawBootstrapAsync(this WebApplicationBuilder builder, string[] args)
    {
        ApplyConfigFileOverride(builder, args);

        builder.Services.ConfigureHttpJsonOptions(opts =>
            opts.SerializerOptions.TypeInfoResolverChain.Add(CoreJsonContext.Default));

        var config = LoadGatewayConfig(builder.Configuration);

        var isNonLoopbackBind = !GatewaySecurity.IsLoopbackBind(config.BindAddress);
        var isDoctorMode = args.Any(a => string.Equals(a, "--doctor", StringComparison.Ordinal));
        var isHealthCheckMode = args.Any(a => string.Equals(a, "--health-check", StringComparison.Ordinal));

        if (isHealthCheckMode)
        {
            var exitCode = await RunHealthCheckAsync(config, isNonLoopbackBind);
            return new BootstrapResult
            {
                ShouldExit = true,
                ExitCode = exitCode
            };
        }

        if (isNonLoopbackBind && string.IsNullOrWhiteSpace(config.AuthToken))
        {
            var message = "OPENCLAW_AUTH_TOKEN must be set when binding to a non-loopback address.";
            if (isDoctorMode)
            {
                Console.Error.WriteLine(message);
                return new BootstrapResult
                {
                    ShouldExit = true,
                    ExitCode = 1
                };
            }

            throw new InvalidOperationException(message);
        }

        var configErrors = ConfigValidator.Validate(config);
        if (configErrors.Count > 0)
        {
            foreach (var err in configErrors)
                Console.Error.WriteLine($"Configuration error: {err}");

            if (isDoctorMode)
            {
                return new BootstrapResult
                {
                    ShouldExit = true,
                    ExitCode = 1
                };
            }

            throw new InvalidOperationException($"Gateway configuration has {configErrors.Count} error(s). See above for details.");
        }

        GatewayRuntimeState runtimeState;
        try
        {
            runtimeState = RuntimeModeResolver.Resolve(config.Runtime);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Configuration error: {ex.Message}");
            if (isDoctorMode)
            {
                return new BootstrapResult
                {
                    ShouldExit = true,
                    ExitCode = 1
                };
            }

            throw;
        }

        if (isDoctorMode)
        {
            var ok = await DoctorCheck.RunAsync(config, runtimeState);
            return new BootstrapResult
            {
                ShouldExit = true,
                ExitCode = ok ? 0 : 1
            };
        }

        GatewaySecurityExtensions.EnforcePublicBindHardening(config, isNonLoopbackBind);

        return new BootstrapResult
        {
            ShouldExit = false,
            ExitCode = 0,
            Startup = new GatewayStartupContext
            {
                Config = config,
                RuntimeState = runtimeState,
                IsNonLoopbackBind = isNonLoopbackBind,
                WorkspacePath = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE")
            }
        };
    }

    internal static GatewayConfig LoadGatewayConfig(IConfiguration configuration)
    {
        var openClawSection = configuration.GetSection("OpenClaw");
        var config = openClawSection.Get<GatewayConfig>() ?? new GatewayConfig();
        ApplyConfiguredToolingOverrides(openClawSection, config);
        ApplyEnvironmentOverrides(config);
        return config;
    }

    private static void ApplyConfigFileOverride(WebApplicationBuilder builder, string[] args)
    {
        var extraConfigPath = FindArgValue(args, "--config")
            ?? Environment.GetEnvironmentVariable("OPENCLAW_CONFIG_PATH");
        if (string.IsNullOrWhiteSpace(extraConfigPath))
            return;

        var fullPath = Path.GetFullPath(ExpandPath(extraConfigPath));
        builder.Configuration.AddJsonFile(fullPath, optional: false, reloadOnChange: true);
    }

    private static string? FindArgValue(string[] argv, string name)
    {
        for (var i = 0; i < argv.Length; i++)
        {
            var arg = argv[i];
            if (arg.Equals(name, StringComparison.Ordinal) && i + 1 < argv.Length)
                return argv[i + 1];

            var prefix = name + "=";
            if (arg.StartsWith(prefix, StringComparison.Ordinal))
                return arg[prefix.Length..];
        }

        return null;
    }

    private static string ExpandPath(string path)
    {
        var expanded = Environment.ExpandEnvironmentVariables(path);
        if (!expanded.StartsWith('~'))
            return expanded;

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            expanded[1..].TrimStart('/').TrimStart('\\'));
    }

    private static void ApplyEnvironmentOverrides(GatewayConfig config)
    {
        config.Llm.ApiKey = ResolveSecretRefOrNull(config.Llm.ApiKey) ?? Environment.GetEnvironmentVariable("MODEL_PROVIDER_KEY");
        config.Llm.Model = Environment.GetEnvironmentVariable("MODEL_PROVIDER_MODEL") ?? config.Llm.Model;
        config.Llm.Endpoint = ResolveSecretRefOrNull(config.Llm.Endpoint) ?? Environment.GetEnvironmentVariable("MODEL_PROVIDER_ENDPOINT");
        config.AuthToken ??= Environment.GetEnvironmentVariable("OPENCLAW_AUTH_TOKEN");
    }

    private static void ApplyConfiguredToolingOverrides(IConfiguration section, GatewayConfig config)
    {
        var toolingSection = section.GetSection("Tooling");

        var allowedReadRoots = ReadStringArray(toolingSection, "AllowedReadRoots");
        if (allowedReadRoots is not null)
            config.Tooling.AllowedReadRoots = allowedReadRoots;

        var allowedWriteRoots = ReadStringArray(toolingSection, "AllowedWriteRoots");
        if (allowedWriteRoots is not null)
            config.Tooling.AllowedWriteRoots = allowedWriteRoots;
    }

    private static string[]? ReadStringArray(IConfiguration section, string key)
    {
        var values = section.GetSection(key)
            .GetChildren()
            .Select(child => child.Value)
            .Where(value => value is not null)
            .Cast<string>()
            .ToArray();

        return values.Length > 0 ? values : null;
    }

    private static string? ResolveSecretRefOrNull(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        if (value.StartsWith("env:", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("raw:", StringComparison.OrdinalIgnoreCase))
        {
            return SecretResolver.Resolve(value);
        }

        return value;
    }

    private static async Task<int> RunHealthCheckAsync(GatewayConfig config, bool isNonLoopbackBind)
    {
        var url = $"http://127.0.0.1:{config.Port}/health";
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (isNonLoopbackBind && !string.IsNullOrWhiteSpace(config.AuthToken))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.AuthToken);

        try
        {
            using var resp = await http.SendAsync(req);
            return resp.IsSuccessStatusCode ? 0 : 1;
        }
        catch
        {
            return 1;
        }
    }
}
