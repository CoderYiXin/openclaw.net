# 治理账本

治理账本将审批和监管决策记录为持久化的 Harness 状态。它帮助运维人员检查哪些内容被批准或拒绝、谁做的决策、原因是什么、适用范围是什么、涉及什么风险级别，以及该决策关联了哪个会话、Harness 合约、证据包、学习提案或审批请求。

治理账本条目在当前版本中是被动的。它们不会改变正常的聊天行为、提供商行为、快速启动、工具执行、审批语义、记忆行为、Companion 设置、MCP 路由或 OpenAI 兼容路由。

## 与 Harness 状态的关系

Harness 合约 = 计划的工作。

证据包 = 发生了什么、检查了什么、什么仍然不确定，以及为什么结果应该或不应该被信任。

治理账本 = 人类/运维人员的决策历史。

账本与现有的审批提示本身是分离的。它在现有审批流程做出决定后记录决策，因此审批失败、拒绝、超时和请求者检查保持其现有行为。

它也与可复用的审批授权是分离的。授权可以被现有的审批授权逻辑消费，账本可以记录这一事实，但账本不创建或应用授权。

## 示例

```json
{
  "id": "gov_approval_001",
  "decision": "approved",
  "status": "active",
  "source": "tool_approval",
  "actionType": "execute",
  "toolName": "shell",
  "actionSummary": "Operator approved a shell command after reviewing arguments.",
  "argumentSummary": "{\"cmd\":\"dotnet test\"}",
  "redactedArguments": "{\"cmd\":\"dotnet test\"}",
  "riskLevel": "high",
  "scope": "once",
  "scopeKey": "apr_123",
  "sessionId": "sess_123",
  "harnessContractId": "hctr_123",
  "evidenceBundleId": "evb_123",
  "approvalId": "apr_123",
  "channelId": "web",
  "senderId": "operator",
  "decidedBy": "operator",
  "decisionReason": "Tests and rollback plan were reviewed.",
  "policyHint": {
    "suggestedFutureBehavior": "consider_reusable_grant",
    "suggestedScope": "session",
    "confidence": "medium",
    "requiresReview": true,
    "notes": "Informational only; future automation must be explicit."
  },
  "tags": ["approval", "governance"]
}
```

## 目前不做的事情

- 不会自动批准未来的操作。
- 不会替换现有的工具审批或请求者匹配检查。
- 不会削弱安全行为。
- 不会启用完整的计划-执行-验证模式。
- 不会为每个审批自动创建证据包。
- 不会使用 `policyHint` 进行强制执行；未来的策略自动化必须是显式的且需要选择加入。
