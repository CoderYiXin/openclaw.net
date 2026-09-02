# Harness 演进提案

OpenClaw.NET 可以提议改进自身的 Harness，但持久化运行时行为仍然是"先审查"的。

Harness 演进提案是一个 `learning_proposal`，类型为 `harness_change`。它描述了可能对策略、路由、记忆检索、验证规则、上下文预算、Pulse 行为、工具治理或相关 Harness 行为进行的更改。运维人员批准或拒绝该提案。批准记录接受以进行手动应用；它不会静默地改变运行时配置。

Harness 演进提案专为以下场景设计：

- 维护者
- 运维人员
- CI 和发布准备
- 先审查的 Harness 更改
- 未来的 Harness 演进提案工作流
- 未来的计划-执行-验证和回归审查循环

它们帮助回答：

- 什么失败了或可以改进？
- 哪个组件受到影响？
- 提议了什么更改？
- 什么证据支持该更改？
- 哪些不变量必须保持为真？
- 应该运行哪些回归检查？
- 运维人员如何回滚该更改？

## 提案结构

Harness 特定的正文包括：

- `component`：`memory`、`retrieval`、`tools`、`approvals`、`verification`、`routing`、`prompt`、`model_profile`、`pulse`、`security`、`governance`、`context_budget`、`channel`、`sandbox` 或 `unknown`
- `failureMode`
- `proposedChange`
- `predictedImprovement`
- `invariantsToPreserve`
- `falsificationTests`
- `evaluationPlan`
- `canaryPlan`
- `rollbackPlan`
- 相关的 Harness 合约、证据包、治理账本、回归报告、运行时事件和会话 ID
- `riskLevel`
- `applyMode`
- `requiresRegression`
- `regressionCategories`

示例：

```json
{
  "kind": "harness_change",
  "harnessEvolution": {
    "component": "memory",
    "failureMode": "Pulse runs include too much session history",
    "proposedChange": "Use compact memory export for pulse context",
    "predictedImprovement": "Lower token use and reduce context overload",
    "invariantsToPreserve": [
      "Do not include secrets",
      "Do not auto-write memory",
      "Keep durable changes review-first"
    ],
    "falsificationTests": [
      "harness regression: memory",
      "pulse test: compact context under budget"
    ],
    "rollbackPlan": "Disable compact pulse context mode",
    "applyMode": "manual_only",
    "requiresRegression": true,
    "regressionCategories": ["memory"]
  }
}
```

## 审查流程

Harness 演进提案使用现有的学习提案审查队列：

- `GET /admin/learning/proposals?kind=harness_change`
- `GET /admin/learning/proposals/{id}`
- `POST /admin/learning/proposals/{id}/approve`
- `POST /admin/learning/proposals/{id}/reject`

额外的运维端点：

- `POST /admin/learning/proposals/harness-change`
- `POST /admin/learning/proposals/harness-change/detect`

创建和检测由 `Learning.HarnessEvolutionEnabled` 控制。默认值是 `false`，因此现有部署不会暴露 Harness 更改提案生成，直到运维人员启用它。列出和检查现有的学习提案仍然可以通过正常的学习队列进行。

检测端点是显式的。它扫描最近的警告/错误运行时事件，对重复信号进行分组，并仅在运维人员调用时创建"先审查"的提案。

批准 `manual_only` 提案：

- 将学习提案标记为已批准
- 记录治理账本决策
- 将账本条目标回链接到提案
- 不会改变配置、记忆、技能、工具、路由、审批或提供商行为

拒绝提案会记录拒绝和关联的治理账本决策。

## 风险与验证

风险默认值是保守的：

- `security`：critical
- `approvals`、`tools`、`sandbox`、`governance`、`unknown`：high
- `memory`、`retrieval`、`verification`、`routing`、`prompt`、`model_profile`、`context_budget`、`channel`：medium
- `pulse`：low，除非它影响通知或渠道范围

验证阻止缺少组件、缺少提议更改、不支持的 apply 模式和自动应用请求。警告会提示缺少回滚计划、缺少证伪测试、模糊的预测改进、安全/审批影响以及没有回归类别的高风险提案。

## 与其他 Harness 原语的关系

- 学习提案提供审查队列和批准/拒绝工作流。
- Harness 合约描述可能支持提案的计划治理工作。
- 证据包可以链接支持证据和未经测试的领域。
- 治理账本记录人类审查决策。
- Harness 回归套件在应用提案前提供推荐的检查。
- 计划-执行-验证模式可以创建合约、证据和验证结果，以后支持提案。

## 不做的事情

- 默认不会静默自我修改。
- 不会自动进行高风险配置变更。
- 不替代人类审查。
- 不保证每个提案都是正确的。
- 提案批准后不会自动运行回归测试。
- 不会自动回滚。
