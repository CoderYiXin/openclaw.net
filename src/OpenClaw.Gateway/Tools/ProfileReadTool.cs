using System.Text;
using System.Text.Json;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;

namespace OpenClaw.Gateway.Tools;

internal sealed class ProfileReadTool : IToolWithContext
{
    private readonly IUserProfileStore _profiles;

    public ProfileReadTool(IUserProfileStore profiles)
    {
        _profiles = profiles;
    }

    public string Name => "profile_read";
    public string Description => "Read the persisted user profile for the current or specified actor.";
    public string ParameterSchema => """
    {
      "type":"object",
      "properties":{
        "actor_id":{"type":"string"},
        "channel_id":{"type":"string"},
        "sender_id":{"type":"string"}
      }
    }
    """;

    public ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
        => ValueTask.FromResult("Error: profile_read requires execution context.");

    public async ValueTask<string> ExecuteAsync(string argumentsJson, ToolExecutionContext context, CancellationToken ct)
    {
        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
        var root = document.RootElement;
        var channelId = GetString(root, "channel_id") ?? context.Session.ChannelId;
        var senderId = GetString(root, "sender_id") ?? context.Session.SenderId;
        var actorId = GetString(root, "actor_id") ?? $"{channelId}:{senderId}";

        var profile = await _profiles.GetProfileAsync(actorId, ct);
        if (profile is null)
            return $"No profile found for actor {actorId}.";

        var sb = new StringBuilder();
        sb.AppendLine($"actor: {profile.ActorId}");
        if (!string.IsNullOrWhiteSpace(profile.Summary))
            sb.AppendLine($"summary: {profile.Summary}");
        if (!string.IsNullOrWhiteSpace(profile.Tone))
            sb.AppendLine($"tone: {profile.Tone}");
        if (profile.Preferences.Count > 0)
            sb.AppendLine($"preferences: {string.Join(", ", profile.Preferences)}");
        if (profile.ActiveProjects.Count > 0)
            sb.AppendLine($"active_projects: {string.Join(", ", profile.ActiveProjects)}");
        if (profile.RecentIntents.Count > 0)
            sb.AppendLine($"recent_intents: {string.Join(", ", profile.RecentIntents)}");
        if (profile.Facts.Count > 0)
        {
            sb.AppendLine("facts:");
            foreach (var fact in profile.Facts.Take(8))
                sb.AppendLine($"- {fact.Key}: {fact.Value} ({fact.Confidence:0.00})");
        }

        return sb.ToString().TrimEnd();
    }

    private static string? GetString(JsonElement root, string propertyName)
        => root.TryGetProperty(propertyName, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;
}
