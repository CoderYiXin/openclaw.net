using OpenClaw.Core.Models;

namespace OpenClaw.Gateway;

internal static class AutomationRunStatusMapper
{
    public static AutomationRunState? NormalizeState(string automationId, AutomationRunState? state)
    {
        if (state is null)
            return null;

        var lifecycleState = string.IsNullOrWhiteSpace(state.LifecycleState)
            ? DeriveLifecycleState(state.Outcome)
            : state.LifecycleState;
        if (string.Equals(lifecycleState, AutomationLifecycleStates.Never, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(state.Outcome)
            && !string.Equals(state.Outcome, AutomationLifecycleStates.Never, StringComparison.OrdinalIgnoreCase))
        {
            lifecycleState = DeriveLifecycleState(state.Outcome);
        }

        var verificationStatus = string.IsNullOrWhiteSpace(state.VerificationStatus)
            ? DeriveVerificationStatus(automationId, state.Outcome)
            : state.VerificationStatus;
        if (string.Equals(verificationStatus, AutomationVerificationStatuses.NotRun, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(state.Outcome)
            && state.Outcome is not AutomationLifecycleStates.Never and not AutomationLifecycleStates.Queued and not AutomationLifecycleStates.Running and not AutomationLifecycleStates.Stuck)
        {
            verificationStatus = DeriveVerificationStatus(automationId, state.Outcome);
        }

        var signalSeverity = string.IsNullOrWhiteSpace(state.SignalSeverity)
            ? DeriveSignalSeverity(automationId, state.Outcome)
            : state.SignalSeverity;
        var healthState = string.IsNullOrWhiteSpace(state.HealthState) || string.Equals(state.HealthState, AutomationHealthStates.Unknown, StringComparison.OrdinalIgnoreCase)
            ? DeriveHealthState(lifecycleState, verificationStatus, state.QuarantinedAtUtc)
            : state.HealthState;

        var derivedOutcome = string.IsNullOrWhiteSpace(state.Outcome)
            ? DeriveOutcome(automationId, lifecycleState, verificationStatus, signalSeverity)
            : state.Outcome;

        if (string.Equals(derivedOutcome, state.Outcome, StringComparison.OrdinalIgnoreCase)
            && string.Equals(lifecycleState, state.LifecycleState, StringComparison.OrdinalIgnoreCase)
            && string.Equals(verificationStatus, state.VerificationStatus, StringComparison.OrdinalIgnoreCase)
            && string.Equals(signalSeverity, state.SignalSeverity, StringComparison.OrdinalIgnoreCase)
            && string.Equals(healthState, state.HealthState, StringComparison.OrdinalIgnoreCase))
        {
            return state;
        }

        return new AutomationRunState
        {
            AutomationId = state.AutomationId,
            Outcome = derivedOutcome,
            LifecycleState = lifecycleState,
            VerificationStatus = verificationStatus,
            HealthState = healthState,
            LastRunAtUtc = state.LastRunAtUtc,
            LastCompletedAtUtc = state.LastCompletedAtUtc,
            LastDeliveredAtUtc = state.LastDeliveredAtUtc,
            LastVerifiedSuccessAtUtc = state.LastVerifiedSuccessAtUtc,
            QuarantinedAtUtc = state.QuarantinedAtUtc,
            NextRetryAtUtc = state.NextRetryAtUtc,
            DeliverySuppressed = state.DeliverySuppressed,
            InputTokens = state.InputTokens,
            OutputTokens = state.OutputTokens,
            FailureStreak = state.FailureStreak,
            UnverifiedStreak = state.UnverifiedStreak,
            NextRetryAttempt = state.NextRetryAttempt,
            LastRunId = state.LastRunId,
            SessionId = state.SessionId,
            MessagePreview = state.MessagePreview,
            VerificationSummary = state.VerificationSummary,
            QuarantineReason = state.QuarantineReason,
            SignalSeverity = signalSeverity
        };
    }

    public static string DeriveOutcome(string automationId, string lifecycleState, string verificationStatus, string? signalSeverity = null)
    {
        if (string.Equals(lifecycleState, AutomationLifecycleStates.Queued, StringComparison.OrdinalIgnoreCase))
            return AutomationLifecycleStates.Queued;

        if (string.Equals(lifecycleState, AutomationLifecycleStates.Running, StringComparison.OrdinalIgnoreCase))
            return AutomationLifecycleStates.Running;

        if (string.Equals(lifecycleState, AutomationLifecycleStates.Stuck, StringComparison.OrdinalIgnoreCase))
            return AutomationLifecycleStates.Stuck;

        if (string.Equals(automationId, GatewayAutomationService.HeartbeatAutomationId, StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(signalSeverity, AutomationSignalSeverities.Alert, StringComparison.OrdinalIgnoreCase)
                && string.Equals(verificationStatus, AutomationVerificationStatuses.Verified, StringComparison.OrdinalIgnoreCase))
            {
                return "alert";
            }

            if (string.Equals(verificationStatus, AutomationVerificationStatuses.Failed, StringComparison.OrdinalIgnoreCase))
                return "error";

            if (string.Equals(verificationStatus, AutomationVerificationStatuses.Verified, StringComparison.OrdinalIgnoreCase))
                return "ok";
        }

        return verificationStatus switch
        {
            AutomationVerificationStatuses.Verified => "success",
            AutomationVerificationStatuses.NotVerified => AutomationVerificationStatuses.NotVerified,
            AutomationVerificationStatuses.Failed => AutomationVerificationStatuses.Failed,
            AutomationVerificationStatuses.Blocked => AutomationVerificationStatuses.Blocked,
            _ => AutomationLifecycleStates.Never
        };
    }

    public static string DeriveHealthState(string lifecycleState, string verificationStatus, DateTimeOffset? quarantinedAtUtc)
    {
        if (quarantinedAtUtc is not null)
            return AutomationHealthStates.Quarantined;

        if (string.Equals(lifecycleState, AutomationLifecycleStates.Stuck, StringComparison.OrdinalIgnoreCase))
            return AutomationHealthStates.Degraded;

        if (string.Equals(verificationStatus, AutomationVerificationStatuses.Verified, StringComparison.OrdinalIgnoreCase))
            return AutomationHealthStates.Healthy;

        if (verificationStatus is AutomationVerificationStatuses.NotVerified
            or AutomationVerificationStatuses.Failed
            or AutomationVerificationStatuses.Blocked)
        {
            return AutomationHealthStates.Degraded;
        }

        return AutomationHealthStates.Unknown;
    }

    public static AutomationRunState MapHeartbeatState(HeartbeatRunStatusDto status, AutomationRunState? overlay = null)
    {
        var verificationStatus = status.Outcome switch
        {
            "ok" => AutomationVerificationStatuses.Verified,
            "alert" => AutomationVerificationStatuses.Verified,
            "error" => AutomationVerificationStatuses.Failed,
            _ => overlay?.VerificationStatus ?? AutomationVerificationStatuses.NotRun
        };

        var signalSeverity = status.Outcome switch
        {
            "alert" => AutomationSignalSeverities.Alert,
            "error" => AutomationSignalSeverities.Error,
            _ => overlay?.SignalSeverity
        };

        var lifecycleState = overlay?.LifecycleState;
        if (string.IsNullOrWhiteSpace(lifecycleState) || string.Equals(lifecycleState, AutomationLifecycleStates.Never, StringComparison.OrdinalIgnoreCase))
            lifecycleState = string.Equals(status.Outcome, "never", StringComparison.OrdinalIgnoreCase) ? AutomationLifecycleStates.Never : AutomationLifecycleStates.Completed;

        var quarantinedAtUtc = overlay?.QuarantinedAtUtc;

        return new AutomationRunState
        {
            AutomationId = GatewayAutomationService.HeartbeatAutomationId,
            Outcome = DeriveOutcome(GatewayAutomationService.HeartbeatAutomationId, lifecycleState!, verificationStatus, signalSeverity),
            LifecycleState = lifecycleState!,
            VerificationStatus = verificationStatus,
            HealthState = DeriveHealthState(lifecycleState!, verificationStatus, quarantinedAtUtc),
            LastRunAtUtc = overlay?.LastRunAtUtc ?? status.LastRunAtUtc,
            LastCompletedAtUtc = string.Equals(lifecycleState, AutomationLifecycleStates.Completed, StringComparison.OrdinalIgnoreCase)
                ? (overlay?.LastCompletedAtUtc ?? status.LastRunAtUtc)
                : overlay?.LastCompletedAtUtc,
            LastDeliveredAtUtc = overlay?.LastDeliveredAtUtc ?? status.LastDeliveredAtUtc,
            LastVerifiedSuccessAtUtc = string.Equals(verificationStatus, AutomationVerificationStatuses.Verified, StringComparison.OrdinalIgnoreCase)
                ? (overlay?.LastVerifiedSuccessAtUtc ?? status.LastRunAtUtc)
                : overlay?.LastVerifiedSuccessAtUtc,
            QuarantinedAtUtc = quarantinedAtUtc,
            NextRetryAtUtc = null,
            DeliverySuppressed = status.DeliverySuppressed,
            InputTokens = status.InputTokens,
            OutputTokens = status.OutputTokens,
            FailureStreak = overlay?.FailureStreak ?? 0,
            UnverifiedStreak = overlay?.UnverifiedStreak ?? 0,
            NextRetryAttempt = null,
            LastRunId = overlay?.LastRunId,
            SessionId = status.SessionId,
            MessagePreview = status.MessagePreview,
            VerificationSummary = overlay?.VerificationSummary,
            QuarantineReason = overlay?.QuarantineReason,
            SignalSeverity = signalSeverity
        };
    }

    private static string DeriveLifecycleState(string? outcome)
        => outcome switch
        {
            AutomationLifecycleStates.Queued => AutomationLifecycleStates.Queued,
            AutomationLifecycleStates.Running => AutomationLifecycleStates.Running,
            AutomationLifecycleStates.Stuck => AutomationLifecycleStates.Stuck,
            null or "" or AutomationLifecycleStates.Never => AutomationLifecycleStates.Never,
            _ => AutomationLifecycleStates.Completed
        };

    private static string DeriveVerificationStatus(string automationId, string? outcome)
    {
        if (string.IsNullOrWhiteSpace(outcome))
            return AutomationVerificationStatuses.NotRun;

        if (string.Equals(automationId, GatewayAutomationService.HeartbeatAutomationId, StringComparison.OrdinalIgnoreCase))
        {
            return outcome switch
            {
                "ok" or "alert" => AutomationVerificationStatuses.Verified,
                "error" => AutomationVerificationStatuses.Failed,
                _ => AutomationVerificationStatuses.NotRun
            };
        }

        return outcome switch
        {
            "success" => AutomationVerificationStatuses.Verified,
            AutomationVerificationStatuses.NotVerified => AutomationVerificationStatuses.NotVerified,
            AutomationVerificationStatuses.Failed => AutomationVerificationStatuses.Failed,
            "error" => AutomationVerificationStatuses.Failed,
            AutomationVerificationStatuses.Blocked => AutomationVerificationStatuses.Blocked,
            _ => AutomationVerificationStatuses.NotRun
        };
    }

    private static string? DeriveSignalSeverity(string automationId, string? outcome)
    {
        if (!string.Equals(automationId, GatewayAutomationService.HeartbeatAutomationId, StringComparison.OrdinalIgnoreCase))
            return null;

        return outcome switch
        {
            "alert" => AutomationSignalSeverities.Alert,
            "error" => AutomationSignalSeverities.Error,
            _ => null
        };
    }
}
