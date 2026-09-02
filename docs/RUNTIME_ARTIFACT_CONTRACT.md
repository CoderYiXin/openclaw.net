# Runtime ArtifactContract + Projection Technical Document

## 1. Overview

**ArtifactContract** and **Projection** are two orthogonal extensions to the OpenClaw Skill system:
<!-- markdownlint-disable MD040 -->

| System | Responsibility | Trigger |
|--------|---------------|---------|
| **ArtifactContract** | Defines the whitelist of allowed artifact types for a Skill's multi-stage workflow; validates `emit_artifact` calls at runtime | When the LLM calls the `emit_artifact` tool |
| **Projection** | Matches the optimal Skill projection view based on user request signal scoring; dynamically tailors Skill instructions and injects them into the system prompt | Every user turn |

Both systems share the data model layer (`SkillArtifactContractModels.cs`), declare contract rules via JSON files under the `contracts/` directory, and use a null-safe design — silently degrading when no contract file exists, never blocking Skill loading or normal inference.

### 1.1 Key Files

| File | Project | Responsibility |
|------|---------|---------------|
| `src/OpenClaw.Core/Models/SkillArtifact.cs` | Core | `SkillArtifact` record — runtime artifact message carrier |
| `src/OpenClaw.Core/Models/SkillArtifactContractModels.cs` | Core | 4 Artifact contract classes + 14 Projection model classes |
| `src/OpenClaw.Core/Models/WebSocketEnvelopes.cs` | Core | `WsServerEnvelope` extensions + `SkillStageGateEvent` record |
| `src/OpenClaw.Core/Skills/SkillModels.cs` | Core | `SkillDefinition` extended properties |
| `src/OpenClaw.Core/Skills/SkillLoader.cs` | Core | `TryLoadArtifactContract` + `TryLoadProjectionContracts` |
| `src/OpenClaw.Core/Skills/LoadSkillTool.cs` | Core | ArtifactContract XML injection into prompt |
| `src/OpenClaw.Core/Skills/SkillPromptBuilder.cs` | Core | `BuildSkillBody` Projection overload |
| `src/OpenClaw.Core/Skills/SkillProjectionResolver.cs` | Core | Projection routing engine: Topic/View signal matching + scoring + JSON loading |
| `src/OpenClaw.Core/Skills/SkillProjectionArtifactTerms.cs` | Core | Explicit artifact keyword mappings for four projection views |
| `src/OpenClaw.Gateway/Tools/EmitArtifactTool.cs` | Gateway | `emit_artifact` tool implementation |
| `src/OpenClaw.Gateway/Tools/SkillArtifactRuntime.cs` | Gateway | Runtime validation engine + stage state machine |
| `src/OpenClaw.Agent/AgentRuntime.cs` | Agent | Per-turn Projection resolution + `ResolveSkillsForTurn` + `CloneSkill` |
| `src/OpenClaw.MicrosoftAgentFrameworkAdapter/MafAgentRuntime.cs` | MAF Adapter | Projection resolution equivalent to AgentRuntime |

---

## 2. Core Data Models

### 2.1 SkillArtifact (Runtime Message Carrier)

```csharp
// SkillArtifact.cs
public sealed record SkillArtifact
{
    public required string Kind { get; init; }          // "file" | "data"
    public string ArtifactType { get; init; } = "generic";
    public string? Label { get; init; }
    public string? SkillName { get; init; }
    public string? Stage { get; init; }
    public bool IsTerminal { get; init; }
    // File fields (kind = "file")
    public string? FileUrl { get; init; }
    public string? FileName { get; init; }
    public string? MimeType { get; init; }
    public long? FileSizeBytes { get; init; }
    // Data fields (kind = "data")
    public JsonElement? Data { get; init; }
    public string? DisplayHint { get; init; }
}
```

### 2.2 SkillArtifactContract (Top-Level Contract)

```csharp
// SkillArtifactContractModels.cs
public sealed class SkillArtifactContract
{
    public int SchemaVersion { get; init; } = 1;
    public IReadOnlyList<SkillArtifactStageContract> Stages { get; init; } = [];
}

public sealed class SkillArtifactStageContract
{
    public required string Name { get; init; }
    public string? Label { get; init; }
    public SkillArtifactStageGateContract? Gate { get; init; }
    public IReadOnlyList<SkillArtifactTypeContract> Artifacts { get; init; } = [];
}

public sealed class SkillArtifactStageGateContract
{
    public string? RequiresStage { get; init; }
}

public sealed class SkillArtifactTypeContract
{
    public required string Type { get; init; }
    public string? Label { get; init; }
    public string? Display { get; init; }
    public bool? Terminal { get; init; }
}
```

### 2.3 Stage Gate Model (SkillStageGateEvent)

```csharp
// WebSocketEnvelopes.cs
public sealed record SkillStageGateEvent
{
    public required string SkillName { get; init; }
    public required string CompletedStage { get; init; }
    public required string NextStage { get; init; }
    public required bool CanProceed { get; init; }
    public string? BlockedReason { get; init; }
}
```

### 2.4 Projection Models (14 Classes)

```text
SkillProjectionContractSet      — Bound projection contract set
SkillProjectionDiscovery        — Load diagnostics
SkillProjectionResolution       — Single resolution result
ProjectionContractIndex         — Index: Topics + Views + Scoring
ProjectionSelectionPolicy       — Selection policy
ProjectionTopicScoring          — Topic-level scoring config
ProjectionTargetViewScoring     — View-level scoring config
ProjectionScoreDimension        — Score dimension
ProjectionTopicSignals          — Topic signal keywords
ProjectionViewSignals           — View signal keywords
ProjectionTopicViewOverride     — Within-topic View weight override
ProjectionTopicViewBonus        — Bonus weight item
ProjectionTopicRecord           — Topic definition
ProjectionViewRecord            — View definition
ProjectionDocument              — Projection file content
ProjectionMappingPolicy         — Mapping policy
ProjectionPromptPayload         — Prompt constraint payload
ProjectionDeliveryArtifact      — Output artifact
```

---

## 3. Loading Mechanism

### 3.1 Disk Layout

```text
skills/<skill-name>/
├── SKILL.md
└── contracts/
    ├── artifacts.json                ← ArtifactContract
    └── projections/                  ← Projection index root
        └── <producer-name>/
            ├── contract-index.json   ← Index definition
            └── <view-path>.json      ← Concrete projection file
```

### 3.2 artifacts.json Example

```json
{
  "schemaVersion": 1,
  "stages": [
    {
      "name": "stage1_collect",
      "label": "Stage 1: Collection",
      "artifacts": [
        {
          "type": "progress_update",
          "label": "Progress",
          "display": "progress",
          "terminal": false
        },
        {
          "type": "collection_summary",
          "label": "Summary",
          "display": "tree",
          "terminal": true
        }
      ]
    },
    {
      "name": "stage2_analysis",
      "label": "Stage 2: Analysis",
      "gate": { "requiresStage": "stage1_collect" },
      "artifacts": [
        { "type": "analysis_result", "label": "Result", "display": "tree", "terminal": true }
      ]
    }
  ]
}
```

### 3.3 contract-index.json Example

```json
{
  "producer_skill": "ontology-extraction",
  "producer_priority": 10,
  "default_selection_policy": {
    "prefer_ready_only": true,
    "block_on_open_questions": false,
    "fallback_order_by_target_view": ["domain-model", "json-schema"]
  },
  "topic_scoring": {
    "clarify_when_score_gap_below": 2,
    "score_dimensions": [
      { "dimension": "primary_intent_match", "score": 5 },
      { "dimension": "explicit_artifact_bonus", "score": 4 }
    ],
    "topics": [
      {
        "domain_slug": "skill-loading",
        "primary_intent_signals": ["load skill", "skill loading"],
        "supporting_signals": ["skill"],
        "explicit_artifact_signals": ["skill definition"],
        "demote_when_competing_topic_signals": []
      }
    ]
  },
  "target_view_scoring": {
    "score_dimensions": [
      { "dimension": "explicit_output_match", "score": 5 },
      { "dimension": "strong_signal_match", "score": 3 }
    ],
    "views": [
      {
        "target_view": "json-schema",
        "explicit_output_signals": ["json schema", "schema file"],
        "strong_signals": ["schema", "structure"],
        "supporting_signals": [],
        "demote_when_competing_view_signals": []
      }
    ],
    "within_topic_overrides": [
      {
        "domain_slug": "skill-loading",
        "bonuses": [
          {
            "target_view": "json-schema",
            "when_request_signals": ["validate"],
            "score": 3
          }
        ]
      }
    ]
  },
  "topics": [
    {
      "domain_slug": "skill-loading",
      "default_target_view": "domain-model",
      "views": [
        { "target_view": "domain-model", "status": "READY", "path": "domain-model/projection.json" },
        { "target_view": "json-schema", "status": "DRAFT", "path": "json-schema/projection.json" }
      ]
    }
  ]
}
```

### 3.4 projection.json Example

```json
{
  "mapping_policy": {
    "unresolved_item_policy": "allow_unmapped_terms",
    "prompt_assumption_policy": "disallow_unmapped_terms"
  },
  "prompt_projection": {
    "allowed_terms": ["skill_config", "model_provider"],
    "forbidden_assumptions": ["API key is embedded"],
    "required_clarifications": ["Identify the target deployment provider"],
    "reasoning_paths": ["Provider → Model → Config → Deploy"],
    "source_digest": ["Extracted from SkillsConfigModel"]
  },
  "delivery_artifacts": [
    {
      "artifact_name": "SkillsConfig",
      "artifact_type": "json_schema",
      "path": "artifacts/SkillsConfig.schema.json",
      "status": "planned"
    }
  ],
  "dropped_items": [],
  "open_questions": []
}
```

### 3.5 Loading Flow

```text
SkillLoader.ParseSkillContent()
  │
  ├─ ParseSkillFile(filePath, skillDir, source)
  │   └─ ReadAllText → ParseSkillContent
  │
  ├─ ScanSkillResources(skillDir)    ← scan references/scripts/
  ├─ TryLoadArtifactContract(skillDir, logger)
  │   └─ Read contracts/artifacts.json → SkillArtifactContract
  │   └─ File missing / parse error → null (silent degrade)
  │
  └─ TryLoadProjectionContracts(skillDir, logger)
      └─ Scan contracts/projections/*/contract-index.json
      └─ Dir missing → ProjectionContracts = [], Discovery.Status = "none"
      └─ Parse error → Discovery.Status = "partial" / "parse-failed"
```

---

## 4. Runtime Architecture

### 4.1 Runtime Landscape

```text
┌──────────────────────────────────────────────────────────────┐
│                     Gateway Startup                           │
│                                                              │
│   new SkillArtifactRuntime()                                  │
│   skills = SkillLoader.LoadAll(...)                            │
│   artifactRuntime.ReplaceSkills(skills)   ← initial register  │
│                                                              │
│   ┌─ Native AgentRuntime ─┐  ┌─ MAF AgentRuntime ─┐         │
│   │ (OpenClaw.Agent)      │  │ (MicrosoftAgent-    │         │
│   │                       │  │  FrameworkAdapter)  │         │
│   │ AgentRuntime.cs       │  │ MafAgentRuntime.cs  │         │
│   └───────────────────────┘  └─────────────────────┘         │
│                                                              │
│   ┌─ Shared Tool Layer ────────────────────────────┐        │
│   │ EmitArtifactTool  ←─ SkillArtifactRuntime       │        │
│   │ LoadSkillTool     ←─ SkillDefinition (Contract) │        │
│   │ ReadSkillResourceTool                           │        │
│   │ MetaInvokeTool                                  │        │
│   └─────────────────────────────────────────────────┘        │
└──────────────────────────────────────────────────────────────┘
```

### 4.2 Per-Turn Complete Data Flow

```text
User Message arrives
  │
  ├─ Gateway ReceiveMessage
  │
  ├─ AgentRuntime.GetSystemPrompt(session, userMessage)   ← new: userMessage param
  │   │
  │   ├─ Check hasProjectionSkills?
  │   │   │
  │   │   ├─ YES (any Skill has ProjectionContracts):
  │   │   │   └─ ResolveSkillsForTurn(_loadedSkills, userMessage, out blockedRoutes)
  │   │   │       │
  │   │   │       ├─ For each Skill:
  │   │   │       │   ├─ No ProjectionContracts → keep as-is
  │   │   │       │   │
  │   │   │       │   └─ Has ProjectionContracts:
  │   │   │       │       └─ SkillProjectionResolver.ResolveForRequest(skill, userMessage)
  │   │   │       │           ├─ SelectTopic(index, requestText)   ← signal scoring
  │   │   │       │           │   ├─ PrimaryIntentSignals weight 5
  │   │   │       │           │   ├─ SupportingSignals weight 1
  │   │   │       │           │   ├─ ExplicitArtifactSignals bonus +4
  │   │   │       │           │   └─ DemoteWhenCompeting penalty -2
  │   │   │       │           │
  │   │   │       │           ├─ SelectView(index, topic, requestText)  ← view match
  │   │   │       │           │   ├─ ExplicitOutputSignals weight 5
  │   │   │       │           │   ├─ StrongSignals weight 3
  │   │   │       │           │   ├─ SupportingSignals weight 1
  │   │   │       │           │   ├─ CrossViewPenalty -2
  │   │   │       │           │   ├─ DefaultViewBonus +1
  │   │   │       │           │   └─ TopicOverrideBonuses variable
  │   │   │       │           │
  │   │   │       │           ├─ LoadProjection(filePath)            ← load projection.json
  │   │   │       │           │
  │   │   │       │           ├─ Check PreferReadyOnly / BlockOnOpenQuestions / BlockOrEscalate
  │   │   │       │           │
  │   │   │       │           └─ Multi-producer ranking: Score → ProducerPriority
  │   │   │       │
  │   │   │       ├─ IsBlocked?
  │   │   │       │   └─ CloneSkill(skill, originalInstructions, disableModel: true)
  │   │   │       │       Record in [Blocked Skill Routes]
  │   │   │       │
  │   │   │       └─ HasPatch?
  │   │   │           └─ CloneSkill(skill, patchedInstructions, disableModel: false)
  │   │   │               Instructions += "\n\n[Projection Route]\n<projection-patch>"
  │   │   │
  │   │   └─ BuildIndex(effectiveSkills) → Patched Skill Index
  │   │   └─ [Blocked Skill Routes] injected
  │   │
  │   └─ NO:
  │       └─ Use cached _systemPrompt (zero overhead)
  │
  ├─ BuildMessages(session, userMessage)
  │   └─ List<ChatMessage> { System(patchedPrompt), History... }
  │
  ├─ LLM Inference (sees patched skill index)
  │   │
  │   ├─ LLM calls load_skill({ skill: "xxx" })
  │   │   └─ LoadSkillTool.ExecuteAsync()
  │   │       ├─ BuildSkillBody(match)           ← full skill instructions
  │   │       ├─ if ArtifactContract:            ← inject <skill-artifact-contract> XML
  │   │       ├─ if Resources:                   ← inject <skill-resources> manifest
  │   │       └─ return body
  │   │
  │   └─ LLM calls emit_artifact({ kind, artifact_type, stage, ... })
  │       └─ EmitArtifactTool.ExecuteAsync()
  │           ├─ kind=file → ExecuteFileAsync()
  │           │   ├─ ResolveRealPath → IsReadAllowed
  │           │   ├─ ReadFile → MediaCache.Save → /media/{id}
  │           │   ├─ _artifactRuntime.NormalizeAndRecord(sessionId, artifact)
  │           │   │   ├─ No SkillName → skip validation, pass through
  │           │   │   ├─ SkillName not in registry → reject
  │           │   │   ├─ No contract → skip validation, pass through
  │           │   │   ├─ TryResolveArtifactContract()
  │           │   │   │   ├─ Explicit stage → exact match
  │           │   │   │   └─ Inferred stage → global unique match
  │           │   │   ├─ Normalize: Label/DisplayHint/IsTerminal/Stage
  │           │   │   ├─ if Terminal → MarkStageTerminal() + BuildGateEvent()
  │           │   │   └─ return SkillArtifactResult
  │           │   ├─ WebSocket → { type: "artifact", artifact: ... }
  │           │   └─ if StageGate → { type: "skill_stage_gate", stage_gate: ... }
  │           │
  │           └─ kind=data → ExecuteDataAsync()
  │               └─ Same flow without file operations
  │
  └─ WebSocket → Frontend
      ├─ "artifact" envelope → render (progress/tree/badge/file/text)
      └─ "skill_stage_gate" envelope → update stage capsule state
```

---

## 5. Contract Validation Engine (SkillArtifactRuntime)

### 5.1 Two-Level Matching Algorithm

```text
Model Input                      | Contract State          | Result
─────────────────────────────────────────────────────────────────
Explicit stage + artifactType    | Both match              | ✅ Success
Explicit stage + artifactType    | Stage exists, type not  | ❌ "not declared for stage"
Explicit stage + artifactType    | Stage not found         | ❌ "not declared"
Only artifactType                | Globally unique         | ✅ Auto-infer stage
Only artifactType                | Zero matches            | ❌ "not declared in contracts"
Only artifactType                | Multi-stage ambiguous   | ❌ "appears in multiple stages"
No SkillName                     | —                       | ✅ Skip validation
```

### 5.2 Normalization Logic

The model only needs to provide minimal fields; the runtime auto-fills the rest:

```text
Model provides:             Runtime fills:
  artifact_type (required) → Label       ← artifactType.Label
  stage (optional)         → Stage       ← auto-inferred or used directly
  label (optional)         → DisplayHint ← artifactType.Display
  terminal (optional)      → IsTerminal  ← artifactType.Terminal
```

### 5.3 Stage Gate State Machine

```text
Current Stage Terminal Artifact emitted
  │
  ├─ Is last stage → null (no gate event)
  │
  └─ Has next stage
      ├─ No gate.requiresStage → CanProceed = true
      │
      └─ Has gate.requiresStage = "stageX"
          ├─ stageX completed (IsTerminal) → CanProceed = true
          └─ stageX not completed          → CanProceed = false + BlockedReason
```

### 5.4 Concurrency Safety

- `_skills`: `ConcurrentDictionary<string, SkillDefinition>` — `ReplaceSkills` clears and rebuilds
- `_stageStates`: `ConcurrentDictionary<string, StageState>` — independent state per session+skill+stage
- Key format: `$"{sessionId}:{skillName}:{stageName}"`

---

## 6. Projection Resolution Engine (SkillProjectionResolver)

### 6.1 Four Projection Views

| View | ViewKey | Explicit Artifact Signals |
|------|---------|--------------------------|
| JSON Schema | `json-schema` | "json schema", "schema file", "schema definition" |
| Workflow Contract | `workflow-contract` | "workflow contract", "工作流契约" |
| Domain Model | `domain-model` | "domain model", "领域模型" |
| Prompt Constraint | `prompt-constraint` | "prompt policy", "prompt constraint" |

### 6.2 Signal Scoring Algorithm

```text
Topic Scoring:
  Score = Σ PrimaryIntentSignals × 5
        + Σ SupportingSignals × 1
        + ExplicitArtifact hit bonus +4
        + PrimaryIntent hit bonus +5
        - DemoteWhenCompeting × 2

View Scoring:
  Score = Σ ExplicitOutputSignals × 5
        + Σ StrongSignals × 3
        + Σ SupportingSignals × 1
        + DefaultViewBonus +1
        + ExplicitArtifactRequestBonus +4
        - DemoteWhenCompeting × 2
        + TopicOverrideBonuses (variable)
```

### 6.3 Decision Chain

```text
ResolveForRequest(skill, requestText)
  │
  ├─ For each ProjectionContractSet:
  │   ├─ TryResolveContract()
  │   │   ├─ SelectTopic(index, requestText)
  │   │   │   ├─ Score > 0? → Continue
  │   │   │   ├─ Top-2 Score Gap ≥ Threshold? → Select Top-1
  │   │   │   └─ Gap < Threshold → Ambiguous
  │   │   │
  │   │   ├─ SelectView(index, topic, requestText)
  │   │   │   ├─ PreferReadyOnly? → Only consider READY Views
  │   │   │   ├─ Score > 0? → Continue
  │   │   │   └─ Top-2 Score Gap ≥ Threshold? → Select Top-1
  │   │   │
  │   │   ├─ LoadProjection(file)
  │   │   │   └─ File not found → Block
  │   │   │
  │   │   ├─ BlockOnOpenQuestions ∧ OpenQuestions.Count > 0 → Block
  │   │   └─ BlockOrEscalate ∧ OpenQuestions.Count > 0 → Block
  │   │
  │   └─ matchedAttempts (ordered by Score → ProducerPriority desc)
  │
  ├─ Top-1 vs Top-2 tied score & priority? → Block (ambiguous)
  └─ Return Top-1 SkillProjectionResolution
```

### 6.4 BuildPromptPatch Output Format

```text
[Projection Route]
Selected topic: task-execution
Selected target view: prompt-constraint
Projection source: /skills/.../contracts/projections/.../projection.json
Prompt constraint: Do not use unmapped terms or invent terminology beyond this projection.

Allowed terms:
- approved term

Forbidden assumptions:
- API key is embedded

Required clarifications:
- Identify the target deployment provider

Reasoning paths:
- Provider → Model → Config → Deploy

Delivery artifacts:
- SkillsConfig (json_schema) -> artifacts/SkillsConfig.schema.json [planned]
```

---

## 7. Runtime Behavioral Alignment

### 7.1 Startup Phase

| Behavior | Native AgentRuntime | MAF MafAgentRuntime |
|----------|:--:|:--:|
| SkillLoader loads ArtifactContract | ✅ | ✅ |
| SkillLoader loads ProjectionContracts | ✅ | ✅ |
| `SkillArtifactRuntime.ReplaceSkills` | ✅ | ✅ |
| `EmitArtifactTool` DI registration | ✅ (gated) | ✅ (shared) |

### 7.2 Per-Turn Phase

| Behavior | Native AgentRuntime | MAF MafAgentRuntime |
|----------|:--:|:--:|
| `GetSystemPrompt(session, userMessage)` overload | ✅ | ✅ |
| `ResolveSkillsForTurn` invoked | ✅ | ✅ |
| `SkillProjectionResolver.ResolveForRequest` | ✅ (shared code) | ✅ (shared code) |
| `CloneSkill` preserves all 20 fields | ✅ | ✅ |
| `[Blocked Skill Routes]` injection | ✅ | ✅ |
| Reuses cached prompt when no Projection (zero overhead) | ✅ | ✅ |

### 7.3 Hot Reload Phase

| Behavior | Native AgentRuntime | MAF MafAgentRuntime |
|----------|:--:|:--:|
| `SkillWatcherService` callback fires | ✅ | ✅ |
| `artifactRuntime.ReplaceSkills(newSkills)` | ✅ | ✅ |
| `ApplySkills(newSkills)` rebuilds prompt | ✅ | ✅ |

### 7.4 CloneSkill Field Completeness

```csharp
// Both runtimes' CloneSkill are aligned, copying all 20 fields:
Name, Description, Instructions, Location, Source, Metadata,
Kind, Triggers, MetaPriority, FinalTextMode, Composition,
UserInvocable, DisableModelInvocation,
CommandDispatch, CommandTool, CommandArgMode,
Resources, ProjectionContracts, ProjectionDiscovery,
ArtifactContract
```

---

## 8. Frontend Protocol

### 8.1 WebSocket Envelopes

**type = "artifact"** (artifact delivery):

```json
{
  "type": "artifact",
  "text": "Requirements Doc",
  "artifact": {
    "kind": "data",
    "artifact_type": "requirements",
    "label": "Requirements Doc",
    "skill_name": "pipeline-skill",
    "stage": "design",
    "is_terminal": true,
    "display_hint": "badge",
    "data": { ... }
  }
}
```

**type = "skill_stage_gate"** (stage gate, only sent after Terminal artifact):

```json
{
  "type": "skill_stage_gate",
  "stage_gate": {
    "skill_name": "pipeline-skill",
    "completed_stage": "design",
    "next_stage": "review",
    "can_proceed": true,
    "blocked_reason": null
  }
}
```

### 8.2 Frontend Behavior Mapping

| Contract Field | Frontend Behavior |
|---------------|-------------------|
| `display: "progress"` | Stage capsule set to running |
| `display: "badge"` | Show confirmation gate badge |
| `display: "tree"` | Render tree data structure |
| `display: "file"` | Trigger auto-download/import |
| `terminal: true` | Stage capsule set to completed |
| `terminal: false` | Update progress without changing stage state |
| `gate.requiresStage` | Lock next stage until prerequisite is complete |

---

## 9. Configuration

### 9.1 GatewayConfig

```jsonc
{
  "Tooling": {
    "EnableEmitArtifact": true   // default true, can disable emit_artifact tool
  }
}
```

### 9.2 Contract File Conventions

- `contracts/artifacts.json` — present → validation enabled; absent → silently skipped
- `contracts/projections/*/contract-index.json` — present → Projection enabled; absent → `Status = "none"`
- Both systems are fully independent; either can be enabled or disabled individually

---

## 10. Design Principles

1. **Null-safe**: No contract file → silent degrade, no errors, no blocking
2. **Minimal model constraint**: Model only provides `artifact_type`; runtime auto-fills label/display/stage/terminal
3. **Deterministic signal matching**: Projection resolution uses strict scoring rules; deterministic routing when unambiguous
4. **Concurrency-safe**: `ConcurrentDictionary` ensures thread safety for `ReplaceSkills` + `NormalizeAndRecord`
5. **Progressive disclosure**: ArtifactContract XML block only injected on `load_skill`, not fully expanded in system prompt
6. **Per-turn projection**: Dynamically resolve Projection each user turn, ensuring instructions always align with the current request
7. **Zero-overhead path**: When no Projection contracts exist, reuse cached `_systemPrompt` with no additional computation
