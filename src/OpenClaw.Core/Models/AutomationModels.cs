namespace OpenClaw.Core.Models;

public sealed class AutomationsConfig
{
    public bool Enabled { get; set; } = true;
    public string DefaultDeliveryChannelId { get; set; } = "cron";
    public int SuggestionThreshold { get; set; } = 3;
}

public sealed class AutomationDefinition
{
    public required string Id { get; init; }
    public string Name { get; init; } = "";
    public bool Enabled { get; init; } = true;
    public string Schedule { get; init; } = "@hourly";
    public string? Timezone { get; init; }
    public string Prompt { get; init; } = "";
    public string? ModelId { get; init; }
    public bool RunOnStartup { get; init; }
    public string? SessionId { get; init; }
    public string DeliveryChannelId { get; init; } = "cron";
    public string? DeliveryRecipientId { get; init; }
    public string? DeliverySubject { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = [];
    public bool IsDraft { get; init; }
    public string Source { get; init; } = "managed";
    public string? TemplateKey { get; init; }
    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}

public sealed class AutomationRunState
{
    public required string AutomationId { get; init; }
    public string Outcome { get; init; } = "never";
    public DateTimeOffset? LastRunAtUtc { get; init; }
    public DateTimeOffset? LastDeliveredAtUtc { get; init; }
    public bool DeliverySuppressed { get; init; }
    public long InputTokens { get; init; }
    public long OutputTokens { get; init; }
    public string? SessionId { get; init; }
    public string? MessagePreview { get; init; }
}

public sealed class AutomationTemplate
{
    public string Key { get; init; } = "";
    public string Label { get; init; } = "";
    public string Description { get; init; } = "";
    public string Category { get; init; } = "";
    public string SuggestedName { get; init; } = "";
    public string Schedule { get; init; } = "@daily";
    public string Prompt { get; init; } = "";
    public string DeliveryChannelId { get; init; } = "cron";
    public string? DeliverySubject { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = [];
    public bool Available { get; init; }
    public string? Reason { get; init; }
}

public sealed class AutomationTemplateListResponse
{
    public IReadOnlyList<AutomationTemplate> Items { get; init; } = [];
}

public sealed class AutomationValidationIssue
{
    public string Severity { get; init; } = "error";
    public string Code { get; init; } = "";
    public string Message { get; init; } = "";
}

public sealed class AutomationPreview
{
    public required AutomationDefinition Definition { get; init; }
    public IReadOnlyList<AutomationValidationIssue> Issues { get; init; } = [];
    public IReadOnlyList<AutomationTemplate> Templates { get; init; } = [];
    public string PromptPreview { get; init; } = "";
    public int EstimatedRunsPerMonth { get; init; }
}
