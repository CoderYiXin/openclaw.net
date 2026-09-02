# Runtime ArtifactContract + Projection 技术文档

## 1. 概述

**ArtifactContract** 和 **Projection** 是 OpenClaw Skill 系统的两个正交扩展：
<!-- markdownlint-disable MD040 -->

| 系统 | 职责 | 触发时机 |
|------|------|---------|
| **ArtifactContract** | 定义 Skill 多阶段工作流中允许发出的产物白名单，运行时校验 `emit_artifact` 调用合法性 | LLM 调用 `emit_artifact` 工具时 |
| **Projection** | 基于用户请求信号匹配最优的 Skill 投影视图，动态裁剪 Skill 指令并注入到系统 Prompt | 每次用户 Turn 时 |

两系统共享数据模型层（`SkillArtifactContractModels.cs`），通过 `contracts/` 目录下的 JSON 文件声明契约规则，采用 `null` 安全设计——无契约文件时静默降级，不阻塞 Skill 加载和正常推理。

### 1.1 关键文件

| 文件 | 项目 | 职责 |
|------|------|------|
| `src/OpenClaw.Core/Models/SkillArtifact.cs` | Core | `SkillArtifact` record — Artifact 运行时消息载体 |
| `src/OpenClaw.Core/Models/SkillArtifactContractModels.cs` | Core | 4 个 Artifact 契约类 + 14 个 Projection 模型类 |
| `src/OpenClaw.Core/Models/WebSocketEnvelopes.cs` | Core | `WsServerEnvelope` 扩展 + `SkillStageGateEvent` record |
| `src/OpenClaw.Core/Skills/SkillModels.cs` | Core | `SkillDefinition` 扩展属性 |
| `src/OpenClaw.Core/Skills/SkillLoader.cs` | Core | `TryLoadArtifactContract` + `TryLoadProjectionContracts` |
| `src/OpenClaw.Core/Skills/LoadSkillTool.cs` | Core | ArtifactContract XML 注入 Prompt |
| `src/OpenClaw.Core/Skills/SkillPromptBuilder.cs` | Core | `BuildSkillBody` Projection 重载 |
| `src/OpenClaw.Core/Skills/SkillProjectionResolver.cs` | Core | 投影路由引擎：Topic/View 信号匹配 + 评分 + JSON 加载 |
| `src/OpenClaw.Core/Skills/SkillProjectionArtifactTerms.cs` | Core | 四类投影视图的显式 Artifact 关键词映射 |
| `src/OpenClaw.Gateway/Tools/EmitArtifactTool.cs` | Gateway | `emit_artifact` 工具实现 |
| `src/OpenClaw.Gateway/Tools/SkillArtifactRuntime.cs` | Gateway | 运行时校验引擎 + 阶段状态机 |
| `src/OpenClaw.Agent/AgentRuntime.cs` | Agent | 每 Turn Projection 解析 + `ResolveSkillsForTurn` + `CloneSkill` |
| `src/OpenClaw.MicrosoftAgentFrameworkAdapter/MafAgentRuntime.cs` | MAF Adapter | 与 AgentRuntime 等价的 Projection 解析 |

---

## 2. 核心数据模型

### 2.1 SkillArtifact（运行时消息载体）

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

### 2.2 SkillArtifactContract（顶层契约）

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

### 2.3 阶段门控模型（SkillStageGateEvent）

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

### 2.4 Projection 模型（14 个类）

```text
SkillProjectionContractSet      — 绑定的投影契约集
SkillProjectionDiscovery        — 加载诊断信息
SkillProjectionResolution       — 单次解析结果
ProjectionContractIndex         — 索引：Topics + Views + Scoring
ProjectionSelectionPolicy       — 选择策略
ProjectionTopicScoring          — Topic 级评分配置
ProjectionTargetViewScoring     — View 级评分配置
ProjectionScoreDimension        — 评分维度
ProjectionTopicSignals          — Topic 信号关键词
ProjectionViewSignals           — View 信号关键词
ProjectionTopicViewOverride     — Topic 内 View 加权覆盖
ProjectionTopicViewBonus        — 加权项
ProjectionTopicRecord           — Topic 定义
ProjectionViewRecord            — View 定义
ProjectionDocument              — 投影文件内容
ProjectionMappingPolicy         — 映射策略
ProjectionPromptPayload         — Prompt 约束载荷
ProjectionDeliveryArtifact      — 产出 Artifact
```

---

## 3. 加载机制

### 3.1 磁盘文件布局

```text
skills/<skill-name>/
├── SKILL.md
└── contracts/
    ├── artifacts.json                ← ArtifactContract 契约
    └── projections/                  ← Projection 索引根
        └── <producer-name>/
            ├── contract-index.json   ← 索引定义
            └── <view-path>.json      ← 具体投影文件
```

### 3.2 artifacts.json 示例

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

### 3.3 contract-index.json 示例

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

### 3.4 projection.json 示例

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

### 3.5 加载流程

```text
SkillLoader.ParseSkillContent()
  │
  ├─ ParseSkillFile(filePath, skillDir, source)
  │   └─ ReadAllText → ParseSkillContent
  │
  ├─ ScanSkillResources(skillDir)    ← 扫描 references/scripts/
  ├─ TryLoadArtifactContract(skillDir, logger)
  │   └─ 读取 contracts/artifacts.json → SkillArtifactContract
  │   └─ 文件不存在/解析失败 → null（静默降级）
  │
  └─ TryLoadProjectionContracts(skillDir, logger)
      └─ 扫描 contracts/projections/*/contract-index.json
      └─ 目录不存在 → ProjectionContracts = [], Discovery.Status = "none"
      └─ 解析失败 → Discovery.Status = "partial" / "parse-failed"
```

---

## 4. 运行时架构

### 4.1 两个 Runtime 全景图

```text
┌──────────────────────────────────────────────────────────────┐
│                     Gateway 启动                              │
│                                                              │
│   new SkillArtifactRuntime()                                  │
│   skills = SkillLoader.LoadAll(...)                            │
│   artifactRuntime.ReplaceSkills(skills)   ← 初始注册技能契约  │
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

### 4.2 每 Turn 完整数据流

```text
User Message 到达
  │
  ├─ Gateway ReceiveMessage
  │
  ├─ AgentRuntime.GetSystemPrompt(session, userMessage)   ← 新增：userMessage 参数
  │   │
  │   ├─ 检查 hasProjectionSkills?
  │   │   │
  │   │   ├─ YES (任何 Skill 有 ProjectionContracts):
  │   │   │   └─ ResolveSkillsForTurn(_loadedSkills, userMessage, out blockedRoutes)
  │   │   │       │
  │   │   │       ├─ 对每个 Skill:
  │   │   │       │   ├─ 无 ProjectionContracts → 保持原样
  │   │   │       │   │
  │   │   │       │   └─ 有 ProjectionContracts:
  │   │   │       │       └─ SkillProjectionResolver.ResolveForRequest(skill, userMessage)
  │   │   │       │           ├─ SelectTopic(index, requestText)   ← 信号匹配 + 评分
  │   │   │       │           │   ├─ PrimaryIntentSignals 权重 5
  │   │   │       │           │   ├─ SupportingSignals 权重 1
  │   │   │       │           │   ├─ ExplicitArtifactSignals 额外 +4
  │   │   │       │           │   └─ DemoteWhenCompeting 惩罚 -2
  │   │   │       │           │
  │   │   │       │           ├─ SelectView(index, topic, requestText)  ← View 匹配
  │   │   │       │           │   ├─ ExplicitOutputSignals 权重 5
  │   │   │       │           │   ├─ StrongSignals 权重 3
  │   │   │       │           │   ├─ SupportingSignals 权重 1
  │   │   │       │           │   ├─ CrossViewPenalty -2
  │   │   │       │           │   ├─ DefaultViewBonus +1
  │   │   │       │           │   └─ TopicOverrideBonuses 可变
  │   │   │       │           │
  │   │   │       │           ├─ LoadProjection(filePath)            ← 加载 projection.json
  │   │   │       │           │
  │   │   │       │           ├─ 检查 PreferReadyOnly / BlockOnOpenQuestions / BlockOrEscalate
  │   │   │       │           │
  │   │   │       │           └─ 多 Producer 排序: Score → ProducerPriority
  │   │   │       │
  │   │   │       ├─ IsBlocked?
  │   │   │       │   └─ CloneSkill(skill, originalInstructions, disableModel: true)
  │   │   │       │       记录到 [Blocked Skill Routes]
  │   │   │       │
  │   │   │       └─ HasPatch?
  │   │   │           └─ CloneSkill(skill, patchedInstructions, disableModel: false)
  │   │   │               Instructions += "\n\n[Projection Route]\n<projection-patch>"
  │   │   │
  │   │   └─ BuildIndex(effectiveSkills) → Patched Skill Index
  │   │   └─ [Blocked Skill Routes] 注入
  │   │
  │   └─ NO:
  │       └─ 复用缓存的 _systemPrompt（零开销）
  │
  ├─ BuildMessages(session, userMessage)
  │   └─ List<ChatMessage> { System(patchedPrompt), History... }
  │
  ├─ LLM 推理 (sees patched skill index)
  │   │
  │   ├─ LLM 调用 load_skill({ skill: "xxx" })
  │   │   └─ LoadSkillTool.ExecuteAsync()
  │   │       ├─ BuildSkillBody(match)           ← Skill 完整指令
  │   │       ├─ if ArtifactContract:            ← 注入 <skill-artifact-contract> XML
  │   │       ├─ if Resources:                   ← 注入 <skill-resources> manifest
  │   │       └─ return body
  │   │
  │   └─ LLM 调用 emit_artifact({ kind, artifact_type, stage, ... })
  │       └─ EmitArtifactTool.ExecuteAsync()
  │           ├─ kind=file → ExecuteFileAsync()
  │           │   ├─ ResolveRealPath → IsReadAllowed
  │           │   ├─ ReadFile → MediaCache.Save → /media/{id}
  │           │   ├─ _artifactRuntime.NormalizeAndRecord(sessionId, artifact)
  │           │   │   ├─ 无 SkillName → 跳过校验，放行
  │           │   │   ├─ SkillName 不在注册表 → 拒绝
  │           │   │   ├─ 无契约 → 跳过校验，放行
  │           │   │   ├─ TryResolveArtifactContract()
  │           │   │   │   ├─ 显式 stage → 精确匹配
  │           │   │   │   └─ 推断 stage → 全局唯一匹配
  │           │   │   ├─ 归一化: Label/DisplayHint/IsTerminal/Stage
  │           │   │   ├─ if Terminal → MarkStageTerminal() + BuildGateEvent()
  │           │   │   └─ return SkillArtifactResult
  │           │   ├─ WebSocket → { type: "artifact", artifact: ... }
  │           │   └─ if StageGate → { type: "skill_stage_gate", stage_gate: ... }
  │           │
  │           └─ kind=data → ExecuteDataAsync()
  │               └─ 同上，但不涉及文件操作
  │
  └─ WebSocket → 前端
      ├─ "artifact" 信封 → 渲染对应组件（progress/tree/badge/file/text）
      └─ "skill_stage_gate" 信封 → 更新阶段胶囊状态
```

---

## 5. 契约校验引擎 (SkillArtifactRuntime)

### 5.1 两级匹配算法

```text
模型传参                        | 契约状态                | 结果
─────────────────────────────────────────────────────────────────
指定 stage + artifactType      | 均匹配                  | ✅ 成功
指定 stage + artifactType      | stage存在但type不匹配    | ❌ "not declared for stage"
指定 stage + artifactType      | stage不存在             | ❌ "not declared"
仅 artifactType                | 全局唯一匹配            | ✅ 自动推断 stage
仅 artifactType                | 零匹配                  | ❌ "not declared in contracts"
仅 artifactType                | 多阶段匹配（歧义）      | ❌ "appears in multiple stages"
无 SkillName                   | —                       | ✅ 跳过校验
```

### 5.2 归一化逻辑

模型只需提供最低限度的字段，运行时自动补齐：

```text
模型提供:                   运行时补齐:
  artifact_type (必填)  →    Label       ← artifactType.Label
  stage (可选)          →    Stage       ← 自动推断或直接使用
  label (可选)          →    DisplayHint ← artifactType.Display
  terminal (可选)       →    IsTerminal  ← artifactType.Terminal
```

### 5.3 阶段门控状态机

```text
Current Stage Terminal Artifact 发出
  │
  ├─ 是最后一个阶段 → null (无门控事件)
  │
  └─ 存在下一阶段
      ├─ 无 gate.requiresStage → CanProceed = true
      │
      └─ 有 gate.requiresStage = "stageX"
          ├─ stageX 已完成 (IsTerminal) → CanProceed = true
          └─ stageX 未完成                → CanProceed = false + BlockedReason
```

### 5.4 并发安全

- `_skills`：`ConcurrentDictionary<string, SkillDefinition>` — `ReplaceSkills` 清空重建
- `_stageStates`：`ConcurrentDictionary<string, StageState>` — 每个 session+skill+stage 组合独立状态
- Key 格式：`$"{sessionId}:{skillName}:{stageName}"`

---

## 6. Projection 解析引擎 (SkillProjectionResolver)

### 6.1 四类投影视图

| 视图 | ViewKey | 显式 Artifact 信号 |
|------|---------|-------------------|
| JSON Schema | `json-schema` | "json schema", "schema file", "schema definition" |
| Workflow Contract | `workflow-contract` | "workflow contract", "工作流契约" |
| Domain Model | `domain-model` | "domain model", "领域模型" |
| Prompt Constraint | `prompt-constraint` | "prompt policy", "prompt constraint" |

### 6.2 信号评分算法

```text
Topic 评分:
  Score = Σ PrimaryIntentSignals × 5
        + Σ SupportingSignals × 1
        + ExplicitArtifact 命中额外 +4
        + PrimaryIntent 命中额外 +5
        - DemoteWhenCompeting × 2

View 评分:
  Score = Σ ExplicitOutputSignals × 5
        + Σ StrongSignals × 3
        + Σ SupportingSignals × 1
        + DefaultViewBonus +1
        + ExplicitArtifactRequestBonus +4
        - DemoteWhenCompeting × 2
        + TopicOverrideBonuses (可变)
```

### 6.3 决策链

```text
ResolveForRequest(skill, requestText)
  │
  ├─ 对每个 ProjectionContractSet:
  │   ├─ TryResolveContract()
  │   │   ├─ SelectTopic(index, requestText)
  │   │   │   ├─ Score > 0? → Continue
  │   │   │   ├─ Top-2 Score Gap ≥ Threshold? → Select Top-1
  │   │   │   └─ Gap < Threshold → Ambiguous
  │   │   │
  │   │   ├─ SelectView(index, topic, requestText)
  │   │   │   ├─ PreferReadyOnly? → 只考虑 READY Views
  │   │   │   ├─ Score > 0? → Continue
  │   │   │   └─ Top-2 Score Gap ≥ Threshold? → Select Top-1
  │   │   │
  │   │   ├─ LoadProjection(file)
  │   │   │   └─ 文件不存在 → Block
  │   │   │
  │   │   ├─ BlockOnOpenQuestions ∧ OpenQuestions.Count > 0 → Block
  │   │   └─ BlockOrEscalate ∧ OpenQuestions.Count > 0 → Block
  │   │
  │   └─ matchedAttempts (按 Score → ProducerPriority 降序)
  │
  ├─ Top-1 vs Top-2 同分同优先级? → Block (ambiguous)
  └─ 返回 Top-1 的 SkillProjectionResolution
```

### 6.4 BuildPromptPatch 输出格式

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

## 7. 两个 Runtime 行为对齐

### 7.1 启动阶段

| 行为 | Native AgentRuntime | MAF MafAgentRuntime |
|------|:--:|:--:|
| SkillLoader 加载 ArtifactContract | ✅ | ✅ |
| SkillLoader 加载 ProjectionContracts | ✅ | ✅ |
| `SkillArtifactRuntime.ReplaceSkills` | ✅ | ✅ |
| `EmitArtifactTool` DI 注册 | ✅ (gated) | ✅ (共享) |

### 7.2 每 Turn 阶段

| 行为 | Native AgentRuntime | MAF MafAgentRuntime |
|------|:--:|:--:|
| `GetSystemPrompt(session, userMessage)` 重载 | ✅ | ✅ |
| `ResolveSkillsForTurn` 调用 | ✅ | ✅ |
| `SkillProjectionResolver.ResolveForRequest` | ✅ (共享代码) | ✅ (共享代码) |
| `CloneSkill` 保留全部 20 字段 | ✅ | ✅ |
| `[Blocked Skill Routes]` 注入 | ✅ | ✅ |
| 无 Projection 时复用缓存（零开销） | ✅ | ✅ |

### 7.3 热重载阶段

| 行为 | Native AgentRuntime | MAF MafAgentRuntime |
|------|:--:|:--:|
| `SkillWatcherService` 回调触发 | ✅ | ✅ |
| `artifactRuntime.ReplaceSkills(newSkills)` | ✅ | ✅ |
| `ApplySkills(newSkills)` 重建 Prompt | ✅ | ✅ |

### 7.4 CloneSkill 字段完整性

```csharp
// 两个 Runtime 的 CloneSkill 保持一致，复制全部 20 个字段：
Name, Description, Instructions, Location, Source, Metadata,
Kind, Triggers, MetaPriority, FinalTextMode, Composition,
UserInvocable, DisableModelInvocation,
CommandDispatch, CommandTool, CommandArgMode,
Resources, ProjectionContracts, ProjectionDiscovery,
ArtifactContract
```

---

## 8. 前端协议

### 8.1 WebSocket 信封

**type = "artifact"**（产物推送）：

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

**type = "skill_stage_gate"**（阶段门控，仅在 Terminal Artifact 后发送）：

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

### 8.2 前端行为映射

| 契约字段 | 前端行为 |
|---------|---------|
| `display: "progress"` | 阶段胶囊设为 running 状态 |
| `display: "badge"` | 显示确认门徽章 |
| `display: "tree"` | 展示树形数据结构 |
| `display: "file"` | 触发自动下载/导入 |
| `terminal: true` | 阶段胶囊设为 completed 状态 |
| `terminal: false` | 更新进度但不改变阶段状态 |
| `gate.requiresStage` | 锁住下一阶段直到前置完成 |

---

## 9. 配置

### 9.1 GatewayConfig

```jsonc
{
  "Tooling": {
    "EnableEmitArtifact": true   // 默认 true，可关闭 emit_artifact 工具
  }
}
```

### 9.2 Contract 文件约定

- `contracts/artifacts.json` — 存在 → 启用校验；不存在 → 静默跳过
- `contracts/projections/*/contract-index.json` — 存在 → 启用 Projection；不存在 → `Status = "none"`
- 两个契约系统完全独立，可单独启用/关闭

---

## 10. 设计原则

1. **null 安全**：无契约文件 → 静默降级，不报错、不阻塞
2. **模型约束最小化**：模型只需提供 `artifact_type`，运行时自动补齐 label/display/stage/terminal
3. **信号匹配确定性**：Projection 解析使用严格的评分规则，无歧义时确定性路由
4. **并发安全**：`ConcurrentDictionary` 保证 `ReplaceSkills` + `NormalizeAndRecord` 线程安全
5. **渐进式披露**：ArtifactContract XML 块仅在 `load_skill` 时注入，不在系统 Prompt 中完整展开
6. **Per-Turn 投影**：每次用户 Turn 动态解析 Projection，确保指令始终与当前请求对齐
7. **零开销路径**：无 Projection 时直接复用缓存的 `_systemPrompt`，不做额外计算
