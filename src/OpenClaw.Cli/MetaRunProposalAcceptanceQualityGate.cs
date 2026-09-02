using OpenClaw.Core.Models;

namespace OpenClaw.Cli;

internal static class MetaRunProposalAcceptanceQualityGate
{
    public const string ProfileId = "opensquilla-authoring-v1";

    public static MetaRunProposalAcceptanceGateResult Evaluate(MetaRunDerivedProposalSummary proposal, Session session)
    {
        var checks = new List<MetaRunProposalAcceptanceGateCheck>();
        var run = session.MetaRunHistory.FirstOrDefault(item => string.Equals(item.RunId, proposal.RunId, StringComparison.Ordinal));

        AddCheck(checks, "runtime.run_present", run is not null);
        if (run is null)
            return new MetaRunProposalAcceptanceGateResult(ProfileId, checks);

        AddCheck(
            checks,
            "runtime.identity_present",
            !string.IsNullOrWhiteSpace(run.RunId) && !string.IsNullOrWhiteSpace(run.SkillName));

        AddCheck(
            checks,
            "trigger.proposal_status_matches_run",
            string.Equals(run.Status, proposal.Status, StringComparison.OrdinalIgnoreCase));

        if (run.StepResults.Count > 0)
        {
            AddCheck(
                checks,
                "runtime.step_shape_valid",
                !run.StepResults.Any(static step =>
                    string.IsNullOrWhiteSpace(step.Id)
                    || string.IsNullOrWhiteSpace(step.Kind)
                    || string.IsNullOrWhiteSpace(step.Status)));

            var hasDuplicateStepIds = run.StepResults
                .Select(static step => step.Id)
                .GroupBy(static id => id, StringComparer.Ordinal)
                .Any(static group => group.Count() > 1);
            AddCheck(checks, "runtime.step_id_unique", !hasDuplicateStepIds);
        }

        if (string.Equals(run.Status, "paused", StringComparison.OrdinalIgnoreCase))
        {
            var checkpoint = session.MetaExecutionCheckpoint;
            AddCheck(
                checks,
                "safety.paused_checkpoint_consistent",
                checkpoint is not null
                && string.Equals(checkpoint.SkillName, run.SkillName, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(checkpoint.PendingStepId));
        }

        if (string.Equals(run.Status, "failed", StringComparison.OrdinalIgnoreCase))
        {
            var hasFailureEvidence = !string.IsNullOrWhiteSpace(run.ErrorCode)
                || !string.IsNullOrWhiteSpace(run.Error)
                || run.StepResults.Any(static step => !string.IsNullOrWhiteSpace(step.FailureCode));
            AddCheck(checks, "runtime.failed_evidence_present", hasFailureEvidence);
        }

        return new MetaRunProposalAcceptanceGateResult(ProfileId, checks);
    }

    private static void AddCheck(List<MetaRunProposalAcceptanceGateCheck> checks, string id, bool passed)
        => checks.Add(new MetaRunProposalAcceptanceGateCheck(id, passed));
}

internal sealed record MetaRunProposalAcceptanceGateResult(string ProfileId, IReadOnlyList<MetaRunProposalAcceptanceGateCheck> Checks)
{
    public bool Passed => Checks.All(static check => check.Passed);

    public IReadOnlyList<string> FailedChecks => Checks
        .Where(static check => !check.Passed)
        .Select(static check => check.Id)
        .ToArray();
}

internal sealed record MetaRunProposalAcceptanceGateCheck(string Id, bool Passed);