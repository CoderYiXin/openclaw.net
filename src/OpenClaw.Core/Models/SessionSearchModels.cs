namespace OpenClaw.Core.Models;

public sealed class SessionSearchQuery
{
    public string Text { get; init; } = "";
    public string? ChannelId { get; init; }
    public string? SenderId { get; init; }
    public DateTimeOffset? FromUtc { get; init; }
    public DateTimeOffset? ToUtc { get; init; }
    public int Limit { get; init; } = 25;
    public int SnippetLength { get; init; } = 180;
}

public sealed class SessionSearchHit
{
    public required string SessionId { get; init; }
    public required string ChannelId { get; init; }
    public required string SenderId { get; init; }
    public required string Role { get; init; }
    public DateTimeOffset Timestamp { get; init; }
    public string Snippet { get; init; } = "";
    public float Score { get; init; }
}

public sealed class SessionSearchResult
{
    public required SessionSearchQuery Query { get; init; }
    public IReadOnlyList<SessionSearchHit> Items { get; init; } = [];
}
