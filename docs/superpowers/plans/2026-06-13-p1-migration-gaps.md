# P1 Migration Gaps Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Close the remaining P1 migration gaps by (1) moving derived meta-run proposal provenance/lifecycle into durable `LearningProposal` records and (2) adding `skill_exec` replay/inspection observability plus machine-readable stdin/replay failure contracts.

**Architecture:** Keep the existing CLI command surface stable and evolve internals behind it. Convert proposal review from a review-overlay model into a proposal-domain model keyed by derived proposal identity, then extend meta-run step persistence to retain safe `skill_exec` execution evidence for operator-facing replay/inspection output.

**Tech Stack:** .NET 10, xUnit, `System.Text.Json` source generation (`CoreJsonContext`), `FileFeatureStore`/`SqliteFeatureStore`, existing meta-runs CLI in `OpenClaw.Cli`.

---

## Scope Check

The spec has two partially independent tracks:

1. Proposal domain alignment (durable lifecycle + provenance semantics).
2. `skill_exec` replay/inspection observability and failure contracts.

They can ship as two atomic PR-sized commits and one final matrix-validation commit. This plan keeps both tracks in one document but enforces independent task boundaries and test slices.

## File Structure

### Files to modify

- Modify: `src/OpenClaw.Core/Models/LearningModels.cs`
  - Add a dedicated kind for durable derived meta-run proposals.
- Modify: `src/OpenClaw.Core/Models/Session.cs`
  - Add optional replay/inspection evidence fields for persisted meta step results and related constants.
- Modify: `src/OpenClaw.Agent/AgentRuntime.cs`
  - Persist `skill_exec` step evidence (safe args/cwd/stdin metadata) into `SessionMetaStepResult`.
- Modify: `src/OpenClaw.MicrosoftAgentFrameworkAdapter/MafAgentRuntime.cs`
  - Mirror the same `skill_exec` step evidence persistence in MAF runtime.
- Modify: `src/OpenClaw.Cli/SkillCommands.cs`
  - Replace review-overlay lifecycle with durable proposal lifecycle and enrich replay output with `skill_exec` evidence and machine-readable reasons.
- Modify: `src/OpenClaw.Tests/SkillCommandsTests.cs`
  - Add/adjust CLI contract tests for durable proposal lifecycle and `skill_exec` replay/inspection contracts.
- Modify: `src/OpenClaw.Tests/AgentRuntimeTests.cs`
  - Add runtime tests for persisted `skill_exec` step evidence.
- Modify: `src/OpenClaw.Tests/MafAdapterTests.cs`
  - Add MAF runtime tests for persisted `skill_exec` step evidence.
- Modify: `docs/opensquilla-meta-skill-migration.md`
  - Sync remaining P1 gaps with shipped behavior.
- Modify: `docs/zh-CN/opensquilla-meta-skill-migration.md`
  - Sync Chinese migration note with shipped behavior.

### Validation commands

- Proposal lifecycle slice:
  - `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~RunAsync_MetaRuns_Proposals_"`
- `skill_exec` runtime slice:
  - `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~SkillExecStep_"`
- Replay/reconstruct slice:
  - `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~RunAsync_MetaRuns_Replay|FullyQualifiedName~RunAsync_MetaRuns_Reconstruct"`
- P1 matrix slice:
  - `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~RunAsync_MetaRuns_|FullyQualifiedName~Main_Help_ListsSkillsMetaRuns"`

---

### Task 1: Move Derived Proposal Lifecycle Into Durable `LearningProposal`

**Files:**
- Modify: `src/OpenClaw.Core/Models/LearningModels.cs`
- Modify: `src/OpenClaw.Cli/SkillCommands.cs`
- Test: `src/OpenClaw.Tests/SkillCommandsTests.cs`

- [ ] **Step 1: Write failing tests for durable proposal lifecycle (no review-overlay dependency)**

```csharp
[Fact]
public async Task RunAsync_MetaRuns_Proposals_Accept_StoresDurableMetaRunProposalLifecycle()
{
    var root = CreateTempRoot();
    var memoryPath = Path.Combine(root, "memory");
    var workspace = Path.Combine(root, "workspace");
    Directory.CreateDirectory(workspace);

    var previousWorkspace = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE");
    var previousOut = Console.Out;
    var previousError = Console.Error;

    try
    {
        Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", workspace);

        await using (var memory = new FileMemoryStore(memoryPath))
        {
            await memory.SaveSessionAsync(new Session
            {
                Id = "sess-meta-proposal-durable",
                ChannelId = "cli",
                SenderId = "tester",
                MetaRunHistory =
                {
                    new SessionMetaRunRecord
                    {
                        RunId = "run-paused-001",
                        SkillName = "meta-flow",
                        Status = "paused"
                    }
                },
                MetaExecutionCheckpoint = new SessionMetaExecutionCheckpoint
                {
                    SkillName = "meta-flow",
                    PendingStepId = "ask_user"
                }
            }, CancellationToken.None);
        }

        using var output = new StringWriter();
        using var error = new StringWriter();
        Console.SetOut(output);
        Console.SetError(error);

        var exitCode = await SkillCommands.RunAsync([
            "meta-runs", "proposals", "accept", "sess-meta-proposal-durable",
            "--storage", memoryPath,
            "--proposal", "meta-run:run-paused-001:paused",
            "--json"]);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, error.ToString());

        await using var store = new FileFeatureStore(memoryPath);
        var durable = await store.GetProposalAsync("meta-run-proposal:sess-meta-proposal-durable:meta-run:run-paused-001:paused", CancellationToken.None);
        Assert.NotNull(durable);
        Assert.Equal(LearningProposalKind.MetaRunProposal, durable!.Kind);
        Assert.Equal(LearningProposalStatus.Approved, durable.Status);
        Assert.Equal("run-paused-001", durable.Metadata["meta_run_proposal_run_id"]);
        Assert.Equal("paused", durable.Metadata["meta_run_proposal_status"]);
    }
    finally
    {
        Console.SetOut(previousOut);
        Console.SetError(previousError);
        Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", previousWorkspace);
        Directory.Delete(root, recursive: true);
    }
}
```

- [ ] **Step 2: Run targeted tests and confirm failure**

Run:

`dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~RunAsync_MetaRuns_Proposals_Accept_StoresDurableMetaRunProposalLifecycle"`

Expected:

- FAIL because durable id/kind does not exist yet (`meta_run_proposal` not implemented).

- [ ] **Step 3: Add durable proposal kind and proposal-id strategy**

```csharp
// src/OpenClaw.Core/Models/LearningModels.cs
public static class LearningProposalKind
{
    public const string SkillDraft = "skill_draft";
    public const string ProfileUpdate = "profile_update";
    public const string AutomationSuggestion = "automation_suggestion";
    public const string HarnessChange = "harness_change";
    public const string MetaRunReview = "meta_run_review";
    public const string MetaRunProposal = "meta_run_proposal";
}
```

```csharp
// src/OpenClaw.Cli/SkillCommands.cs
private static string BuildMetaRunProposalDurableId(string sessionId, string proposalId)
    => $"meta-run-proposal:{sessionId}:{proposalId}";
```

- [ ] **Step 4: Implement upsert + lifecycle transitions on the durable proposal record**

```csharp
// src/OpenClaw.Cli/SkillCommands.cs (inside ReviewMetaRunProposalAsync)
var durableId = BuildMetaRunProposalDurableId(sessionId, proposalId);
var existing = await learningProposalStore.GetProposalAsync(durableId, CancellationToken.None);

var targetStatus = MapReviewStatusToLearningProposalStatus(targetStatusInput);
if (existing is not null)
{
    var existingReview = MapLearningProposalStatusToReviewStatus(existing.Status);
    if (string.Equals(existingReview, targetStatusInput, StringComparison.OrdinalIgnoreCase))
    {
        alreadyReviewed = true;
        record = ToMetaRunProposalReviewRecord(existing, sessionId, proposalId);
    }
    else if (!string.Equals(existing.Status, LearningProposalStatus.Pending, StringComparison.OrdinalIgnoreCase))
    {
        Console.Error.WriteLine($"Proposal '{proposalId}' in session '{sessionId}' is already reviewed as {existingReview}.");
        return 1;
    }
}

var now = DateTimeOffset.UtcNow;
var upsert = new LearningProposal
{
    Id = durableId,
    Kind = LearningProposalKind.MetaRunProposal,
    Status = targetStatus,
    Title = proposal.Title,
    Summary = proposal.Summary,
    SkillName = proposal.SkillName,
    Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["meta_run_proposal_session_id"] = sessionId,
        ["meta_run_proposal_id"] = proposalId,
        ["meta_run_proposal_run_id"] = proposal.RunId,
        ["meta_run_proposal_status"] = proposal.Status,
        ["meta_run_proposal_kind"] = proposal.Kind,
        ["meta_run_proposal_source"] = proposal.Source
    },
    SourceSessionIds = [sessionId],
    SourceTurnIds = [],
    ToolNames = [],
    ToolSequence = [],
    ToolObservations = [],
    FeedbackEvents = [],
    RiskLevel = LearningProposalRiskLevels.Low,
    Confidence = 1f,
    CreatedReason = "meta_run_proposal_lifecycle",
    CreatedAtUtc = existing?.CreatedAtUtc ?? now,
    UpdatedAtUtc = now,
    ReviewedAtUtc = now,
    ReviewNotes = allowReason ? reason : null
};

await learningProposalStore.SaveProposalAsync(upsert, CancellationToken.None);
```

- [ ] **Step 5: Re-run proposal tests and verify green**

Run:

`dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~RunAsync_MetaRuns_Proposals_"`

Expected:

- PASS for accept/dismiss/idempotency/conflict/list/show JSON contract tests.

- [ ] **Step 6: Commit Task 1 changes**

```bash
git add src/OpenClaw.Core/Models/LearningModels.cs src/OpenClaw.Cli/SkillCommands.cs src/OpenClaw.Tests/SkillCommandsTests.cs
git commit -m "feat: persist meta-run proposal lifecycle in LearningProposal"
```

---

### Task 2: Persist `skill_exec` Replay/Inspection Evidence In Runtime History

**Files:**
- Modify: `src/OpenClaw.Core/Models/Session.cs`
- Modify: `src/OpenClaw.Agent/AgentRuntime.cs`
- Modify: `src/OpenClaw.MicrosoftAgentFrameworkAdapter/MafAgentRuntime.cs`
- Test: `src/OpenClaw.Tests/AgentRuntimeTests.cs`
- Test: `src/OpenClaw.Tests/MafAdapterTests.cs`

- [ ] **Step 1: Add failing runtime tests for persisted `skill_exec` evidence**

```csharp
[Fact]
public async Task ExecuteMetaSkillAsync_SkillExecStep_WithStdin_PersistsReplayEvidence()
{
    var result = await InvokeMetaSkillAsync(agent, session, "meta-flow", "incident-stdin", CancellationToken.None);
    Assert.Equal("stdin:incident-stdin", result.Trim());

    var run = Assert.Single(session.MetaRunHistory);
    var step = Assert.Single(run.StepResults);

    Assert.Equal("skill_exec", step.Kind);
    Assert.True(step.ExecutionEvidence is not null);
    Assert.Equal("stdin", step.ExecutionEvidence!.InputMode);
    Assert.True(step.ExecutionEvidence.StdinBytes > 0);
    Assert.Contains("echo-stdin.ps1", step.ExecutionEvidence.CommandPreview, StringComparison.Ordinal);
}
```

- [ ] **Step 2: Run the runtime slices and verify failure**

Run:

`dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~SkillExecStep_WithStdin_PersistsReplayEvidence"`

Expected:

- FAIL because `SessionMetaStepResult` does not yet persist structured execution evidence.

- [ ] **Step 3: Extend persisted session model for step-level execution evidence**

```csharp
// src/OpenClaw.Core/Models/Session.cs
public sealed class SessionMetaStepExecutionEvidence
{
    public string CommandPreview { get; init; } = "";
    public string InputMode { get; init; } = "none";
    public int StdinBytes { get; init; }
    public string ParseMode { get; init; } = "text";
}

public sealed class SessionMetaStepResult
{
    public required string Id { get; init; }
    public required string Kind { get; init; }
    public required string Status { get; init; }
    public string? FailureCode { get; init; }
    public double DurationMs { get; init; }
    public bool Continued { get; init; }
    public SessionMetaStepExecutionEvidence? ExecutionEvidence { get; init; }
}
```

```csharp
// src/OpenClaw.Core/Models/Session.cs (CoreJsonContext)
[JsonSerializable(typeof(SessionMetaStepExecutionEvidence))]
```

- [ ] **Step 4: Capture `skill_exec` evidence when appending run/checkpoint history**

```csharp
// src/OpenClaw.Agent/AgentRuntime.cs and src/OpenClaw.MicrosoftAgentFrameworkAdapter/MafAgentRuntime.cs
private static SessionMetaStepExecutionEvidence? BuildStepExecutionEvidence(
    string stepKind,
    IReadOnlyList<string>? renderedArgs,
    string? renderedCwd,
    string? renderedStdin,
    string parseMode)
{
    if (!string.Equals(stepKind, "skill_exec", StringComparison.OrdinalIgnoreCase))
        return null;

    var commandPreview = renderedArgs is null || renderedArgs.Count == 0
        ? string.Empty
        : string.Join(" ", renderedArgs.Take(4));

    return new SessionMetaStepExecutionEvidence
    {
        CommandPreview = commandPreview,
        InputMode = string.IsNullOrEmpty(renderedStdin) ? "args" : "stdin",
        StdinBytes = string.IsNullOrEmpty(renderedStdin) ? 0 : System.Text.Encoding.UTF8.GetByteCount(renderedStdin),
        ParseMode = string.IsNullOrWhiteSpace(parseMode) ? "text" : parseMode
    };
}
```

Then pass the evidence into each `new SessionMetaStepResult { ... }` mapping path for run history and checkpoint persistence.

- [ ] **Step 5: Re-run runtime tests and verify pass**

Run:

`dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~SkillExecStep_"`

Expected:

- PASS for both AgentRuntime and MAF `skill_exec` slices, including stdin success and evidence persistence.

- [ ] **Step 6: Commit Task 2 changes**

```bash
git add src/OpenClaw.Core/Models/Session.cs src/OpenClaw.Agent/AgentRuntime.cs src/OpenClaw.MicrosoftAgentFrameworkAdapter/MafAgentRuntime.cs src/OpenClaw.Tests/AgentRuntimeTests.cs src/OpenClaw.Tests/MafAdapterTests.cs
git commit -m "feat: persist skill_exec replay evidence in meta run history"
```

---

### Task 3: Expose `skill_exec` Replay/Inspection Contracts In CLI

**Files:**
- Modify: `src/OpenClaw.Cli/SkillCommands.cs`
- Test: `src/OpenClaw.Tests/SkillCommandsTests.cs`

- [ ] **Step 1: Add failing CLI tests for machine-readable replay evidence and reasons**

```csharp
[Fact]
public async Task RunAsync_MetaRuns_Reconstruct_Json_SkillExecStep_IncludesExecutionEvidenceInNotes()
{
    var replay = await InvokeReconstructJsonAsync(...);
    var timeline = replay.GetProperty("timeline");

    Assert.Equal("skill_exec", timeline[0].GetProperty("kind").GetString());
    Assert.Contains("input_mode=stdin", timeline[0].GetProperty("notes").GetString(), StringComparison.Ordinal);
    Assert.Contains("stdin_bytes=14", timeline[0].GetProperty("notes").GetString(), StringComparison.Ordinal);
}

[Fact]
public async Task RunAsync_MetaRuns_Replay_Json_SkillExecMissingInputs_ReportsMachineReadableRequirement()
{
    var preview = await InvokeReplayPreviewJsonAsync(...);
    var requirements = preview.GetProperty("missingRequirements");

    Assert.Contains(requirements.EnumerateArray(), item =>
        string.Equals(item.GetProperty("name").GetString(), "skill_exec_inputs", StringComparison.Ordinal) &&
        string.Equals(item.GetProperty("kind").GetString(), "not_persisted", StringComparison.Ordinal) &&
        string.Equals(item.GetProperty("reason").GetString(), "skill_exec_inputs_not_persisted", StringComparison.Ordinal));
}
```

- [ ] **Step 2: Run replay/reconstruct test slice and confirm failure**

Run:

`dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~RunAsync_MetaRuns_Replay|FullyQualifiedName~RunAsync_MetaRuns_Reconstruct"`

Expected:

- FAIL because reconstruct timeline notes and skill-exec-specific replay requirement constants are not emitted.

- [ ] **Step 3: Add `skill_exec` replay requirement constants and emission logic**

```csharp
// src/OpenClaw.Core/Models/Session.cs
public static class MetaRunReplayRequirementNames
{
    public const string PromptContext = "prompt_context";
    public const string StepInputs = "step_inputs";
    public const string ToolArguments = "tool_arguments";
    public const string StepResults = "step_results";
    public const string SkillExecInputs = "skill_exec_inputs";
}

public static class MetaRunReplayRequirementReasons
{
    public const string PromptContextNotPersisted = "Persisted meta run history does not retain full prompt context needed for deterministic replay.";
    public const string StepInputsNotPersisted = "Persisted meta run history records step outcomes but not the step-level inputs needed to re-run the graph deterministically.";
    public const string ToolArgumentsNotPersisted = "Persisted meta run history does not retain the original tool arguments required to reconstruct tool calls.";
    public const string StepResultsNotRetained = "Persisted run metadata does not retain per-step execution traces.";
    public const string SkillExecInputsNotPersisted = "skill_exec step input payloads were not persisted in replay-safe form.";
}
```

```csharp
// src/OpenClaw.Cli/SkillCommands.cs (GetReplayMissingRequirements)
if (run.StepResults.Any(static step => string.Equals(step.Kind, "skill_exec", StringComparison.OrdinalIgnoreCase)
                                   && step.ExecutionEvidence is null))
{
    requirements.Add(new MetaRunReplayRequirementPreview
    {
        Name = MetaRunReplayRequirementNames.SkillExecInputs,
        Kind = MetaRunReplayRequirementKinds.NotPersisted,
        Reason = MetaRunReplayRequirementReasons.SkillExecInputsNotPersisted
    });
}
```

- [ ] **Step 4: Add reconstruct timeline note formatter for `skill_exec` evidence**

```csharp
// src/OpenClaw.Cli/SkillCommands.cs (BuildReplayResult)
Timeline = [.. run.StepResults.Select(static (step, index) => new MetaRunReplayTimelineItem
{
    Sequence = index + 1,
    StepId = step.Id,
    Kind = step.Kind,
    Status = step.Status,
    FailureCode = step.FailureCode,
    DurationMs = step.DurationMs,
    Continued = step.Continued,
    Source = MetaRunReplayTimelineSources.RunHistory,
    Notes = step.ExecutionEvidence is null
        ? null
        : $"input_mode={step.ExecutionEvidence.InputMode}; stdin_bytes={step.ExecutionEvidence.StdinBytes}; parse_mode={step.ExecutionEvidence.ParseMode}; command={step.ExecutionEvidence.CommandPreview}"
})],
```

- [ ] **Step 5: Re-run replay/reconstruct tests and verify pass**

Run:

`dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~RunAsync_MetaRuns_Replay|FullyQualifiedName~RunAsync_MetaRuns_Reconstruct"`

Expected:

- PASS with stable machine-readable contract strings.

- [ ] **Step 6: Commit Task 3 changes**

```bash
git add src/OpenClaw.Cli/SkillCommands.cs src/OpenClaw.Core/Models/Session.cs src/OpenClaw.Tests/SkillCommandsTests.cs
git commit -m "feat: add skill_exec replay inspection contracts"
```

---

### Task 4: Sync Migration Docs And Run P1 Acceptance Matrix

**Files:**
- Modify: `docs/opensquilla-meta-skill-migration.md`
- Modify: `docs/zh-CN/opensquilla-meta-skill-migration.md`

- [ ] **Step 1: Update English migration note with new status wording**

```markdown
- `proposals accept/dismiss` now mutate durable `LearningProposal` lifecycle for derived meta-run proposals (`meta_run_proposal`).
- `skill_exec` replay/inspection now includes persisted execution evidence and machine-readable replay requirement reasons.
- Remaining P1 gaps are narrowed to richer replay ergonomics and broader proposal-domain workflows beyond current CLI scope.
```

- [ ] **Step 2: Update Chinese migration note with the same status wording**

```markdown
- `proposals accept/dismiss` 已落到 durable `LearningProposal` 生命周期（`meta_run_proposal`）。
- `skill_exec` 的 replay/inspection 已输出持久化执行证据与 machine-readable 缺口原因。
- 当前 P1 剩余缺口收敛到更完整的 replay 体验和 proposal 域工作流扩展。
```

- [ ] **Step 3: Run P1 matrix tests**

Run:

`dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~RunAsync_MetaRuns_|FullyQualifiedName~Main_Help_ListsSkillsMetaRuns|FullyQualifiedName~SkillExecStep_"`

Expected:

- PASS, no regressions in command surface, JSON contracts, failure-path behavior, and runtime skill-exec slices.

- [ ] **Step 4: Commit docs + matrix signoff**

```bash
git add docs/opensquilla-meta-skill-migration.md docs/zh-CN/opensquilla-meta-skill-migration.md
git commit -m "docs: refresh P1 migration gap status after lifecycle and replay updates"
```

---

## Self-Review

### 1. Spec coverage

- Durable proposal lifecycle/provenance semantics: covered by Task 1.
- `skill_exec` inspection/replay observability: covered by Task 2 + Task 3.
- Machine-readable stdin/replay failure contracts: covered by Task 3 constants/tests.
- P1 acceptance matrix and docs sync: covered by Task 4.

No uncovered requirement remains.

### 2. Placeholder scan

- No `TODO`/`TBD` placeholders.
- Each code-changing step includes concrete code snippets.
- Each validation step includes exact command and expected result.

### 3. Type consistency

- Durable proposal kind name: `LearningProposalKind.MetaRunProposal` used consistently in tests and implementation.
- Replay requirement names/reasons use shared constants to avoid string drift.
- `SessionMetaStepResult.ExecutionEvidence` is referenced consistently by runtime persistence and CLI replay formatting.

No naming/signature conflicts detected in this plan.
