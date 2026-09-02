using System.Text.Json;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Memory;
using OpenClaw.PluginKit;

namespace OpenClaw.TestPluginFixtures;

public sealed class ToolAndCommandPlugin : INativeDynamicPlugin
{
    public void Register(INativeDynamicPluginContext context)
    {
        string? startPath = null;
        string? stopPath = null;

        context.RegisterTool(new EchoTool());
        context.RegisterCommand("native_dynamic_echo", "Echo from native dynamic plugin", (args, _) => Task.FromResult($"cmd:{args}"));

        if (TryGetConfigValue(context.Config, "startPath", out var configuredStartPath))
            startPath = configuredStartPath;
        if (TryGetConfigValue(context.Config, "stopPath", out var configuredStopPath))
            stopPath = configuredStopPath;

        if (TryGetConfigValue(context.Config, "memoryProviderId", out var memoryProviderId))
        {
            context.RegisterMemoryProvider(memoryProviderId, providerContext =>
                new FileMemoryStore(providerContext.GatewayConfig.Memory.StoragePath));
        }

        if (!string.IsNullOrWhiteSpace(startPath) || !string.IsNullOrWhiteSpace(stopPath))
        {
            context.RegisterService(new MarkerService(startPath, stopPath));
        }
    }

    private static bool TryGetConfigValue(JsonElement? config, string propertyName, out string value)
    {
        value = "";
        if (config is not { ValueKind: JsonValueKind.Object } obj ||
            !obj.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString() ?? "";
        return !string.IsNullOrWhiteSpace(value);
    }

    private sealed class EchoTool : ITool
    {
        public string Name => "native_dynamic_echo";
        public string Description => "Echo tool from native dynamic plugin";
        public string ParameterSchema => """{"type":"object","properties":{"text":{"type":"string"}},"required":["text"]}""";

        public ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            var text = doc.RootElement.TryGetProperty("text", out var prop) ? prop.GetString() ?? "" : "";
            return ValueTask.FromResult("native:" + text);
        }
    }

    private sealed class MarkerService(string? startPath, string? stopPath) : INativeDynamicPluginService
    {
        public Task StartAsync(CancellationToken ct)
        {
            if (!string.IsNullOrWhiteSpace(startPath))
                File.WriteAllText(startPath, "started");
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken ct)
        {
            if (!string.IsNullOrWhiteSpace(stopPath))
                File.WriteAllText(stopPath, "stopped");
            return Task.CompletedTask;
        }
    }
}
