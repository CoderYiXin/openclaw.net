# 计划-执行-验证模式

计划-执行-验证（Plan-Execute-Verify）模式是 OpenClaw.NET 的一个可选治理执行模式。

它将非平凡的 Agent 工作转化为：

```text
意图
-> Harness 合约
-> 审批/治理
-> 执行
-> 证据包
-> 验证
-> 接受/修订/升级/回滚
```

该模式专为有界的、可检查的和可验证的工作而设计。默认情况下禁用，除非显式启用，否则不会改变正常的聊天行为。

## 何时使用

对于在被信任之前应留下治理轨迹的工作，使用计划-执行-验证模式：

- 高风险工具
- 文件写入
- Shell 执行
- 浏览器操作
- 外部 API 调用
- 多工具工作流
- 学习提案应用
- 公共或频道触发的操作
- 工业和运维工作流
- 未来的远程执行后端

## 它做什么

启用后，运行时用 PEV 运行包装配置的高风险工具执行：

1. 使用现有的工具治理描述符和操作元数据对工具/操作进行分类。
2. 为配置的高风险或可写工作创建 Harness 合约。
3. 保留现有的审批行为，并对配置的风险级别要求审批。
4. 在配置时创建证据包。
5. 记录工具结果、审批证据、验证检查和治理决策。
6. 使用初始内置验证器验证结果。
7. 将运行和关联合约标记为已验证、失败、被拒绝、已取消或已升级。

第一个实现包装了中央工具执行路径。它不会替换整个 Agent 循环。

## 内置验证

初始验证器包括：

- `ToolOutcomeVerifier`：当所需的工具操作成功完成时通过。
- `ApprovalVerifier`：当所需的审批被记录为已批准时通过。
- `ContractCompletenessVerifier`：当成功标准、验证计划或回滚计划缺失时发出警告。
- `SecurityPostureVerifier`：当可从配置检测到不安全的公共绑定审批姿态时发出警告。

验证失败是安全失败。默认情况下，OpenClaw.NET 不会自动回滚工作。失败的验证建议修订或运维升级，除非以后添加显式的安全回滚支持。

## 配置

JSON 配置示例：

```json
{
  "OpenClaw": {
    "harness": {
      "executionMode": "plan-execute-verify",
      "planExecuteVerify": {
        "enabled": true,
        "contractRequiredFor": [
          "high_risk_tools",
          "write_tools",
          "shell",
          "browser",
          "external_api",
          "multi_tool_workflows"
        ],
        "requireApprovalForRisk": ["high", "critical"],
        "createEvidenceBundles": true,
        "runVerification": true,
        "autoRollbackOnFailedVerification": false,
        "maxPlanActions": 20,
        "maxVerificationSteps": 20
      }
    }
  }
}
```

默认值是保守的：

- `executionMode` 为 `normal`。
- `planExecuteVerify.enabled` 为 `false`。
- 仅在 PEV 启用时创建证据包。
- 仅在 PEV 启用且 `runVerification` 为 true 时运行验证。
- 自动回滚已禁用。
- 当一个模型响应要求 OpenClaw 运行多个工具调用时，`multi_tool_workflows` 适用。

## 管理 API

运维人员认证的端点：

- `GET /admin/harness/pev/runs`
- `GET /admin/harness/pev/runs/{id}`
- `POST /admin/harness/pev/runs/{id}/verify`
- `POST /admin/harness/pev/runs/{id}/cancel`

当这些记录可用时，PEV 运行链接到 Harness 合约和证据包。

## 不做的事情

计划-执行-验证模式：

- 默认不启用
- 正常聊天不需要
- 默认不自动回滚
- 不是人类审查的替代品
- 不保证每个任务在语义上都是正确的
- 不是单元、集成或 Harness 回归测试的替代品

将它作为需要意图、审批、证据和验证可检查的工作的执行治理层来使用。
