using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using OpenClaw.Core.Models;
using OpenClaw.Core.Plugins;

namespace OpenClaw.Cli;

/// <summary>
/// Lazily discovers and executes root CLI commands registered by upstream plugins.
/// Metadata discovery uses an isolated child process; execution uses a fresh child
/// with inherited terminal streams so interactive plugin commands remain usable.
/// </summary>
internal static class PluginCliCommands
{
    private const string ConfigPathEnvironment = "OPENCLAW_CONFIG_PATH";
    private const string WorkspaceEnvironment = "OPENCLAW_WORKSPACE";
    private static readonly TimeSpan DescribeTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan ExecuteTimeout = TimeSpan.FromMinutes(10);

    internal static async Task<int?> TryRunAsync(
        string command,
        string[] args,
        CancellationToken cancellationToken = default)
    {
        var configPathValue = Environment.GetEnvironmentVariable(ConfigPathEnvironment);
        var configPath = Path.GetFullPath(GatewayConfigFile.ExpandPath(
            string.IsNullOrWhiteSpace(configPathValue)
                ? GatewayConfigFile.DefaultConfigPath
                : configPathValue));

        GatewayConfig config;
        var loadedConfig = File.Exists(configPath);
        try
        {
            config = loadedConfig ? GatewayConfigFile.Load(configPath) : new GatewayConfig();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            Console.Error.WriteLine($"Unable to load plugin CLI configuration from '{configPath}': {ex.Message}");
            return 1;
        }

        if (!config.Plugins.Enabled)
            return null;

        var bridgeScript = PluginCommands.ResolveBridgeScriptPath();
        if (bridgeScript is null)
        {
            Console.Error.WriteLine("Plugin bridge script was not found. Reinstall or republish the OpenClaw CLI.");
            return 1;
        }

        var blockedPluginIds = LoadBlockedPluginIds(config.Memory.StoragePath);
        return await TryRunAsync(
            command,
            args,
            config.Plugins,
            Environment.GetEnvironmentVariable(WorkspaceEnvironment),
            blockedPluginIds,
            bridgeScript,
            cancellationToken);
    }

    internal static async Task<int?> TryRunAsync(
        string command,
        string[] args,
        PluginsConfig pluginsConfig,
        string? workspacePath,
        IReadOnlySet<string> blockedPluginIds,
        string bridgeScript,
        CancellationToken cancellationToken)
    {
        var discovered = PluginDiscovery.Filter(
            PluginDiscovery.Discover(pluginsConfig, workspacePath),
            pluginsConfig);
        var matches = new List<(DiscoveredPlugin Plugin, JsonElement? Config)>();

        foreach (var plugin in discovered)
        {
            if (plugin.Format == PluginFormats.Bundle ||
                blockedPluginIds.Contains("*") ||
                blockedPluginIds.Contains(plugin.Manifest.Id) ||
                string.IsNullOrWhiteSpace(plugin.EntryPath))
            {
                continue;
            }

            pluginsConfig.Entries.TryGetValue(plugin.Manifest.Id, out var entryConfig);
            var pluginConfig = entryConfig?.Config;
            var diagnostics = PluginPackageCompatibility.Validate(plugin)
                .Concat(PluginConfigValidator.Validate(plugin.Manifest, pluginConfig));
            if (diagnostics.Any(static item =>
                    string.Equals(item.Severity, "error", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var description = await DescribeAsync(
                plugin.EntryPath,
                plugin.Manifest.Id,
                pluginConfig,
                bridgeScript,
                cancellationToken);
            if (!description.Success)
                continue;

            if (description.Commands.Any(item =>
                    string.Equals(item.Name, command, StringComparison.Ordinal)))
            {
                matches.Add((plugin, pluginConfig));
            }
        }

        if (matches.Count == 0)
            return null;

        if (matches.Count > 1)
        {
            Console.Error.WriteLine(
                $"Plugin CLI command '{command}' is ambiguous; registered by: " +
                string.Join(", ", matches.Select(static item => item.Plugin.Manifest.Id)));
            return 2;
        }

        var match = matches[0];
        return await ExecuteAsync(
            match.Plugin.EntryPath,
            match.Plugin.Manifest.Id,
            match.Config,
            bridgeScript,
            [command, .. args],
            cancellationToken);
    }

    internal static async Task<PluginCliDescribeResult> DescribeAsync(
        string entryPath,
        string pluginId,
        JsonElement? pluginConfig,
        string bridgeScript,
        CancellationToken cancellationToken)
    {
        using var process = CreateProcess(entryPath, pluginId, pluginConfig, bridgeScript, "--cli-describe");
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;

        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return PluginCliDescribeResult.Failure($"Unable to start Node.js for plugin CLI discovery: {ex.Message}");
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(DescribeTimeout);
            await process.WaitForExitAsync(timeout.Token);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            if (process.ExitCode != 0)
            {
                return PluginCliDescribeResult.Failure(
                    string.IsNullOrWhiteSpace(stderr)
                        ? $"Plugin CLI discovery exited with code {process.ExitCode}."
                        : stderr.Trim());
            }

            var descriptorLine = stdout
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .LastOrDefault();
            var commands = descriptorLine is null
                ? null
                : JsonSerializer.Deserialize(
                descriptorLine,
                CoreJsonContext.Default.BridgeCliCommandRegistrationArray);
            return commands is null
                ? PluginCliDescribeResult.Failure("Plugin CLI discovery returned unreadable metadata.")
                : new PluginCliDescribeResult { Success = true, Commands = commands };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            return PluginCliDescribeResult.Failure("Plugin CLI discovery timed out after 20 seconds.");
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or JsonException or NotSupportedException)
        {
            TryKill(process);
            return PluginCliDescribeResult.Failure($"Plugin CLI discovery failed: {ex.Message}");
        }
    }

    private static async Task<int> ExecuteAsync(
        string entryPath,
        string pluginId,
        JsonElement? pluginConfig,
        string bridgeScript,
        string[] argv,
        CancellationToken cancellationToken)
    {
        using var process = CreateProcess(entryPath, pluginId, pluginConfig, bridgeScript, "--cli-run");
        foreach (var arg in argv)
            process.StartInfo.ArgumentList.Add(arg);

        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            Console.Error.WriteLine($"Unable to start plugin CLI command: {ex.Message}");
            return 1;
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ExecuteTimeout);
            await process.WaitForExitAsync(timeout.Token);
            return process.ExitCode;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            Console.Error.WriteLine("Plugin CLI command timed out after 10 minutes.");
            return 124;
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }
    }

    private static Process CreateProcess(
        string entryPath,
        string pluginId,
        JsonElement? pluginConfig,
        string bridgeScript,
        string mode)
    {
        var nodeExecutable = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "node.exe" : "node";
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = nodeExecutable,
                WorkingDirectory = Path.GetDirectoryName(entryPath) ?? Directory.GetCurrentDirectory(),
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        process.StartInfo.ArgumentList.Add("--experimental-vm-modules");
        process.StartInfo.ArgumentList.Add(bridgeScript);
        process.StartInfo.ArgumentList.Add(mode);
        process.StartInfo.Environment["OPENCLAW_PLUGIN_CLI_ENTRY"] = Path.GetFullPath(entryPath);
        process.StartInfo.Environment["OPENCLAW_PLUGIN_CLI_ID"] = pluginId;
        process.StartInfo.Environment["OPENCLAW_PLUGIN_CLI_CONFIG_BASE64"] = Convert.ToBase64String(
            Encoding.UTF8.GetBytes(pluginConfig?.GetRawText() ?? "{}"));
        return process;
    }

    internal static HashSet<string> LoadBlockedPluginIds(string storagePath)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            var rootedStoragePath = Path.IsPathRooted(storagePath)
                ? storagePath
                : Path.GetFullPath(storagePath);
            var statePath = Path.Combine(rootedStoragePath, "admin", "plugin-state.json");
            if (!File.Exists(statePath))
                return result;

            using var stream = File.OpenRead(statePath);
            var states = JsonSerializer.Deserialize(
                stream,
                CoreJsonContext.Default.ListPluginOperatorState) ?? [];
            foreach (var state in states)
            {
                if (state.Disabled || state.Quarantined)
                    result.Add(state.PluginId);
            }
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or JsonException
                                   or InvalidOperationException
                                   or NotSupportedException
                                   or ArgumentException)
        {
            // A malformed optional operator-state file must not activate a plugin.
            // Treat discovery as empty rather than bypassing a possible quarantine.
            return new HashSet<string>(StringComparer.Ordinal) { "*" };
        }

        return result;
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
        {
        }
    }

    internal sealed class PluginCliDescribeResult
    {
        public bool Success { get; init; }
        public string? Error { get; init; }
        public IReadOnlyList<BridgeCliCommandRegistration> Commands { get; init; } = [];

        public static PluginCliDescribeResult Failure(string error)
            => new() { Error = error };
    }
}
