using OpenClaw.Core.Models;
using OpenClaw.Core.Observability;
using OpenClaw.Gateway.Memory;
using OpenClaw.Gateway.Tools;
using MemPalace.KnowledgeGraph;
using Xunit;

namespace OpenClaw.Tests;

public sealed class MempalaceMemoryStoreTests : IAsyncLifetime
{
    private readonly string _storagePath = Path.Combine(Path.GetTempPath(), "openclaw-mempalace-tests", Guid.NewGuid().ToString("N"));

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_storagePath);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        try { Directory.Delete(_storagePath, recursive: true); } catch { }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Notes_RoundTripThroughMempalaceCollection()
    {
        await using var store = CreateStore();

        await store.SaveNoteAsync("project:demo:cats", "Cats prefer quiet sunny rooms.", CancellationToken.None);
        await store.SaveNoteAsync("project:demo:dogs", "Dogs enjoy daily walks.", CancellationToken.None);

        var loaded = await store.LoadNoteAsync("project:demo:cats", CancellationToken.None);
        var hits = await store.SearchNotesAsync("cats sunny", "project:demo:", 5, CancellationToken.None);
        var entries = await store.ListNotesAsync("project:demo:", 10, CancellationToken.None);

        Assert.Equal("Cats prefer quiet sunny rooms.", loaded);
        Assert.Contains(hits, hit => hit.Key == "project:demo:cats");
        Assert.Equal(
            ["project:demo:cats", "project:demo:dogs"],
            entries.Select(static entry => entry.Key).OrderBy(static key => key, StringComparer.Ordinal));
    }

    [Fact]
    public async Task SaveNote_RecordsTemporalKnowledgeGraphLocation()
    {
        await using var store = CreateStore();

        await store.SaveNoteAsync("project:demo:cats", "Cats prefer quiet sunny rooms.", CancellationToken.None);

        var triples = await store.KnowledgeGraph.QueryAsync(
            new TriplePattern(
                new EntityRef("memory", "project:demo:cats"),
                "stored-in",
                null!),
            ct: CancellationToken.None);

        var triple = Assert.Single(triples);
        Assert.Equal("drawer:cats", triple.Triple.Object.ToString());
    }

    [Fact]
    public async Task KnowledgeGraphTool_AddsAndQueriesTemporalTriples()
    {
        await using var store = CreateStore();
        var tool = new MempalaceKnowledgeGraphTool(store.KnowledgeGraph);

        var add = await tool.ExecuteAsync(
            """{"action":"add","subject":"agent:openclaw","predicate":"uses","object":"memory:mempalace"}""",
            CancellationToken.None);
        var query = await tool.ExecuteAsync(
            """{"action":"query","subject":"agent:openclaw","predicate":"uses"}""",
            CancellationToken.None);

        Assert.Contains("Added temporal triple", add, StringComparison.Ordinal);
        Assert.Contains("agent:openclaw uses memory:mempalace", query, StringComparison.Ordinal);
    }

    private MempalaceMemoryStore CreateStore()
    {
        var config = new GatewayConfig
        {
            Memory = new MemoryConfig
            {
                Provider = "mempalace",
                StoragePath = _storagePath,
                Mempalace = new MemoryMempalaceConfig
                {
                    BasePath = Path.Combine(_storagePath, "palace"),
                    SessionDbPath = Path.Combine(_storagePath, "sessions.db"),
                    KnowledgeGraphDbPath = Path.Combine(_storagePath, "kg.db"),
                    PalaceId = "test",
                    CollectionName = "memories",
                    EmbeddingDimensions = 64
                }
            }
        };

        return new MempalaceMemoryStore(config, new RuntimeMetrics());
    }
}
