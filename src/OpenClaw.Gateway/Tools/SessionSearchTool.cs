using System.Text;
using System.Text.Json;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;

namespace OpenClaw.Gateway.Tools;

internal sealed class SessionSearchTool : IToolWithContext
{
    private readonly ISessionSearchStore _searchStore;

    public SessionSearchTool(ISessionSearchStore searchStore)
    {
        _searchStore = searchStore;
    }

    public string Name => "session_search";
    public string Description => "Search conversation history across sessions with snippets and relevance ranking.";
    public string ParameterSchema => """
    {
      "type":"object",
      "properties":{
        "text":{"type":"string"},
        "channel_id":{"type":"string"},
        "sender_id":{"type":"string"},
        "limit":{"type":"integer","minimum":1,"maximum":100},
        "snippet_length":{"type":"integer","minimum":40,"maximum":800}
      },
      "required":["text"]
    }
    """;

    public ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
        => ValueTask.FromResult("Error: session_search requires execution context.");

    public async ValueTask<string> ExecuteAsync(string argumentsJson, ToolExecutionContext context, CancellationToken ct)
    {
        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
        var root = document.RootElement;
        var text = GetString(root, "text");
        if (string.IsNullOrWhiteSpace(text))
            return "Error: text is required.";

        var result = await _searchStore.SearchSessionsAsync(new SessionSearchQuery
        {
            Text = text,
            ChannelId = GetString(root, "channel_id"),
            SenderId = GetString(root, "sender_id"),
            Limit = GetInt(root, "limit") ?? 10,
            SnippetLength = GetInt(root, "snippet_length") ?? 180
        }, ct);

        if (result.Items.Count == 0)
            return $"No session hits found for '{text}'.";

        var sb = new StringBuilder();
        foreach (var hit in result.Items)
            sb.AppendLine($"{hit.SessionId} {hit.Role} score={hit.Score:0.00} :: {hit.Snippet}");
        return sb.ToString().TrimEnd();
    }

    private static string? GetString(JsonElement root, string propertyName)
        => root.TryGetProperty(propertyName, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;

    private static int? GetInt(JsonElement root, string propertyName)
        => root.TryGetProperty(propertyName, out var element) && element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var value)
            ? value
            : null;
}
