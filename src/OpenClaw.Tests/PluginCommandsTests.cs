using System.Diagnostics;
using System.Text.Json;
using OpenClaw.Cli;
using OpenClaw.Core.Plugins;
using Xunit;

namespace OpenClaw.Tests;

public sealed class PluginCommandsTests
{
    [Fact]
    public void InspectCandidate_WithManifest_ReturnsUpstreamCompatibleSummary()
    {
        var root = CreateTempRoot();
        try
        {
            File.WriteAllText(
                Path.Combine(root, "openclaw.plugin.json"),
                """
                {
                  "id": "sample-plugin",
                  "name": "Sample Plugin",
                  "version": "1.2.3",
                  "description": "Test plugin",
                  "channels": ["telegram"],
                  "providers": ["sample-provider"],
                  "skills": ["skills"]
                }
                """);
            File.WriteAllText(Path.Combine(root, "index.js"), "export default {};");
            var skillsDir = Path.Combine(root, "skills");
            Directory.CreateDirectory(skillsDir);
            File.WriteAllText(Path.Combine(skillsDir, "SKILL.md"), "# Sample skill");

            var inspection = PluginCommands.InspectCandidate(root, "./sample-plugin", sourceIsNpm: false);

            Assert.True(inspection.Success);
            Assert.True(inspection.CanInstall);
            Assert.Equal("sample-plugin", inspection.PluginId);
            Assert.Equal("upstream-compatible", inspection.TrustLevel);
            Assert.Equal("manifest-valid", inspection.CompatibilityStatus);
            Assert.Contains("channels=telegram", inspection.DeclaredSurface, StringComparison.Ordinal);
            Assert.Contains("providers=sample-provider", inspection.DeclaredSurface, StringComparison.Ordinal);
            Assert.Contains("skills=1", inspection.DeclaredSurface, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void InspectCandidate_WithRegisterCli_AllowsInstall()
    {
        var root = CreateTempRoot();
        try
        {
            File.WriteAllText(
                Path.Combine(root, "openclaw.plugin.json"),
                """{"id":"cli-plugin","configSchema":{"type":"object"}}""");
            File.WriteAllText(
                Path.Combine(root, "index.js"),
                "module.exports = api => api.registerCli(({ program }) => program.command('fixture'), { commands: ['fixture'] });");

            var inspection = PluginCommands.InspectCandidate(root, "./cli-plugin", sourceIsNpm: false);

            Assert.True(inspection.Success);
            Assert.True(inspection.CanInstall);
            Assert.Equal("manifest-valid", inspection.CompatibilityStatus);
            Assert.DoesNotContain(inspection.Diagnostics, item => item.Code == "unsupported_cli_registration");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void InspectCandidate_WithNewerPluginApiFloor_BlocksInstall()
    {
        var root = CreateTempRoot();
        try
        {
            File.WriteAllText(
                Path.Combine(root, "openclaw.plugin.json"),
                """{"id":"future-plugin","configSchema":{"type":"object"}}""");
            File.WriteAllText(Path.Combine(root, "dist.js"), "module.exports = () => {};");
            File.WriteAllText(
                Path.Combine(root, "package.json"),
                """
                {
                  "name": "future-plugin",
                  "openclaw": {
                    "runtimeExtensions": ["./dist.js"],
                    "compat": { "pluginApi": ">=2026.7.1" }
                  }
                }
                """);

            var inspection = PluginCommands.InspectCandidate(root, "./future-plugin", sourceIsNpm: false);

            Assert.False(inspection.CanInstall);
            Assert.Contains(inspection.Diagnostics, item => item.Code == "plugin_api_version_unsupported");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task InspectRuntimeAsync_WithRegisterCli_ReturnsDescriptorCount()
    {
        if (!HasNode())
            return;

        var root = CreateTempRoot();
        try
        {
            var entryPath = Path.Combine(root, "index.js");
            File.WriteAllText(
                entryPath,
                "module.exports = api => api.registerCli(({ program }) => program.command('fixture').description('Fixture commands'), { commands: ['fixture'] });");

            var inspection = await PluginCommands.InspectRuntimeAsync(
                entryPath,
                "runtime-cli",
                TestContext.Current.CancellationToken);

            Assert.True(inspection.Compatible);
            Assert.Equal(1, inspection.CliCommandCount);
            Assert.DoesNotContain(inspection.Diagnostics, item => item.Code == "unsupported_cli_registration");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PluginCliCommands_ExecutesNestedCommandWithArgumentsAndOptions()
    {
        if (!HasNode())
            return;

        var root = CreateTempRoot();
        try
        {
            var markerPath = Path.Combine(root, "result.txt");
            File.WriteAllText(
                Path.Combine(root, "openclaw.plugin.json"),
                """{"id":"cli-execution-plugin","configSchema":{"type":"object"}}""");
            File.WriteAllText(
                Path.Combine(root, "index.js"),
                $$"""
                const fs = require("node:fs");
                module.exports = api => api.registerCli(({ program }) => {
                  const root = program.command("fixture").description("Fixture commands");
                  root.command("write")
                    .argument("<value>", "Value to write")
                    .option("--upper", "Uppercase the value")
                    .action(async (value, options) => {
                      fs.writeFileSync({{JsonSerializer.Serialize(markerPath)}}, options.upper ? value.toUpperCase() : value);
                    });
                }, { commands: ["fixture"] });
                """);

            var bridgeScript = PluginCommands.ResolveBridgeScriptPath();
            Assert.NotNull(bridgeScript);
            var result = await PluginCliCommands.TryRunAsync(
                "fixture",
                ["write", "hello", "--upper"],
                new PluginsConfig { Load = new PluginLoadConfig { Paths = [root] } },
                workspacePath: null,
                new HashSet<string>(StringComparer.Ordinal),
                bridgeScript,
                TestContext.Current.CancellationToken);

            Assert.Equal(0, result);
            Assert.Equal("HELLO", File.ReadAllText(markerPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void InspectCandidate_WithStandaloneEntry_ReturnsUntrustedWarning()
    {
        var root = CreateTempRoot();
        try
        {
            File.WriteAllText(Path.Combine(root, "index.js"), "export default {};");

            var inspection = PluginCommands.InspectCandidate(root, "./standalone-plugin", sourceIsNpm: false);

            Assert.True(inspection.Success);
            Assert.True(inspection.CanInstall);
            Assert.Equal("untrusted", inspection.TrustLevel);
            Assert.Equal("entry-only", inspection.DeclaredSurface);
            Assert.Contains(inspection.Warnings, warning => warning.Contains("No openclaw.plugin.json manifest", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void InspectCandidate_WithCompatibleBundle_ReportsMappedAndDetectedCapabilities()
    {
        var root = CreateTempRoot();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, ".claude-plugin"));
            Directory.CreateDirectory(Path.Combine(root, "commands"));
            Directory.CreateDirectory(Path.Combine(root, "agents"));
            File.WriteAllText(
                Path.Combine(root, ".claude-plugin", "plugin.json"),
                "{\"name\":\"claude-value-bundle\",\"version\":\"1.0.0\"}");
            File.WriteAllText(Path.Combine(root, "commands", "summarize.md"), "Summarize the current task.");

            var inspection = PluginCommands.InspectCandidate(root, "./claude-value-bundle", sourceIsNpm: false);

            Assert.True(inspection.Success);
            Assert.True(inspection.CanInstall);
            Assert.Equal(PluginFormats.Bundle, inspection.Format);
            Assert.Equal("claude", inspection.BundleFormat);
            Assert.Contains("mapped=commands", inspection.DeclaredSurface, StringComparison.Ordinal);
            Assert.Contains("detected_only=agents", inspection.DeclaredSurface, StringComparison.Ordinal);
            Assert.Contains(inspection.Diagnostics, item => item.Code == "bundle_capability_detected_only");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task InstallPreparedDirectoryAsync_BundleDoesNotRunNpmLifecycleScripts()
    {
        var root = CreateTempRoot();
        var targetParent = CreateTempRoot();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, ".codex-plugin"));
            Directory.CreateDirectory(Path.Combine(root, "skills", "safe-bundle"));
            File.WriteAllText(Path.Combine(root, ".codex-plugin", "plugin.json"), "{\"name\":\"safe-bundle\"}");
            File.WriteAllText(
                Path.Combine(root, "skills", "safe-bundle", "SKILL.md"),
                "---\nname: safe-bundle\ndescription: Safe bundle\n---\nUse safe content.");
            File.WriteAllText(
                Path.Combine(root, "package.json"),
                "{\"name\":\"safe-bundle\",\"scripts\":{\"install\":\"node -e \\\"require('fs').writeFileSync('lifecycle-ran','yes')\\\"\"}}");
            var target = Path.Combine(targetParent, "safe-bundle");

            var result = await PluginCommands.InstallPreparedDirectoryAsync(
                root,
                target,
                "./safe-bundle",
                sourceIsNpm: false);

            Assert.True(result.Success, result.Error);
            Assert.True(Directory.Exists(target));
            Assert.False(File.Exists(Path.Combine(target, "lifecycle-ran")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            Directory.Delete(targetParent, recursive: true);
        }
    }

    [Fact]
    public async Task InstallPreparedDirectoryAsync_InvalidBundlePreservesExistingInstall()
    {
        var root = CreateTempRoot();
        var targetParent = CreateTempRoot();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, ".claude-plugin"));
            File.WriteAllText(
                Path.Combine(root, ".claude-plugin", "plugin.json"),
                "{\"name\":\"preserved-bundle\",\"skills\":[\"missing-skills\"]}");
            var target = Path.Combine(targetParent, "preserved-bundle");
            Directory.CreateDirectory(target);
            File.WriteAllText(Path.Combine(target, "sentinel.txt"), "working-version");

            var result = await PluginCommands.InstallPreparedDirectoryAsync(
                root,
                target,
                "./preserved-bundle",
                sourceIsNpm: false);

            Assert.False(result.Success);
            Assert.Equal("working-version", File.ReadAllText(Path.Combine(target, "sentinel.txt")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            Directory.Delete(targetParent, recursive: true);
        }
    }

    [Fact]
    public void InspectCandidate_WithInvalidConfigSchema_BlocksInstall()
    {
        var root = CreateTempRoot();
        try
        {
            File.WriteAllText(
                Path.Combine(root, "openclaw.plugin.json"),
                """
                {
                  "id": "schema-plugin",
                  "name": "Schema Plugin",
                  "configSchema": {
                    "type": "object",
                    "$ref": "#/definitions/unsupported"
                  }
                }
                """);
            File.WriteAllText(Path.Combine(root, "index.js"), "export default {};");

            var inspection = PluginCommands.InspectCandidate(root, "./schema-plugin", sourceIsNpm: false);

            Assert.True(inspection.Success);
            Assert.False(inspection.CanInstall);
            Assert.Equal("errors", inspection.CompatibilityStatus);
            Assert.True(inspection.ErrorCount > 0);
            Assert.Contains(inspection.Diagnostics, item => item.Code == "unsupported_schema_keyword");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "openclaw-plugin-command-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static bool HasNode()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = OperatingSystem.IsWindows() ? "node.exe" : "node",
                Arguments = "--version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            process?.WaitForExit(3000);
            return process is { ExitCode: 0 };
        }
        catch
        {
            return false;
        }
    }
}
