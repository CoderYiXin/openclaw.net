namespace OpenClaw.Dashboard.Models;

public record PluginInfo(
    string PluginId,
    string Origin,
    string? BundleFormat,
    bool Loaded,
    bool Disabled,
    bool Quarantined,
    bool Reviewed,
    string TrustLevel,
    string CompatibilityStatus,
    int ErrorCount,
    int WarningCount,
    string DeclaredSurface,
    string? PendingReason,
    string? LastError,
    int RestartCount,
    int ToolCount,
    int ChannelCount,
    int ProviderCount)
{
    public bool Enabled => !Disabled && !Quarantined;
    public string Status => Quarantined ? "quarantined" : Disabled ? "disabled" : Loaded ? "loaded" : "not loaded";
    public string Detail => LastError ?? PendingReason ?? DeclaredSurface;
}

public record ApprovalPolicy(
    string? Id,
    string? ToolPattern,
    string? Policy,
    string? Description
);

public record DeadLetterItem(
    string? Id,
    string? WebhookUrl,
    string? Error,
    DateTime? FailedAt,
    int RetryCount
);
