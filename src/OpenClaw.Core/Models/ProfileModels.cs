namespace OpenClaw.Core.Models;

public sealed class ProfilesConfig
{
    public bool Enabled { get; set; } = true;
    public bool InjectRecall { get; set; } = true;
    public int MaxRecallChars { get; set; } = 2_000;
}

public sealed class UserProfile
{
    public required string ActorId { get; init; }
    public required string ChannelId { get; init; }
    public required string SenderId { get; init; }
    public string Summary { get; init; } = "";
    public string Tone { get; init; } = "";
    public IReadOnlyList<UserProfileFact> Facts { get; init; } = [];
    public IReadOnlyList<string> Preferences { get; init; } = [];
    public IReadOnlyList<string> ActiveProjects { get; init; } = [];
    public IReadOnlyList<string> RecentIntents { get; init; } = [];
    public DateTimeOffset UpdatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}

public sealed class UserProfileFact
{
    public string Key { get; init; } = "";
    public string Value { get; init; } = "";
    public float Confidence { get; init; }
    public IReadOnlyList<string> SourceSessionIds { get; init; } = [];
    public DateTimeOffset UpdatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}
