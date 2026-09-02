using System.Text.Json;
using OpenClaw.Core.Models;
using OpenClaw.Core.Plugins;
using OpenClaw.Agent.Plugins;
using OpenClaw.Core.Skills;
using Xunit;

namespace OpenClaw.Tests;

public class PluginDiscoveryTests : IDisposable
{
    private readonly string _tempDir;

    public PluginDiscoveryTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "openclaw-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void Discover_FindsPluginWithManifest()
    {
        // Arrange – create a plugin directory with manifest + entry
        var pluginDir = Path.Combine(_tempDir, "test-plugin");
        Directory.CreateDirectory(pluginDir);

        File.WriteAllText(Path.Combine(pluginDir, "openclaw.plugin.json"),
            """{"id":"test-plugin","name":"Test Plugin","version":"1.0.0"}""");
        File.WriteAllText(Path.Combine(pluginDir, "index.ts"), "export default function() {}");

        var config = new PluginsConfig { Load = new PluginLoadConfig { Paths = [_tempDir] } };

        // Act
        var discovered = PluginDiscovery.Discover(config);

        // Assert
        Assert.Single(discovered);
        Assert.Equal("test-plugin", discovered[0].Manifest.Id);
        Assert.EndsWith("index.ts", discovered[0].EntryPath);
    }

    [Fact]
    public void Discover_PrefersBuiltRuntimeExtensionAndReadsCompatibilityMetadata()
    {
        var pluginDir = Path.Combine(_tempDir, "modern-plugin");
        Directory.CreateDirectory(Path.Combine(pluginDir, "src"));
        Directory.CreateDirectory(Path.Combine(pluginDir, "dist"));
        File.WriteAllText(Path.Combine(pluginDir, "openclaw.plugin.json"), """{"id":"modern-plugin"}""");
        File.WriteAllText(Path.Combine(pluginDir, "src", "index.ts"), "export default function() {}");
        File.WriteAllText(Path.Combine(pluginDir, "dist", "index.js"), "module.exports = function() {};");
        File.WriteAllText(
            Path.Combine(pluginDir, "package.json"),
            """
            {
              "name": "modern-plugin",
              "openclaw": {
                "extensions": ["./src/index.ts"],
                "runtimeExtensions": ["./dist/index.js"],
                "compat": {
                  "pluginApi": ">=2026.5.4",
                  "minGatewayVersion": ">=2026.5.4"
                },
                "install": { "expectedIntegrity": "sha512-example" }
              }
            }
            """);

        var plugin = Assert.Single(PluginDiscovery.Discover(new PluginsConfig
        {
            Load = new PluginLoadConfig { Paths = [pluginDir] }
        }));

        Assert.EndsWith(Path.Combine("dist", "index.js"), plugin.EntryPath);
        Assert.Equal(">=2026.5.4", plugin.PluginApiRange);
        Assert.Equal(">=2026.5.4", plugin.MinHostVersion);
        Assert.Equal("sha512-example", plugin.ExpectedIntegrity);
        Assert.Contains(
            PluginPackageCompatibility.Validate(plugin),
            diagnostic => diagnostic.Code == "package_integrity_unverified");
    }

    [Theory]
    [InlineData("codex", ".codex-plugin")]
    [InlineData("claude", ".claude-plugin")]
    [InlineData("cursor", ".cursor-plugin")]
    public void Discover_DetectsCompatibleBundleManifests(string bundleFormat, string markerDirectory)
    {
        var bundleDir = Path.Combine(_tempDir, $"{bundleFormat}-bundle");
        Directory.CreateDirectory(Path.Combine(bundleDir, markerDirectory));
        Directory.CreateDirectory(Path.Combine(bundleDir, "skills", "bundle-skill"));
        File.WriteAllText(
            Path.Combine(bundleDir, markerDirectory, "plugin.json"),
            $$"""{"name":"{{bundleFormat}}-sample","version":"1.0.0"}""");
        File.WriteAllText(
            Path.Combine(bundleDir, "skills", "bundle-skill", "SKILL.md"),
            "---\nname: bundle-skill\ndescription: Bundle skill\n---\nUse the bundle.");

        var plugin = Assert.Single(PluginDiscovery.Discover(new PluginsConfig
        {
            Load = new PluginLoadConfig { Paths = [bundleDir] }
        }));

        Assert.Equal(PluginFormats.Bundle, plugin.Format);
        Assert.Equal(bundleFormat, plugin.BundleFormat);
        Assert.Contains("skills", plugin.BundleMappedCapabilities);
        Assert.Empty(plugin.EntryPath);
    }

    [Fact]
    public void Discover_ManifestlessClaudeBundle_MapsCommandsAndReportsOtherSurfaces()
    {
        var bundleDir = Path.Combine(_tempDir, "claude-default-bundle");
        Directory.CreateDirectory(Path.Combine(bundleDir, "commands"));
        Directory.CreateDirectory(Path.Combine(bundleDir, "agents"));
        File.WriteAllText(Path.Combine(bundleDir, "commands", "review.md"), "Review this change carefully.");
        File.WriteAllText(Path.Combine(bundleDir, ".mcp.json"), "{}");

        var plugin = Assert.Single(PluginDiscovery.Discover(new PluginsConfig
        {
            Load = new PluginLoadConfig { Paths = [bundleDir] }
        }));

        Assert.Equal("claude", plugin.BundleFormat);
        Assert.Contains("commands", plugin.BundleMappedCapabilities);
        Assert.Contains("agents", plugin.BundleDetectedCapabilities);
        Assert.Contains("mcp", plugin.BundleDetectedCapabilities);
    }

    [Fact]
    public void Discover_NativePluginTakesPrecedenceOverBundleMarkers()
    {
        var pluginDir = Path.Combine(_tempDir, "dual-format");
        Directory.CreateDirectory(Path.Combine(pluginDir, ".claude-plugin"));
        File.WriteAllText(Path.Combine(pluginDir, ".claude-plugin", "plugin.json"), "{\"name\":\"bundle-copy\"}");
        File.WriteAllText(Path.Combine(pluginDir, "openclaw.plugin.json"), "{\"id\":\"native-wins\"}");
        File.WriteAllText(Path.Combine(pluginDir, "index.js"), "module.exports = () => {};");

        var plugin = Assert.Single(PluginDiscovery.Discover(new PluginsConfig
        {
            Load = new PluginLoadConfig { Paths = [pluginDir] }
        }));

        Assert.Equal(PluginFormats.Native, plugin.Format);
        Assert.Equal("native-wins", plugin.Manifest.Id);
    }

    [Fact]
    public void Discover_StandaloneEntryWithWeakBundleFolders_RemainsNative()
    {
        var pluginDir = Path.Combine(_tempDir, "standalone-with-content");
        Directory.CreateDirectory(Path.Combine(pluginDir, "skills"));
        Directory.CreateDirectory(Path.Combine(pluginDir, "commands"));
        Directory.CreateDirectory(Path.Combine(pluginDir, "agents"));
        Directory.CreateDirectory(Path.Combine(pluginDir, "hooks"));
        File.WriteAllText(Path.Combine(pluginDir, ".mcp.json"), "{}");
        File.WriteAllText(Path.Combine(pluginDir, "settings.json"), "{}");
        File.WriteAllText(Path.Combine(pluginDir, "index.js"), "module.exports = () => {};");

        var plugin = Assert.Single(PluginDiscovery.Discover(new PluginsConfig
        {
            Load = new PluginLoadConfig { Paths = [pluginDir] }
        }));

        Assert.Equal(PluginFormats.Native, plugin.Format);
        Assert.EndsWith("index.js", plugin.EntryPath, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    public void Discover_BundleManifestMustBeJsonObject(string manifestJson)
    {
        var bundleDir = Path.Combine(_tempDir, $"invalid-bundle-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(bundleDir, ".codex-plugin"));
        File.WriteAllText(Path.Combine(bundleDir, ".codex-plugin", "plugin.json"), manifestJson);

        var result = PluginDiscovery.DiscoverWithDiagnostics(new PluginsConfig
        {
            Load = new PluginLoadConfig { Paths = [bundleDir] }
        });

        Assert.Empty(result.Plugins);
        Assert.Contains(
            result.Reports.SelectMany(static report => report.Diagnostics),
            diagnostic => diagnostic.Code == "invalid_bundle_manifest");
    }

    [Theory]
    [InlineData("^2026.5.0")]
    [InlineData("~2026.5.0")]
    [InlineData("2026")]
    public void PackageCompatibility_NormalizesCommonNpmVersionRanges(string range)
    {
        Assert.Empty(PluginPackageCompatibility.Validate(
            range,
            null,
            "range-plugin",
            _tempDir));
    }

    [Fact]
    public void Discover_SkipsBrokenManifestJson()
    {
        // Arrange – invalid manifest JSON should be ignored without throwing
        var pluginDir = Path.Combine(_tempDir, "broken-plugin");
        Directory.CreateDirectory(pluginDir);

        File.WriteAllText(Path.Combine(pluginDir, "openclaw.plugin.json"), "{ this is not valid json");
        File.WriteAllText(Path.Combine(pluginDir, "index.ts"), "export default function() {}");

        var config = new PluginsConfig { Load = new PluginLoadConfig { Paths = [_tempDir] } };

        // Act
        var discovered = PluginDiscovery.Discover(config);

        // Assert
        Assert.Empty(discovered);
    }

    [Fact]
    public void Discover_FindsStandaloneFile()
    {
        // Arrange – a bare .ts file with no manifest
        var extDir = Path.Combine(_tempDir, ".openclaw", "extensions");
        Directory.CreateDirectory(extDir);
        File.WriteAllText(Path.Combine(extDir, "my-tool.ts"), "// tool code");

        var config = new PluginsConfig();

        // Act
        var discovered = PluginDiscovery.Discover(config, workspacePath: _tempDir);

        // Assert
        Assert.Single(discovered);
        Assert.Equal("my-tool", discovered[0].Manifest.Id);
    }

    [Fact]
    public void Discover_IgnoresDuplicateIds()
    {
        // Arrange – two plugins with same manifest id
        var dir1 = Path.Combine(_tempDir, "plugins", "a");
        var dir2 = Path.Combine(_tempDir, "plugins", "b");
        Directory.CreateDirectory(dir1);
        Directory.CreateDirectory(dir2);

        foreach (var dir in new[] { dir1, dir2 })
        {
            File.WriteAllText(Path.Combine(dir, "openclaw.plugin.json"),
                """{"id":"duplicate-id"}""");
            File.WriteAllText(Path.Combine(dir, "index.js"), "");
        }

        var config = new PluginsConfig
        {
            Load = new PluginLoadConfig { Paths = [Path.Combine(_tempDir, "plugins")] }
        };

        // Act
        var discovered = PluginDiscovery.Discover(config);

        // Assert
        Assert.Single(discovered);
    }

    [Fact]
    public void Filter_DenyWinsOverAllow()
    {
        var plugin = MakePlugin("blocked");
        var config = new PluginsConfig
        {
            Allow = ["blocked"],
            Deny = ["blocked"]
        };

        var result = PluginDiscovery.Filter([plugin], config);

        Assert.Empty(result);
    }

    [Fact]
    public void Filter_AllowRestrictsToNamedPlugins()
    {
        var alpha = MakePlugin("alpha");
        var beta = MakePlugin("beta");
        var config = new PluginsConfig { Allow = ["alpha"] };

        var result = PluginDiscovery.Filter([alpha, beta], config);

        Assert.Single(result);
        Assert.Equal("alpha", result[0].Manifest.Id);
    }

    [Fact]
    public void Filter_PerPluginEnabled_False_Excludes()
    {
        var plugin = MakePlugin("off-plugin");
        var config = new PluginsConfig
        {
            Entries = new(StringComparer.Ordinal)
            {
                ["off-plugin"] = new PluginEntryConfig { Enabled = false }
            }
        };

        var result = PluginDiscovery.Filter([plugin], config);

        Assert.Empty(result);
    }

    [Fact]
    public void Filter_SlotExclusivity()
    {
        var memA = MakePlugin("mem-a", kind: "memory");
        var memB = MakePlugin("mem-b", kind: "memory");

        var config = new PluginsConfig
        {
            Slots = new(StringComparer.Ordinal) { ["memory"] = "mem-b" }
        };

        var result = PluginDiscovery.Filter([memA, memB], config);

        Assert.Single(result);
        Assert.Equal("mem-b", result[0].Manifest.Id);
    }

    [Fact]
    public void Filter_SlotNone_ExcludesAll()
    {
        var memA = MakePlugin("mem-a", kind: "memory");

        var config = new PluginsConfig
        {
            Slots = new(StringComparer.Ordinal) { ["memory"] = "none" }
        };

        var result = PluginDiscovery.Filter([memA], config);

        Assert.Empty(result);
    }

    [Fact]
    public void Filter_EmptyAllow_PassesAll()
    {
        var a = MakePlugin("a");
        var b = MakePlugin("b");
        var config = new PluginsConfig(); // Allow = []

        var result = PluginDiscovery.Filter([a, b], config);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void DiscoverWithDiagnostics_PackageEntryOutsideRoot_IsRejected()
    {
        var pluginDir = Path.Combine(_tempDir, "packed-plugin");
        Directory.CreateDirectory(pluginDir);
        File.WriteAllText(
            Path.Combine(pluginDir, "package.json"),
            """
            {
              "name": "packed-plugin",
              "openclaw": {
                "extensions": ["../escape.js"]
              }
            }
            """);

        var config = new PluginsConfig
        {
            Load = new PluginLoadConfig { Paths = [pluginDir] }
        };

        var result = PluginDiscovery.DiscoverWithDiagnostics(config);

        Assert.Empty(result.Plugins);
        var report = Assert.Single(result.Reports);
        Assert.Contains(report.Diagnostics, diagnostic => diagnostic.Code == "entry_outside_root");
    }

    [Fact]
    public void DiscoverWithDiagnostics_ManifestEntrySymlinkOutsideRoot_IsRejected()
    {
        if (OperatingSystem.IsWindows())
            return;

        var pluginDir = Path.GetFullPath("symlink-plugin", _tempDir);
        var outsideDir = Path.GetFullPath("outside", _tempDir);
        Directory.CreateDirectory(pluginDir);
        Directory.CreateDirectory(outsideDir);
        File.WriteAllText(Path.Combine(pluginDir, "openclaw.plugin.json"), """{"id":"symlink-plugin"}""");
        File.WriteAllText(Path.Combine(outsideDir, "index.js"), "export default function() {}");
        File.CreateSymbolicLink(Path.Combine(pluginDir, "index.js"), Path.Combine(outsideDir, "index.js"));

        var config = new PluginsConfig
        {
            Load = new PluginLoadConfig { Paths = [pluginDir] }
        };

        var result = PluginDiscovery.DiscoverWithDiagnostics(config);

        Assert.Empty(result.Plugins);
        var report = Assert.Single(result.Reports);
        Assert.Contains(report.Diagnostics, diagnostic => diagnostic.Code == "entry_outside_root");
    }

    [Fact]
    public void DiscoverWithDiagnostics_ManifestEntryCyclicSymlink_DoesNotRecurseIndefinitely()
    {
        if (OperatingSystem.IsWindows())
            return;

        var pluginDir = Path.GetFullPath("cyclic-symlink-plugin", _tempDir);
        Directory.CreateDirectory(pluginDir);
        File.WriteAllText(Path.Combine(pluginDir, "openclaw.plugin.json"), """{"id":"cyclic-symlink-plugin"}""");
        var loop = Path.Combine(pluginDir, "index.js");
        File.CreateSymbolicLink(loop, loop);

        var config = new PluginsConfig
        {
            Load = new PluginLoadConfig { Paths = [pluginDir] }
        };

        var result = PluginDiscovery.DiscoverWithDiagnostics(config);

        Assert.Empty(result.Plugins);
    }

    [Fact]
    public void DiscoverWithDiagnostics_PackageEntryUnderSymlinkedParentOutsideRoot_IsRejected()
    {
        if (OperatingSystem.IsWindows())
            return;

        var pluginDir = Path.Combine(_tempDir, "packed-symlink-plugin");
        var outsideDir = Path.Combine(_tempDir, "outside-pack");
        Directory.CreateDirectory(pluginDir);
        Directory.CreateDirectory(outsideDir);
        File.WriteAllText(Path.Combine(outsideDir, "entry.js"), "export default function() {}");
        Directory.CreateSymbolicLink(Path.Combine(pluginDir, "linked"), outsideDir);
        File.WriteAllText(
            Path.Combine(pluginDir, "package.json"),
            """
            {
              "name": "packed-symlink-plugin",
              "openclaw": {
                "extensions": ["linked/entry.js"]
              }
            }
            """);

        var config = new PluginsConfig
        {
            Load = new PluginLoadConfig { Paths = [pluginDir] }
        };

        var result = PluginDiscovery.DiscoverWithDiagnostics(config);

        Assert.Empty(result.Plugins);
        var report = Assert.Single(result.Reports);
        Assert.Contains(report.Diagnostics, diagnostic => diagnostic.Code == "entry_outside_root");
    }

    private static DiscoveredPlugin MakePlugin(string id, string? kind = null)
        => new()
        {
            Manifest = new PluginManifest { Id = id, Kind = kind },
            RootPath = "/fake",
            EntryPath = "/fake/index.ts"
        };
}

public class BridgedPluginToolTests
{
    [Fact]
    public void Constructor_SetsProperties()
    {
        var reg = new PluginToolRegistration
        {
            Name = "greet",
            Description = "Greets the user",
            Parameters = JsonDocument.Parse("""{"type":"object","properties":{"name":{"type":"string"}}}""").RootElement
        };

        var tool = new BridgedPluginTool(null!, "test-plugin", reg);

        Assert.Equal("greet", tool.Name);
        Assert.Equal("Greets the user", tool.Description);
        Assert.Contains("\"name\"", tool.ParameterSchema);
        Assert.False(tool.Optional);
    }

    [Fact]
    public void Constructor_SetsOptional()
    {
        var reg = new PluginToolRegistration
        {
            Name = "opt-tool",
            Description = "Optional tool",
            Parameters = JsonDocument.Parse("{}").RootElement,
            Optional = true
        };

        var tool = new BridgedPluginTool(null!, "test-plugin", reg);

        Assert.True(tool.Optional);
    }

    [Fact]
    public void Constructor_PreservesOutputSchema()
    {
        var reg = new PluginToolRegistration
        {
            Name = "structured-tool",
            Description = "Structured tool",
            Parameters = JsonDocument.Parse("{}").RootElement,
            OutputSchema = JsonDocument.Parse("""{"type":"object","properties":{"value":{"type":"string"}}}""").RootElement
        };

        var tool = new BridgedPluginTool(null!, "test-plugin", reg);

        Assert.Contains("\"value\"", tool.OutputSchema, StringComparison.Ordinal);
    }
}

public class PluginHostTests
{
    [Fact]
    public async Task LoadAsync_DisabledConfig_ReturnsEmpty()
    {
        var config = new PluginsConfig { Enabled = false };
        var logger = new TestLogger();
        var host = new PluginHost(config, "/nonexistent/bridge.mjs", logger);

        var tools = await host.LoadAsync(null, TestContext.Current.CancellationToken);

        Assert.Empty(tools);
    }

    [Fact]
    public async Task LoadAsync_NoPluginsFound_ReturnsEmpty()
    {
        var config = new PluginsConfig { Enabled = true };
        var logger = new TestLogger();
        var host = new PluginHost(config, "/nonexistent/bridge.mjs", logger);

        // No workspace, no global extensions — should discover nothing
        var tools = await host.LoadAsync(null, TestContext.Current.CancellationToken);

        Assert.Empty(tools);
    }

    [Fact]
    public async Task LoadAsync_BundleMapsSkillsWithoutStartingBridgeCode()
    {
        var root = Path.Combine(Path.GetTempPath(), "openclaw-bundle-host-tests", Guid.NewGuid().ToString("n"));
        var skillDir = Path.Combine(root, "skills", "bundle-value");
        Directory.CreateDirectory(Path.Combine(root, ".codex-plugin"));
        Directory.CreateDirectory(skillDir);
        File.WriteAllText(Path.Combine(root, ".codex-plugin", "plugin.json"), "{\"name\":\"codex-value\"}");
        File.WriteAllText(
            Path.Combine(skillDir, "SKILL.md"),
            "---\nname: bundle-value\ndescription: Adds value\n---\nDeliver useful value.");

        try
        {
            var config = new PluginsConfig { Load = new PluginLoadConfig { Paths = [root] } };
            await using var host = new PluginHost(config, "/nonexistent/bridge.mjs", new TestLogger());

            var tools = await host.LoadAsync(null, TestContext.Current.CancellationToken);
            var report = Assert.Single(host.Reports);

            Assert.Empty(tools);
            Assert.True(report.Loaded);
            Assert.Equal(PluginFormats.Bundle, report.Origin);
            Assert.Equal("codex", report.BundleFormat);
            Assert.Contains(host.SkillRoots, path => path.EndsWith(Path.DirectorySeparatorChar + "skills", StringComparison.Ordinal));

            var skills = SkillLoader.LoadAll(
                new SkillsConfig
                {
                    Enabled = true,
                    Load = new SkillLoadConfig { IncludeBundled = false, IncludeManaged = false, IncludeWorkspace = false }
                },
                null,
                new TestLogger(),
                host.SkillRoots);
            Assert.Contains(skills, skill => skill.Name == "bundle-value");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>Minimal ILogger for testing without DI.</summary>
    private sealed class TestLogger : Microsoft.Extensions.Logging.ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => false;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
            TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        { }
    }
}
