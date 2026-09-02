# Harness 合约

Harness 合约是 Agent 工作的结构化计划。它在有意义的执行发生之前，捕获目标、计划的行动、涉及资源、所需工具、审批需求、验证计划、回滚计划和成功标准。

Harness 合约在当前版本中是被动的。它们不会改变正常的聊天行为、快速启动、提供商行为、现有的工具执行、记忆行为、Companion 设置、MCP 路由或 OpenAI 兼容路由。

## 为什么存在

Harness 合约使非平凡的 Agent 工作在执行前更可检查。它们适用于：

- 高风险工具使用
- 文件写入
- Shell 执行
- 多步骤工作流
- 学习提案
- 未来的计划-执行-验证模式
- 工业和运维工作流
- 未来的证据包、治理账本条目、共享 Harness 状态和 Runtime Pulse 工作流

## 合约结构

每个合约记录：

- 意图和用户请求摘要
- 计划的行动
- 读取和写入资源集
- 工具需求
- 风险级别和审批需求
- 假设和约束
- 验证计划
- 回滚计划
- 成功标准
- 来源会话、参与者、渠道和发送者元数据

示例：

```json
{
  "id": "hctr_docs_update",
  "status": "proposed",
  "goal": "Update documentation for a passive harness feature",
  "userRequestSummary": "Document Harness Contracts and link them from the docs index.",
  "sourceSessionId": "session-123",
  "actorId": "operator",
  "riskLevel": "medium",
  "approvalRequired": "none",
  "plannedActions": [
    {
      "id": "docs",
      "title": "Update docs",
      "toolName": "file_write",
      "actionType": "write",
      "requiresApproval": false,
      "writeSet": [
        {
          "kind": "file",
          "path": "docs/HARNESS_CONTRACTS.md",
          "description": "Harness Contract documentation"
        }
      ],
      "expectedOutcome": "Operators can understand the feature boundary."
    }
  ],
  "verificationPlan": [
    {
      "id": "tests",
      "title": "Run tests",
      "kind": "command",
      "command": "dotnet test",
      "expectedSignal": "All relevant tests pass.",
      "required": true
    }
  ],
  "rollbackPlan": [
    {
      "id": "revert",
      "title": "Revert changes",
      "description": "Revert the feature commit if the passive surface causes regressions."
    }
  ],
  "successCriteria": [
    "Contracts can be stored, listed, and inspected.",
    "Normal runtime behavior is unchanged."
  ],
  "tags": ["harness", "docs"]
}
```

## 管理员检查

运维人员可以通过以下端点检查 Harness 合约：

- `GET /admin/harness/contracts`
- `GET /admin/harness/contracts/{id}`
- `POST /admin/harness/contracts`
- `POST /admin/harness/contracts/{id}/status`

读取端点需要经过身份验证的管理员查看者访问权限。变更端点需要运维级别的访问权限和与现有管理变更相同的 CSRF 保护。

## 重要细微差别

Harness 合约不会替换工具审批。它们使审批需求和计划的工作在执行前更易于检查。

Harness 合约也不同于 OpenClaw.NET 现有的可执行合约治理 API。现有的 `ContractPolicy` 和 `/api/contracts` 表面对会话进行验证和附加执行约束。Harness 合约是结构化的意图记录。未来的计划-执行-验证工作流可以将 Harness 合约转换为可执行的治理约束，但当前版本不会自动执行此操作。

## 目前不做的事情

- 默认不为所有正常聊天启用。
- 不是完整的计划-执行-验证模式。
- 不会自动回滚更改。
- 不是现有工具审批的替代品。
- 不会削弱现有的审批、安全、提供商、记忆、快速启动、MCP 或 OpenAI 兼容行为。
