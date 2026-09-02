# OpenSquilla Meta Product And Governance Closure Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Close the remaining OpenSquilla migration gaps by delivering a global meta-runs operator command surface, a stable bundled meta catalog surface, and a proposal acceptance quality gate with governance-grade audit metadata.

**Architecture:** Keep existing session-scoped commands fully backward compatible, then add additive global subcommands under the same meta-runs namespace. Reuse existing storage abstractions (`ISessionAdminStore` and `IMemoryStore`) to derive global run views without changing persistence schema. For governance closure, enforce quality checks in the proposal mutation path (`accept` and `change --to accept`) and emit machine-readable failures plus durable audit metadata.

**Tech Stack:** .NET 10, C#, existing OpenClaw CLI command pipeline, OpenClaw.Core memory abstractions, xUnit.

---

## Scope Check

This plan covers two subsystem tracks that are coupled in migration acceptance and can ship in one sequence:

1. Operator command-surface convergence (global run views + stable meta catalog).
2. Product and governance convergence (proposal acceptance quality gate + docs/contract alignment).

No runtime orchestration behavior is changed. No AOT-hostile dependency is introduced.

## File Structure

### Files to create

- Create: `src/OpenClaw.Cli/MetaRunsGlobalQuery.cs`
  - Purpose: parse and validate global meta-runs query options (`--page`, `--page-size`, filters, and pagination defaults) so command handlers stay small and testable.
- Create: `src/OpenClaw.Tests/SkillCommandsGlobalMetaRunsTests.cs`
  - Purpose: focused tests for `meta-runs list/show/steps/failures` global command contracts.
- Create: `src/OpenClaw.Tests/SkillCommandsMetaGovernanceTests.cs`
  - Purpose: focused tests for stable catalog and proposal acceptance quality gate behavior.

### Files to modify

- Modify: `src/OpenClaw.Cli/SkillCommands.cs`
  - Add `meta-runs list/show/steps/failures` handlers using `ISessionAdminStore.ListSessionsAsync` + per-session run aggregation.
  - Preserve current `meta-runs <session-id>` path untouched.
  - Add a stable bundled meta catalog switch under `skills catalog`.
  - Add proposal acceptance quality gate in `meta-runs proposals accept` and `meta-runs proposals change --to accept`.
- Modify: `src/OpenClaw.Core/Models/Session.cs`
  - Add additive response contracts for global meta-runs JSON payloads and include source-generation attributes.
- Modify: `src/OpenClaw.Tests/SkillCommandsTests.cs`
  - Keep minimal compatibility assertions here for legacy command behavior while moving new coverage to dedicated files.
- Modify: `docs/opensquilla-meta-skill-migration.md`
  - Update strict migration conclusions and acceptance table wording to reflect new global command and governance completion.
- Modify: `docs/zh-CN/opensquilla-meta-skill-migration.md`
  - Apply Chinese parity wording updates matching the English migration note.

### Validation commands

- Global meta-runs and catalog tests:
  - `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~SkillCommandsGlobalMetaRunsTests|FullyQualifiedName~SkillCommandsMetaGovernanceTests"`
- CLI compatibility and regression tests:
  - `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~SkillCommandsTests"`
- Full target slice:
  - `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~SkillCommandsGlobalMetaRunsTests|FullyQualifiedName~SkillCommandsMetaGovernanceTests|FullyQualifiedName~SkillCommandsTests"`

---

### Task 1: Add Global Meta-Runs Command Surface (`list/show/steps/failures`)

**Files:**
- Create: `src/OpenClaw.Cli/MetaRunsGlobalQuery.cs`
- Modify: `src/OpenClaw.Cli/SkillCommands.cs`
- Modify: `src/OpenClaw.Core/Models/Session.cs`
- Test: `src/OpenClaw.Tests/SkillCommandsGlobalMetaRunsTests.cs`

- [ ] **Step 1: Write failing tests for global command entry points and usage contracts**

```csharp
[Fact]
public async Task RunAsync_MetaRuns_List_Json_PrintsGlobalRunPage()
{
    // Arrange: two sessions with meta run history in file storage
    // Act: openclaw skills meta-runs list --storage <path> --json
    // Assert: payload contains items with sessionId/runId and pagination fields
}

[Fact]
public async Task RunAsync_MetaRuns_Show_Json_PrintsRequestedRunAcrossSessions()
{
    // Arrange: unique run id present in one session
    // Act: openclaw skills meta-runs show run-001 --storage <path> --json
    // Assert: returns run detail including owning session id
}

[Fact]
public async Task RunAsync_MetaRuns_Failures_Text_PrintsOnlyFailedRuns()
{
    // Arrange: mixed completed/failed meta runs
    // Act: openclaw skills meta-runs failures --storage <path>
    // Assert: failed status lines only
}
```

- [ ] **Step 2: Run the new test file and confirm expected failures**

Run: `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~SkillCommandsGlobalMetaRunsTests"`

Expected: FAIL with unknown `meta-runs list/show/steps/failures` subcommand behavior or missing JSON fields.

- [ ] **Step 3: Implement global query parsing and handlers with additive routing**

Add a dedicated query model:

```csharp
internal sealed record MetaRunsGlobalQuery(
    int Page,
    int PageSize,
    string? Search,
    string? ChannelId,
    string? SenderId,
    string? State,
    bool FailuresOnly,
    string? RunId,
    bool Json,
    bool Verbose,
    string? StoragePath);
```

Add additive routing before legacy session-id resolution:

```csharp
if (args.Length > 0 && string.Equals(args[0], "list", StringComparison.OrdinalIgnoreCase))
    return await ListMetaRunsGlobalAsync(args.Skip(1).ToArray());
if (args.Length > 0 && string.Equals(args[0], "show", StringComparison.OrdinalIgnoreCase))
    return await ShowMetaRunGlobalAsync(args.Skip(1).ToArray());
if (args.Length > 0 && string.Equals(args[0], "steps", StringComparison.OrdinalIgnoreCase))
    return await ShowMetaRunStepsGlobalAsync(args.Skip(1).ToArray());
if (args.Length > 0 && string.Equals(args[0], "failures", StringComparison.OrdinalIgnoreCase))
    return await ListMetaRunFailuresGlobalAsync(args.Skip(1).ToArray());
```

Aggregate across sessions using existing store abstractions:

```csharp
var admin = EnsureSessionAdminStore(store);
var sessions = await admin.ListSessionsAsync(page, pageSize, BuildSessionListQuery(query), CancellationToken.None);
foreach (var s in sessions.Items)
{
    var session = await store.GetSessionAsync(s.Id, CancellationToken.None);
    if (session is null) continue;
    foreach (var run in session.MetaRunHistory)
    {
        // project to global summary rows with session id attached
    }
}
```

- [ ] **Step 4: Re-run tests and verify pass for global contract and legacy compatibility**

Run: `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~SkillCommandsGlobalMetaRunsTests|FullyQualifiedName~RunAsync_MetaRuns_"`

Expected: PASS for new global commands and existing `meta-runs <session-id>` tests.

- [ ] **Step 5: Commit task changes**

```bash
git add src/OpenClaw.Cli/MetaRunsGlobalQuery.cs src/OpenClaw.Cli/SkillCommands.cs src/OpenClaw.Core/Models/Session.cs src/OpenClaw.Tests/SkillCommandsGlobalMetaRunsTests.cs src/OpenClaw.Tests/SkillCommandsTests.cs
git commit -m "feat(cli): add global meta-runs list/show/steps/failures operator surface"
```

---

### Task 2: Deliver Stable Bundled Meta Catalog Surface

**Files:**
- Modify: `src/OpenClaw.Cli/SkillCommands.cs`
- Test: `src/OpenClaw.Tests/SkillCommandsMetaGovernanceTests.cs`
- Modify: `docs/opensquilla-meta-skill-migration.md`
- Modify: `docs/zh-CN/opensquilla-meta-skill-migration.md`

- [ ] **Step 1: Write failing tests for stable catalog mode**

```csharp
[Fact]
public async Task RunAsync_Catalog_StableMeta_Json_PrintsBundledMetaOnly()
{
    // Act: openclaw skills catalog --kind meta --stable --json
    // Assert: every item source == bundled and kind == meta
}

[Fact]
public async Task RunAsync_Catalog_StableMeta_Text_PrintsStableHeader()
{
    // Assert output starts with "Stable meta catalog" and includes bundled trust labels
}
```

- [ ] **Step 2: Run governance test slice and confirm failures**

Run: `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~SkillCommandsMetaGovernanceTests"`

Expected: FAIL because `--stable` is unsupported.

- [ ] **Step 3: Add additive `--stable` behavior to `skills catalog`**

Implement additive switch:

```csharp
var stable = args.Contains("--stable");
if (stable)
{
    var bundledDir = Path.Combine(AppContext.BaseDirectory, "skills");
    var bundled = SkillInspector.InspectInstalledRoot(bundledDir, SkillSource.Bundled)
        .Where(static item => item.Success && item.Definition is not null)
        .Select(CreateInspection)
        .Where(item => item.Definition.Kind == SkillKind.Meta)
        .OrderBy(static item => item.Definition.Name, StringComparer.OrdinalIgnoreCase)
        .ToArray();
    // print stable catalog payload/text
}
```

Keep existing `catalog` behavior unchanged when `--stable` is absent.

- [ ] **Step 4: Re-run catalog tests and verify pass**

Run: `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~Catalog|FullyQualifiedName~SkillCommandsMetaGovernanceTests"`

Expected: PASS for new stable catalog tests and existing catalog tests.

- [ ] **Step 5: Commit task changes**

```bash
git add src/OpenClaw.Cli/SkillCommands.cs src/OpenClaw.Tests/SkillCommandsMetaGovernanceTests.cs
git commit -m "feat(cli): add stable bundled meta catalog mode"
```

---

### Task 3: Add Pre-Accept Proposal Quality Gate For Governance Parity

**Files:**
- Modify: `src/OpenClaw.Cli/SkillCommands.cs`
- Modify: `src/OpenClaw.Core/Models/Session.cs`
- Test: `src/OpenClaw.Tests/SkillCommandsMetaGovernanceTests.cs`

- [ ] **Step 1: Write failing tests for accept/change quality gate**

```csharp
[Fact]
public async Task RunAsync_MetaRuns_Proposals_Accept_Json_LowQuality_ReturnsQualityGateError()
{
    // Arrange: derived proposal missing required evidence quality signals
    // Act: meta-runs proposals accept ... --json
    // Assert: errorCode == "proposal_accept_quality_gate_failed"
}

[Fact]
public async Task RunAsync_MetaRuns_Proposals_Change_ToAccept_LowQuality_ReturnsQualityGateError()
{
    // Arrange: rolledBack or pending proposal then change --to accept
    // Assert same gate and machine-readable blocking checks
}
```

- [ ] **Step 2: Run the governance test slice and confirm failures**

Run: `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~SkillCommandsMetaGovernanceTests"`

Expected: FAIL because accept path currently allows proposal mutation without acceptance-quality gating.

- [ ] **Step 3: Implement shared acceptance-quality evaluator and error schema**

Add evaluator and reuse across `accept` and `change --to accept`:

```csharp
private static ProposalAcceptanceQuality EvaluateProposalAcceptanceQuality(MetaRunDerivedProposalSummary proposal)
{
    var checks = new List<ProposalAcceptanceQualityCheck>
    {
        new("proposal_has_summary", !string.IsNullOrWhiteSpace(proposal.Summary), "Proposal summary must be present."),
        new("proposal_has_steps", proposal.StepCount > 0, "Proposal must include at least one step."),
        new("proposal_has_evidence", proposal.Evidence is not null, "Proposal must include evidence metadata.")
    };
    return ProposalAcceptanceQuality.FromChecks(checks);
}
```

Block mutation when failing:

```csharp
if (requiresAcceptanceGate && acceptanceQuality.IsBlocked)
{
    WriteProposalAcceptanceQualityError(asJson, commandName, acceptanceQuality);
    return 1;
}
```

Persist gate snapshot into durable metadata for audit:

```csharp
durableRecord.Metadata["proposal_accept_quality"] = JsonSerializer.Serialize(acceptanceQuality);
```

- [ ] **Step 4: Re-run tests and verify pass for allowed and denied transitions**

Run: `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~SkillCommandsMetaGovernanceTests|FullyQualifiedName~Proposals_Accept|FullyQualifiedName~Proposals_Change"`

Expected: PASS with both blocked low-quality paths and successful high-quality acceptance paths.

- [ ] **Step 5: Commit task changes**

```bash
git add src/OpenClaw.Cli/SkillCommands.cs src/OpenClaw.Core/Models/Session.cs src/OpenClaw.Tests/SkillCommandsMetaGovernanceTests.cs
git commit -m "feat(governance): enforce proposal acceptance quality gate for lifecycle mutations"
```

---

### Task 4: Tighten CLI Help, Migration Docs, And Acceptance Tables

**Files:**
- Modify: `src/OpenClaw.Cli/SkillCommands.cs`
- Modify: `docs/opensquilla-meta-skill-migration.md`
- Modify: `docs/zh-CN/opensquilla-meta-skill-migration.md`
- Test: `src/OpenClaw.Tests/SkillCommandsGlobalMetaRunsTests.cs`
- Test: `src/OpenClaw.Tests/SkillCommandsMetaGovernanceTests.cs`

- [ ] **Step 1: Write failing tests for help text and command discoverability**

```csharp
[Fact]
public async Task RunAsync_Help_IncludesGlobalMetaRunsAndStableCatalogFlags()
{
    // Assert help mentions:
    // meta-runs list/show/steps/failures
    // skills catalog --stable --kind meta
}
```

- [ ] **Step 2: Run help/doc-related tests and confirm failure**

Run: `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~Help_IncludesGlobalMetaRunsAndStableCatalogFlags|FullyQualifiedName~SkillCommandsGlobalMetaRunsTests|FullyQualifiedName~SkillCommandsMetaGovernanceTests"`

Expected: FAIL because help text does not yet list new flags/subcommands.

- [ ] **Step 3: Update help text and migration docs with strict parity wording**

Update CLI help examples to include:

```text
openclaw skills meta-runs list [--page <n>] [--page-size <n>] [--storage <path>] [--json]
openclaw skills meta-runs show <run-id> [--storage <path>] [--json]
openclaw skills meta-runs steps <run-id> [--storage <path>] [--json]
openclaw skills meta-runs failures [--page <n>] [--page-size <n>] [--storage <path>] [--json]
openclaw skills catalog --kind meta --stable [--json]
```

Update migration docs so these two statements are no longer marked as gaps:

- `运维/产品命令面为部分同构` -> upgraded to completion with explicit compatibility notes.
- `剩余缺口集中在产品化与治理同构` -> narrowed to optional follow-up items only.

- [ ] **Step 4: Run full target test slice and verify pass**

Run: `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~SkillCommandsGlobalMetaRunsTests|FullyQualifiedName~SkillCommandsMetaGovernanceTests|FullyQualifiedName~SkillCommandsTests"`

Expected: PASS.

- [ ] **Step 5: Commit task changes**

```bash
git add src/OpenClaw.Cli/SkillCommands.cs docs/opensquilla-meta-skill-migration.md docs/zh-CN/opensquilla-meta-skill-migration.md src/OpenClaw.Tests/SkillCommandsGlobalMetaRunsTests.cs src/OpenClaw.Tests/SkillCommandsMetaGovernanceTests.cs
git commit -m "docs(cli): align migration acceptance wording with global command and governance closure"
```

---

## Self-Review

### 1. Spec coverage

- Requirement: close command-surface partial equivalence.
  - Covered by Task 1 and Task 2.
- Requirement: close product and governance residual gaps.
  - Covered by Task 3 and Task 4.
- Requirement: strict migration conclusion and acceptance table phrasing updates.
  - Covered by Task 4.

No uncovered requirement remains.

### 2. Placeholder scan

- Checked for `TODO`, `TBD`, and deferred placeholders.
- All tasks include concrete file paths, code snippets, test commands, and commit commands.

### 3. Type consistency

- Global query model and handler naming are consistent across tasks.
- Acceptance quality gate naming is consistent (`ProposalAcceptanceQuality` and `proposal_accept_quality_gate_failed`).
- Command names are consistent (`meta-runs list/show/steps/failures`, `skills catalog --stable`).

## Execution Handoff

Plan complete and saved to `docs/superpowers/plans/2026-06-14-opensquilla-meta-product-governance-closure.md`. Two execution options:

1. Subagent-Driven (recommended) - I dispatch a fresh subagent per task, review between tasks, fast iteration
2. Inline Execution - Execute tasks in this session using executing-plans, batch execution with checkpoints

Which approach?
