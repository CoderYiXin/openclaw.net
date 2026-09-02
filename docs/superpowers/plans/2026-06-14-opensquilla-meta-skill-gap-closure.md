# OpenSquilla Meta Skill Gap Closure Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use `superpowers:subagent-driven-development` (recommended) or `superpowers:executing-plans` to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Close the three highest-priority gaps called out by the OpenSquilla migration review:

1. MetaSkill must not compose another MetaSkill.
2. `skip_if`-style clarify semantics need a concrete landing spot.
3. `risk` / `capabilities` metadata need a meta-specific gating loop.

**Architecture:** Keep the current meta runtime and authoring model intact, then add narrow validation and policy layers at the existing seams:

- resolve composition targets through the current runtime paths,
- reuse `MetaConditionEvaluator` for skip predicates,
- extend `MetaSkillPolicyConfig` / `SkillLoader.CheckRequirements` for risk and capability gates.

**Tech Stack:** .NET 10, C#, xUnit, existing OpenClaw meta-skill runtime, current `SkillLoader` / `SkillModels` / `AgentRuntime` / `MafAgentRuntime` surfaces.

## Scope Check

This plan deliberately stays inside the meta-skill pipeline. It does **not** expand unrelated authoring UX, proposal governance, or docs structure. The only documentation follow-up is to refresh the migration notes after the code lands.

The three tracks are related but independently shippable:

1. Hard ban nested meta composition.
2. Add `skip_if` clarify semantics on the clarify schema and runtime.
3. Add a meta-only risk/capability gate to skill eligibility.

## File Structure

### Files to modify

- Modify: `src/OpenClaw.Core/Skills/SkillModels.cs`

  - Add the new clarify/schema fields and meta policy knobs.
- Modify: `src/OpenClaw.Core/Skills/SkillLoader.cs`

  - Parse and gate the new metadata and validation rules.
- Modify: `src/OpenClaw.Core/Skills/Meta/MetaConditionEvaluator.cs`

  - Reuse for `skip_if` evaluation; extend only if the current expression surface needs a small helper.
- Modify: `src/OpenClaw.Agent/AgentRuntime.cs`

  - Enforce the meta nesting ban at the native runtime execution path.
  - Apply `skip_if` before prompting for clarify input.
- Modify: `src/OpenClaw.MicrosoftAgentFrameworkAdapter/MafAgentRuntime.cs`

  - Mirror the same nested-meta and `skip_if` behavior in the MAF adapter.
- Modify: `src/OpenClaw.Core/Skills/MetaInvokeTool.cs`

  - Keep direct meta invocation aligned with the new meta-only policy.
- Modify: `src/OpenClaw.Tests/SkillTests.cs`

  - Add loader and parser tests for nested meta composition and `skip_if` frontmatter.
- Modify: `src/OpenClaw.Tests/MetaCoreServicesTests.cs`

  - Add unit tests for `MetaConditionEvaluator` / clarify predicate behavior.
- Modify: `src/OpenClaw.Tests/AgentRuntimeTests.cs`

  - Add native runtime tests for nested-meta rejection and clarify skipping.
- Modify: `src/OpenClaw.Tests/MafAdapterTests.cs`

  - Add MAF parity tests for nested-meta rejection and clarify skipping.
- Modify: `docs/opensquilla-meta-skill-migration.md`

  - Sync the English migration note after the behavior lands.
- Modify: `docs/zh-CN/opensquilla-meta-skill-migration.md`

  - Sync the Chinese migration note after the behavior lands.

### Validation commands

- Meta loader / parser slice:
  - `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~SkillTests|FullyQualifiedName~MetaCoreServicesTests"`
- Native runtime slice:
  - `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~AgentRuntimeTests"`
- MAF runtime slice:
  - `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~MafAdapterTests"`
- Full meta regression slice:
  - `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~SkillTests|FullyQualifiedName~MetaCoreServicesTests|FullyQualifiedName~AgentRuntimeTests|FullyQualifiedName~MafAdapterTests"`

---

## Task 1: Hard Ban MetaSkill Composing MetaSkill

**Files:**

- Modify: `src/OpenClaw.Core/Skills/SkillModels.cs`
- Modify: `src/OpenClaw.Core/Skills/SkillLoader.cs`
- Modify: `src/OpenClaw.Core/Skills/MetaInvokeTool.cs`
- Modify: `src/OpenClaw.Agent/AgentRuntime.cs`
- Modify: `src/OpenClaw.MicrosoftAgentFrameworkAdapter/MafAgentRuntime.cs`
- Test: `src/OpenClaw.Tests/SkillTests.cs`
- Test: `src/OpenClaw.Tests/AgentRuntimeTests.cs`
- Test: `src/OpenClaw.Tests/MafAdapterTests.cs`

- [x] **Step 1: Write failing tests for nested meta composition rejection**

Add tests that build a parent meta skill whose composition targets a child skill with `kind: meta`, then assert the load or execution path rejects it with a stable error code. Add both native and MAF coverage so the ban is enforced consistently.

Suggested test shape:

```csharp
[Fact]
public void LoadAll_RejectsNestedMetaComposition()
{
    // parent meta skill references a child meta skill through a composition step
    // assert parse/load failure with a dedicated error code
}
```

```csharp
[Fact]
public async Task ExecuteMetaSkillAsync_RejectsNestedMetaTarget()
{
    // assert the runtime stops before dispatching a meta step to another meta skill
}
```

- [x] **Step 2: Run the new tests once to confirm the failure mode exists today**

Run the narrow slice for the new tests and confirm it fails for the expected reason, not for an unrelated parse issue.

- [x] **Step 3: Add the hard ban in the runtime resolution path**

Implement the check where the target skill is actually resolved, not only where the composition is parsed. The guard should fail fast whenever a meta step tries to hand off to another `SkillKind.Meta` definition.

Use the existing runtime path so the rule applies to both direct invocation and composed execution, and keep the error stable enough for CLI/tests to assert.

- [x] **Step 4: Re-run the nested-meta tests and verify green**

Expected outcome:

- a nested meta target is rejected deterministically,
- non-meta `skill` steps continue to work,
- native and MAF runtimes stay aligned.

---

## Task 2: Land `skip_if` Clarify Semantics on the Existing Clarify Schema

**Files:**

- Modify: `src/OpenClaw.Core/Skills/SkillModels.cs`
- Modify: `src/OpenClaw.Core/Skills/SkillLoader.cs`
- Modify: `src/OpenClaw.Core/Skills/Meta/MetaConditionEvaluator.cs`
- Modify: `src/OpenClaw.Agent/AgentRuntime.cs`
- Modify: `src/OpenClaw.MicrosoftAgentFrameworkAdapter/MafAgentRuntime.cs`
- Test: `src/OpenClaw.Tests/SkillTests.cs`
- Test: `src/OpenClaw.Tests/MetaCoreServicesTests.cs`
- Test: `src/OpenClaw.Tests/AgentRuntimeTests.cs`
- Test: `src/OpenClaw.Tests/MafAdapterTests.cs`

- [x] **Step 1: Write failing tests for `skip_if` parsing and execution**

Add a clarify schema test that proves the frontmatter parser accepts a new `skip_if` field, then add runtime tests showing a clarify step is skipped when the predicate evaluates truthy.

Suggested shape:

```csharp
[Fact]
public void LoadAll_ParsesClarifySkipIfPredicate()
{
    // clarify schema contains skip_if
    // assert it is preserved on MetaClarifySchema
}
```

```csharp
[Fact]
public async Task ExecuteMetaSkillAsync_SkipIfSkipsClarifyPrompt()
{
    // context satisfies the predicate
    // assert no clarify prompt is emitted and the step continues
}
```

- [x] **Step 2: Run the clarify tests once to confirm the current gap**

The tests should fail because the schema has no `skip_if` field and the runtime does not branch on it yet.

- [x] **Step 3: Add `skip_if` to `MetaClarifySchema` and wire evaluation into both runtimes**

Add a single optional predicate field to the existing clarify schema in `SkillModels.cs`, parse it in `SkillLoader.cs`, then evaluate it through `MetaConditionEvaluator` using the current `MetaExecutionContext` before the runtime asks the user for input.

Keep the semantics conservative:

- when the predicate is truthy, skip the clarify interaction and continue the step path,
- when the predicate is falsy or missing, preserve the current clarify flow,
- keep the behavior identical across native and MAF runtimes.

- [x] **Step 4: Re-run the clarify tests and verify green**

Expected outcome:

- `skip_if` is preserved in parsed skill definitions,
- clarify prompting is bypassed when the predicate matches,
- both runtimes produce the same result shape.

---

## Task 3: Close the Meta-Specific Risk / Capability Gate

**Files:**

- Modify: `src/OpenClaw.Core/Skills/SkillModels.cs`
- Modify: `src/OpenClaw.Core/Skills/SkillLoader.cs`
- Test: `src/OpenClaw.Tests/SkillTests.cs`
- Test: `src/OpenClaw.Tests/MetaCoreServicesTests.cs`

- [x] **Step 1: Write failing tests for meta-policy filtering**

Add tests proving that meta skills with disallowed `risk` or missing required `capabilities` are filtered out by the meta policy, while allowed meta skills still load.

Suggested cases:

```csharp
[Fact]
public void LoadAll_FiltersMetaSkillByRiskGate()
{
    // meta skill declares a risk above the configured limit
    // assert it is skipped
}
```

```csharp
[Fact]
public void LoadAll_FiltersMetaSkillByCapabilityGate()
{
    // meta skill declares a missing capability
    // assert it is skipped
}
```

- [x] **Step 2: Run the policy tests to confirm the current implementation is permissive**

The current result should show that `risk` and `capabilities` are parsed but not yet used as a meta-only gate.

- [x] **Step 3: Extend `MetaSkillPolicyConfig` and wire the check into `SkillLoader.CheckRequirements`**

Add a small, explicit meta policy surface to `MetaSkillPolicyConfig` for the minimum risk level and required / allowed capabilities. Then evaluate it only for meta skills during eligibility filtering.

Keep the gate additive:

- standard skills should continue to load as before,
- meta skills should be filtered when they fail the policy,
- the policy should be visible in logs with a stable reason.

- [x] **Step 4: Re-run the policy tests and verify green**

Expected outcome:

- meta skills are no longer eligible by default when they violate the policy,
- the filter is deterministic and testable,
- the existing loader behavior for non-meta skills remains unchanged.

---

## Final Verification

- [x] Run the full targeted regression slice for `SkillTests`, `MetaCoreServicesTests`, `AgentRuntimeTests`, and `MafAdapterTests`.
- [x] Confirm the new tests fail before implementation and pass after implementation.
- [x] Confirm the new behavior does not weaken the current `disable_model_invocation` or existing meta skill resolution rules.
- [x] Refresh the English and Chinese migration notes so the documented status matches the shipped behavior.

## Execution Status (2026-06-14)

- Completed: Task 1 nested-meta hard ban in native runtime and MAF runtime.
- Completed: Task 2 `skip_if` clarify schema, runtime bypass, and parity tests.
- Completed: Task 3 meta risk/capability policy gate in `SkillLoader`.
- Completed: validation slices for `SkillTests`, `AgentRuntimeTests`, and `MafAdapterTests`.
- Completed: migration note sync in English and Chinese.

## Phase3 Failure Matrix Baseline (2026-06-14)

This matrix captures the current product-level E2E failure slices that now serve as the non-regression baseline.

| Failure type | E2E chain | Error-contract assertion | Non-drift assertion | Coverage status |
| --- | --- | --- | --- | --- |
| Authorization failure (`permission_denied`) | `create -> dismiss -> rollback -> change(denied) -> show -> change(success) -> show` | `status/command/errorCode/message` on denied response | After denied mutation, `lifecycle/audit/provenance` remain at the pre-denied rolled-back state | Complete |
| Conflict failure (dismissed -> accept) | `create -> dismiss -> accept(conflict) -> show` | `proposal_already_reviewed` + action-specific `command` | `show` remains rejected, audit transition stays `dismiss`, provenance remains single dismiss record | Complete |
| Conflict failure (approved -> dismiss) | `create -> accept -> dismiss(conflict) -> show` | `proposal_already_reviewed` + action-specific `command` | `show` remains approved, audit transition stays `accept`, provenance remains single accept record | Complete |
| Invalid transition (`invalid_lifecycle_transition`) | `create -> accept -> change(to=accept, invalid) -> show` | `invalid_lifecycle_transition` + `skills meta-runs proposals change` command | `show` remains approved, audit/provenance remain anchored to the original accept action | Complete |

Regression slice:

- `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~RunAsync_Phase3_E2E_"`

## Self-Review Checklist

- [x] The hard ban blocks nested meta composition in both runtime paths.
- [x] `skip_if` lands on the existing clarify schema instead of a parallel DSL.
- [x] Risk/capability gating is meta-specific and does not change standard skill eligibility.
- [x] Tests cover both native and MAF parity.
- [x] The change stays AOT-friendly and avoids new reflection-heavy dependencies.
