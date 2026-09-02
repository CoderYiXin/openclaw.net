using System.Text.Json.Serialization;

namespace OpenClaw.Agent.Tools;

internal sealed record CreatorToolError(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("errorCode")] string ErrorCode,
    [property: JsonPropertyName("message")] string Message);

internal sealed record CreatorLintResult(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("passed")] bool Passed,
    [property: JsonPropertyName("failedGates")] string[] FailedGates,
    [property: JsonPropertyName("summary")] string Summary);

internal sealed record CreatorSmokeResult(
    [property: JsonPropertyName("G3")] CreatorSmokeGateResult G3,
    [property: JsonPropertyName("G4")] CreatorSmokeGateResult G4,
    [property: JsonPropertyName("degraded")] bool Degraded,
    [property: JsonPropertyName("summary")] string? Summary = null);

internal sealed record CreatorSmokeGateResult(
    [property: JsonPropertyName("passed")] bool Passed,
    [property: JsonPropertyName("positive_fixture")] string? PositiveFixture = null,
    [property: JsonPropertyName("negative_fixture")] string? NegativeFixture = null,
    [property: JsonPropertyName("classifier")] string? Classifier = null,
    [property: JsonPropertyName("degraded")] bool Degraded = false);

internal sealed record CreatorRuntimeE2EResult(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("passed")] bool Passed,
    [property: JsonPropertyName("winner")] string Winner,
    [property: JsonPropertyName("baseline_model")] string? BaselineModel,
    [property: JsonPropertyName("reason")] string? Reason,
    [property: JsonPropertyName("cases")] CreatorRuntimeE2ECase[] Cases);

internal sealed record CreatorRuntimeE2ECase(
    [property: JsonPropertyName("prompt")] string Prompt,
    [property: JsonPropertyName("winner")] string Winner,
    [property: JsonPropertyName("regression")] string Regression,
    [property: JsonPropertyName("reason")] string Reason);

internal sealed record CreatorPersistResult(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("proposal_id")] string? ProposalIdSnake,
    [property: JsonPropertyName("proposalId")] string ProposalId,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("auto_enable_eligible")] bool AutoEnableEligible = false,
    [property: JsonPropertyName("auto_enabled")] bool AutoEnabled = false);
