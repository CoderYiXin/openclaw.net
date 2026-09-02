# Meta-Run Proposal Provenance Domain Closure Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Close the remaining P1-1 gap by adding durable proposal provenance lifecycle semantics, including explicit rollback and change transitions for meta-run proposals.

**Architecture:** Keep `meta-runs proposals` as the operator entrypoint, but move lifecycle semantics from ad-hoc review mapping to an explicit domain transition model persisted in `LearningProposal` metadata. Add additive response fields (`lifecycle`, `provenanceHistory`) so existing JSON consumers remain compatible. Enforce legal transitions and idempotency/conflict behavior with focused contract tests.

**Tech Stack:** .NET 10, C#, xUnit, System.Text.Json source generation, existing File/SQLite `ILearningProposalStore`.

---

## Scope Check

This is one subsystem (meta-run proposal lifecycle semantics under CLI/operator contracts). No split plan required.

## File Structure (What Changes and Why)

- Modify: `src/OpenClaw.Cli/SkillCommands.cs`
- Responsibility: Add lifecycle transition engine, new proposal actions (`rollback`, `change`), durable metadata persistence, and additive output fields.

- Modify: `src/OpenClaw.Core/Models/Session.cs`
- Responsibility: Add additive DTOs for lifecycle/provenance history in `meta-runs proposals show` JSON contracts, plus source-generation registrations.

- Modify: `src/OpenClaw.Tests/SkillCommandsTests.cs`
- Responsibility: Add TDD coverage for transition matrix, rollback/change idempotency/conflict rules, and additive JSON fields.

- Modify: `src/OpenClaw.Tests/CliProgramTests.cs`
- Responsibility: Lock help text contract for new proposal subcommands.

- Modify: `docs/zh-CN/opensquilla-meta-skill-migration.md`
- Responsibility: Mark P1-1 checklist item complete after implementation and tests.

- Modify: `docs/opensquilla-meta-skill-migration.md`
- Responsibility: Keep English migration status aligned with implemented lifecycle closure.

## Task 1: Define Failing Lifecycle Contract Tests

**Files:**
- Modify: `src/OpenClaw.Tests/SkillCommandsTests.cs` (around existing proposal tests at current `RunAsync_MetaRuns_Proposals_*` region)
- Test: `src/OpenClaw.Tests/SkillCommandsTests.cs`

- [ ] **Step 1: Write failing test for rollback transition from accepted proposal**

```csharp
[Fact]
public async Task RunAsync_MetaRuns_Proposals_Rollback_Json_AfterAccept_TransitionsToRolledBack()
{
    var root = CreateTempRoot();
    var previousWorkspace = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE");
    var previousOut = Console.Out;
    var previousError = Console.Error;

    try
    {
        var workspace = Path.Combine(root, "workspace");
        var memoryPath = Path.Combine(root, "memory");
        Directory.CreateDirectory(workspace);
        Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", workspace);

        await using (var store = new FileMemoryStore(memoryPath))
        {
            await store.SaveSessionAsync(new Session
            {
                Id = "sess-meta-proposals-rollback-json",
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

        var acceptExitCode = await SkillCommands.RunAsync([
            "meta-runs", "proposals", "accept", "sess-meta-proposals-rollback-json",
            "--storage", memoryPath,
            "--proposal", "meta-run:run-paused-001:paused",
            "--json"]);
        Assert.Equal(0, acceptExitCode);

        output.GetStringBuilder().Clear();
        error.GetStringBuilder().Clear();

        var rollbackExitCode = await SkillCommands.RunAsync([
            "meta-runs", "proposals", "rollback", "sess-meta-proposals-rollback-json",
            "--storage", memoryPath,
            "--proposal", "meta-run:run-paused-001:paused",
            "--reason", "operator rollback",
            "--json"]);

        Assert.Equal(0, rollbackExitCode);
        using var document = JsonDocument.Parse(output.ToString());
        Assert.Equal("rolled_back", document.RootElement.GetProperty("lifecycleStatus").GetString());
        Assert.False(document.RootElement.GetProperty("alreadyReviewed").GetBoolean());
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

- [ ] **Step 2: Run only the new rollback test and verify failure**

Run: `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~RunAsync_MetaRuns_Proposals_Rollback_Json_AfterAccept_TransitionsToRolledBack"`
Expected: FAIL with unknown `rollback` proposal action / missing lifecycle fields.

- [ ] **Step 3: Write failing test for change transition after rollback**

```csharp
[Fact]
public async Task RunAsync_MetaRuns_Proposals_Change_Json_AfterRollback_AllowsTargetReviewStatus()
{
    var root = CreateTempRoot();
    var previousWorkspace = Environment.GetEnvironmentVariable("OPENCLAW_WORKSPACE");
    var previousOut = Console.Out;
    var previousError = Console.Error;

    try
    {
        var workspace = Path.Combine(root, "workspace");
        var memoryPath = Path.Combine(root, "memory");
        Directory.CreateDirectory(workspace);
        Environment.SetEnvironmentVariable("OPENCLAW_WORKSPACE", workspace);

        await using (var store = new FileMemoryStore(memoryPath))
        {
            await store.SaveSessionAsync(new Session
            {
                Id = "sess-meta-proposals-change-json",
                ChannelId = "cli",
                SenderId = "tester",
                MetaRunHistory =
                {
                    new SessionMetaRunRecord
                    {
                        RunId = "run-failed-001",
                        SkillName = "meta-flow",
                        Status = "failed",
                        ErrorCode = "tool_failed"
                    }
                }
            }, CancellationToken.None);
        }

        using var output = new StringWriter();
        using var error = new StringWriter();
        Console.SetOut(output);
        Console.SetError(error);

        var dismissExitCode = await SkillCommands.RunAsync([
            "meta-runs", "proposals", "dismiss", "sess-meta-proposals-change-json",
            "--storage", memoryPath,
            "--proposal", "meta-run:run-failed-001:failed",
            "--reason", "operator reviewed",
            "--json"]);
        Assert.Equal(0, dismissExitCode);

        output.GetStringBuilder().Clear();
        error.GetStringBuilder().Clear();

        var rollbackExitCode = await SkillCommands.RunAsync([
            "meta-runs", "proposals", "rollback", "sess-meta-proposals-change-json",
            "--storage", memoryPath,
            "--proposal", "meta-run:run-failed-001:failed",
            "--reason", "undo dismiss",
            "--json"]);
        Assert.Equal(0, rollbackExitCode);

        output.GetStringBuilder().Clear();
        error.GetStringBuilder().Clear();

        var changeExitCode = await SkillCommands.RunAsync([
            "meta-runs", "proposals", "change", "sess-meta-proposals-change-json",
            "--storage", memoryPath,
            "--proposal", "meta-run:run-failed-001:failed",
            "--to", "accept",
            "--reason", "re-evaluated",
            "--json"]);

        Assert.Equal(0, changeExitCode);
        using var document = JsonDocument.Parse(output.ToString());
        Assert.Equal("accepted", document.RootElement.GetProperty("reviewStatus").GetString());
        Assert.Equal("approved", document.RootElement.GetProperty("lifecycleStatus").GetString());
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

- [ ] **Step 4: Run only the new change test and verify failure**

Run: `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~RunAsync_MetaRuns_Proposals_Change_Json_AfterRollback_AllowsTargetReviewStatus"`
Expected: FAIL with unknown `change` action / missing `--to` handling.

- [ ] **Step 5: Commit failing tests**

```bash
git add src/OpenClaw.Tests/SkillCommandsTests.cs
git commit -m "test(meta-runs): add failing lifecycle rollback/change proposal contract tests"
```

## Task 2: Implement Domain Lifecycle Transition Engine in SkillCommands

**Files:**
- Modify: `src/OpenClaw.Cli/SkillCommands.cs` (proposal command dispatcher and review mutation path)
- Test: `src/OpenClaw.Tests/SkillCommandsTests.cs`

- [ ] **Step 1: Add lifecycle action dispatch for proposals rollback/change**

```csharp
private static Task<int> HandleMetaRunProposalsAsync(string[] args)
{
    if (args.Length > 0 && string.Equals(args[0], "accept", StringComparison.OrdinalIgnoreCase))
        return ReviewMetaRunProposalAsync(args.Skip(1).ToArray(), MetaRunProposalReviewStatuses.Accepted, allowReason: false);
    if (args.Length > 0 && string.Equals(args[0], "dismiss", StringComparison.OrdinalIgnoreCase))
        return ReviewMetaRunProposalAsync(args.Skip(1).ToArray(), MetaRunProposalReviewStatuses.Dismissed, allowReason: true);
    if (args.Length > 0 && string.Equals(args[0], "rollback", StringComparison.OrdinalIgnoreCase))
        return RollbackMetaRunProposalAsync(args.Skip(1).ToArray());
    if (args.Length > 0 && string.Equals(args[0], "change", StringComparison.OrdinalIgnoreCase))
        return ChangeMetaRunProposalAsync(args.Skip(1).ToArray());
    if (args.Length > 0 && string.Equals(args[0], "show", StringComparison.OrdinalIgnoreCase))
        return ShowMetaRunProposalAsync(args.Skip(1).ToArray());

    return ListMetaRunProposalsAsync(args);
}
```

- [ ] **Step 2: Implement lifecycle status mapping helpers**

```csharp
private static bool CanRollbackLifecycle(string status)
    => string.Equals(status, LearningProposalStatus.Approved, StringComparison.OrdinalIgnoreCase)
       || string.Equals(status, LearningProposalStatus.Rejected, StringComparison.OrdinalIgnoreCase);

private static bool CanChangeLifecycle(string status)
    => string.Equals(status, LearningProposalStatus.RolledBack, StringComparison.OrdinalIgnoreCase);

private static string MapChangeTargetToLifecycleStatus(string to)
    => to switch
    {
        "accept" => LearningProposalStatus.Approved,
        "dismiss" => LearningProposalStatus.Rejected,
        _ => LearningProposalStatus.Pending
    };
```

- [ ] **Step 3: Add rollback command implementation with legal transition checks**

```csharp
private static async Task<int> RollbackMetaRunProposalAsync(string[] args)
{
    // parse session/proposal/reason/json/storage
    // load durable proposal via BuildMetaRunProposalDurableId
    // enforce: only approved/rejected can rollback; rolled_back idempotent; pending rejects
    // save LearningProposal copy with:
    // Status=LearningProposalStatus.RolledBack,
    // RolledBack=true,
    // RolledBackAtUtc=now,
    // RollbackReason=reason,
    // UpdatedAtUtc=now,
    // metadata lifecycle version + transition stamp
    // write MetaRunProposalReviewMutationResponse with lifecycleStatus
}
```

- [ ] **Step 4: Add change command implementation (`--to accept|dismiss`)**

```csharp
private static async Task<int> ChangeMetaRunProposalAsync(string[] args)
{
    // parse --to and validate
    // load durable proposal
    // enforce: only rolled_back can change
    // set Status=approved/rejected from --to
    // reset RolledBack=false, RolledBackAtUtc=null
    // set ReviewedAtUtc=now, ReviewNotes=reason (optional)
    // keep provenance snapshot additive and append transition metadata
    // return mutation response with lifecycleStatus and reviewStatus
}
```

- [ ] **Step 5: Run rollback/change test slice and verify pass**

Run: `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~RunAsync_MetaRuns_Proposals_Rollback_|FullyQualifiedName~RunAsync_MetaRuns_Proposals_Change_"`
Expected: PASS for new rollback/change contract tests.

- [ ] **Step 6: Commit lifecycle engine changes**

```bash
git add src/OpenClaw.Cli/SkillCommands.cs src/OpenClaw.Tests/SkillCommandsTests.cs
git commit -m "feat(meta-runs): add durable proposal rollback and change lifecycle actions"
```

## Task 3: Add Additive Lifecycle/History DTOs for Show JSON

**Files:**
- Modify: `src/OpenClaw.Core/Models/Session.cs`
- Modify: `src/OpenClaw.Cli/SkillCommands.cs`
- Test: `src/OpenClaw.Tests/SkillCommandsTests.cs`

- [ ] **Step 1: Add failing show-json test asserting lifecycle and provenanceHistory fields**

```csharp
[Fact]
public async Task RunAsync_MetaRuns_Proposals_Show_Json_AfterLifecycleChanges_IncludesLifecycleAndHistory()
{
    // setup session + accept + rollback + change sequence
    // run: meta-runs proposals show ... --json
    // assert proposal.lifecycle.status == "approved" (after change)
    // assert proposal.lifecycle.rolledBack == false
    // assert proposal.provenanceHistory has at least 3 transitions
}
```

- [ ] **Step 2: Run the new show-json lifecycle test and verify failure**

Run: `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~RunAsync_MetaRuns_Proposals_Show_Json_AfterLifecycleChanges_IncludesLifecycleAndHistory"`
Expected: FAIL because `lifecycle` / `provenanceHistory` fields do not exist.

- [ ] **Step 3: Add additive DTOs in Session model**

```csharp
public sealed class MetaRunProposalLifecycleDetail
{
    public required string Status { get; init; }
    public bool RolledBack { get; init; }
    public DateTimeOffset? ReviewedAtUtc { get; init; }
    public DateTimeOffset? RolledBackAtUtc { get; init; }
    public string? ReviewNotes { get; init; }
    public string? RollbackReason { get; init; }
}

public sealed class MetaRunProposalProvenanceTransition
{
    public required string Action { get; init; }
    public required string FromStatus { get; init; }
    public required string ToStatus { get; init; }
    public required DateTimeOffset ChangedAtUtc { get; init; }
    public string? Reason { get; init; }
}
```

- [ ] **Step 4: Wire `lifecycle` and `provenanceHistory` in proposal detail output**

```csharp
public sealed class MetaRunDerivedProposalDetail
{
    // existing fields...
    public MetaRunProposalLifecycleDetail? Lifecycle { get; init; }
    public MetaRunProposalProvenanceTransition[] ProvenanceHistory { get; init; } = [];
}
```

```csharp
var detail = ApplyReviewDetail(
    BuildDerivedProposalDetail(summary, run, session.MetaExecutionCheckpoint),
    review,
    BuildMetaRunProposalProvenanceDetail(durableProposal),
    BuildMetaRunProposalLifecycleDetail(durableProposal),
    BuildMetaRunProposalProvenanceHistory(durableProposal));
```

- [ ] **Step 5: Run lifecycle-show test and replay/reconstruct safety slice**

Run: `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~RunAsync_MetaRuns_Proposals_Show_Json_AfterLifecycleChanges_IncludesLifecycleAndHistory|FullyQualifiedName~RunAsync_MetaRuns_Replay|FullyQualifiedName~RunAsync_MetaRuns_Reconstruct"`
Expected: PASS; replay/reconstruct unchanged.

- [ ] **Step 6: Commit additive contract changes**

```bash
git add src/OpenClaw.Core/Models/Session.cs src/OpenClaw.Cli/SkillCommands.cs src/OpenClaw.Tests/SkillCommandsTests.cs
git commit -m "feat(meta-runs): expose proposal lifecycle and provenance transition history additively"
```

## Task 4: Update Help Contracts and Text UX

**Files:**
- Modify: `src/OpenClaw.Cli/SkillCommands.cs`
- Modify: `src/OpenClaw.Tests/CliProgramTests.cs`
- Test: `src/OpenClaw.Tests/SkillCommandsTests.cs`

- [ ] **Step 1: Add failing help test assertions for rollback/change commands**

```csharp
[Fact]
public async Task Main_Help_ListsSkillsMetaRunsProposalLifecycleCommands()
{
    var previousOut = Console.Out;
    using var output = new StringWriter();
    try
    {
        Console.SetOut(output);
        var exitCode = await OpenClaw.Cli.Program.Main(["--help"]);
        Assert.Equal(0, exitCode);
        Assert.Contains("openclaw skills meta-runs proposals rollback <session-id> --proposal <id> [--reason <text>] [--storage <path>] [--json]", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("openclaw skills meta-runs proposals change <session-id> --proposal <id> --to <accept|dismiss> [--reason <text>] [--storage <path>] [--json]", output.ToString(), StringComparison.Ordinal);
    }
    finally
    {
        Console.SetOut(previousOut);
    }
}
```

- [ ] **Step 2: Run help test and verify failure**

Run: `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~Main_Help_ListsSkillsMetaRunsProposalLifecycleCommands"`
Expected: FAIL until help text includes new commands.

- [ ] **Step 3: Update skill command help text and proposal detail text section**

```csharp
Console.WriteLine("openclaw skills meta-runs proposals rollback <session-id> --proposal <id> [--reason <text>] [--storage <path>] [--json]");
Console.WriteLine("openclaw skills meta-runs proposals change <session-id> --proposal <id> --to <accept|dismiss> [--reason <text>] [--storage <path>] [--json]");
```

```csharp
if (proposal.Lifecycle is not null)
{
    Console.WriteLine("Lifecycle:");
    Console.WriteLine($"Status: {proposal.Lifecycle.Status}");
    Console.WriteLine($"Rolled back: {(proposal.Lifecycle.RolledBack ? "yes" : "no")}");
}
```

- [ ] **Step 4: Run help + proposals slice and verify pass**

Run: `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~Main_Help_ListsSkillsMetaRunsProposal|FullyQualifiedName~RunAsync_MetaRuns_Proposals_"`
Expected: PASS for help contract and proposal lifecycle slices.

- [ ] **Step 5: Commit help/UX updates**

```bash
git add src/OpenClaw.Cli/SkillCommands.cs src/OpenClaw.Tests/CliProgramTests.cs src/OpenClaw.Tests/SkillCommandsTests.cs
git commit -m "chore(meta-runs): document proposal lifecycle commands and text diagnostics"
```

## Task 5: Docs Sync and Final Verification

**Files:**
- Modify: `docs/zh-CN/opensquilla-meta-skill-migration.md`
- Modify: `docs/opensquilla-meta-skill-migration.md`

- [ ] **Step 1: Update Chinese migration checklist (mark P1-1 closed)**

```markdown
- [x] proposal provenance 的域层闭环（snapshot + 回滚/变更）
```

- [ ] **Step 2: Update English migration note to reflect only P2 remaining**

```markdown
Remaining gap (P1-1 closed):
- [x] Durable proposal lifecycle migration to LearningProposal domain store.
- [x] Full proposal provenance and lifecycle semantics at the domain layer.
```

- [ ] **Step 3: Run full targeted verification matrix**

Run: `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~RunAsync_MetaRuns_|FullyQualifiedName~Main_Help_ListsSkillsMetaRuns|FullyQualifiedName~SkillExecStep_"`
Expected: PASS (no regressions in replay/reconstruct/proposals/help/skill_exec).

- [ ] **Step 4: Commit docs + verification updates**

```bash
git add docs/zh-CN/opensquilla-meta-skill-migration.md docs/opensquilla-meta-skill-migration.md
git commit -m "docs(meta-runs): close P1-1 provenance lifecycle gap after rollback/change implementation"
```

## Self-Review

### 1) Spec coverage

- Requirement: provenance snapshot closure at domain layer.
  - Covered by Task 2 and Task 3 (`LearningProposal` lifecycle transitions + additive lifecycle/history output).
- Requirement: rollback/change semantics.
  - Covered by Task 2 transition engine and Task 1 tests.
- Requirement: additive compatibility.
  - Covered by Task 3 additive DTO fields only, no removals.
- Requirement: operational surface clarity.
  - Covered by Task 4 help/text contracts.

No coverage gaps found.

### 2) Placeholder scan

- Checked for: TBD/TODO/implement later/add appropriate handling/similar to Task N.
- Result: none present.

### 3) Type consistency

- Transition targets use a single naming set: `accept|dismiss` (CLI) -> `approved|rejected` (lifecycle).
- Lifecycle fields use the same names across DTO/tests: `lifecycle`, `provenanceHistory`, `lifecycleStatus` mutation response field.
- Status constants align with `LearningProposalStatus` values including `rolled_back`.

No naming or signature conflicts found.
