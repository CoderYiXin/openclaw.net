using Microsoft.Extensions.Logging.Abstractions;
using OpenClaw.Gateway.Mcp;
using Xunit;

namespace OpenClaw.Tests;

public sealed class McpConfigStoreTests
{
    [Fact]
    public async Task SaveAsync_PersistsConfigToLegacyMcpSubdirectory()
    {
        var root = CreateTempRoot();
        try
        {
            var store = new McpConfigStore(root, NullLogger<McpConfigStore>.Instance);

            await store.SaveAsync("""{"enabled":true,"servers":{}}""", TestContext.Current.CancellationToken);

            Assert.True(File.Exists(Path.Combine(root, "mcp", "mcp.json")));
        }
        finally
        {
            DeleteDirectoryIfPresent(root);
        }
    }

    [Fact]
    public async Task TryLoadServersAsync_WhenTopLevelEnabledFalse_ReturnsEmptyDictionary()
    {
        var root = CreateTempRoot();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "mcp"));
            await File.WriteAllTextAsync(
                Path.Combine(root, "mcp", "mcp.json"),
                """{"enabled":false,"servers":{"alpha":{"transport":"http","url":"http://127.0.0.1:1/mcp"}}}""",
                TestContext.Current.CancellationToken);
            var store = new McpConfigStore(root, NullLogger<McpConfigStore>.Instance);

            var servers = await store.TryLoadServersAsync(TestContext.Current.CancellationToken);

            Assert.NotNull(servers);
            Assert.Empty(servers);
        }
        finally
        {
            DeleteDirectoryIfPresent(root);
        }
    }

    private static string CreateTempRoot()
        => Path.Combine(Path.GetTempPath(), "openclaw-tests", Guid.NewGuid().ToString("N"));

    private static void DeleteDirectoryIfPresent(string path)
    {
        if (Directory.Exists(path))
            Directory.Delete(path, recursive: true);
    }
}