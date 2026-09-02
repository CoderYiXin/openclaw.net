# 工作流后端

OpenClaw.NET 可以正常运行 Agent 回合，并将长时间运行的工作委派给配置的工作流后端。工作流委派用于持久计划、审批关卡、扇出/扇入审查和面向审计的企业自动化。它不是为每个快速 Agent 回合使用的。

## 配置

工作流后端位于 `OpenClaw:Workflows` 下：

```json
{
  "OpenClaw": {
    "Workflows": {
      "Enabled": true,
      "Backends": {
        "durable-review": {
          "Kind": "maf-durable-http",
          "DisplayName": "Durable Agent Review",
          "BaseUrl": "http://127.0.0.1:5095",
          "WorkflowName": "DurableAgentReview",
          "PollIntervalSeconds": 2,
          "TimeoutSeconds": 120
        }
      }
    }
  }
}
```

`maf-durable-http` 是第一个受支持的后端类型。网关通过 HTTP 调用持久的 MAF/Azure Functions 主机；Durable Task 和 Azure Functions 依赖保留在该主机或示例中，不在网关中。

## HTTP 合约

网关期望相对于 `BaseUrl` 的以下后端端点：

| 操作 | 方法和路径 | 网关端点 |
| --- | --- | --- |
| 启动运行 | `POST /api/workflows/{workflowName}/run` | `POST /api/integration/workflows/{backendId}/runs` |
| 获取状态 | `GET /api/workflows/{workflowName}/status/{runId}` | `GET /api/integration/workflows/{backendId}/runs/{runId}` |
| 响应输入 | `POST /api/workflows/{workflowName}/respond/{runId}` | `POST /api/integration/workflows/{backendId}/runs/{runId}/responses` |

MCP 工具暴露相同的表面：

| 工具 | 用途 |
| --- | --- |
| `openclaw.list_workflows` | 列出配置的工作流后端。 |
| `openclaw.run_workflow` | 启动工作流运行。 |
| `openclaw.get_workflow_run` | 读取当前状态、事件、待处理的输入和输出。 |
| `openclaw.respond_workflow` | 向待处理的输入端口发送人工或系统响应。 |

## 状态模型

工作流运行使用以下状态：

| 状态 | 含义 |
| --- | --- |
| `queued` | 后端接受运行但尚未开始工作。 |
| `running` | 后端正在主动处理。 |
| `waiting_for_input` | 后端在人工审批或外部响应端口上阻塞。 |
| `completed` | 运行成功完成。 |
| `failed` | 运行失败。 |
| `cancelled` | 运行被拒绝或取消。 |

事件和待处理的输入是来自 `OpenClaw.Core` 的 AOT 安全 JSON DTO。负载字段使用 `JsonElement`，以便工作流主机可以附加结构化审查摘要、审计追踪或审批上下文，而不将网关耦合到特定的持久运行时包。

## DurableAgentReview 示例

`samples/OpenClaw.DurableAgentReview` 暴露了一个示例 `maf-durable-http` 主机：

```bash
dotnet run --project samples/OpenClaw.DurableAgentReview
```

然后使用匹配的后端配置网关，通过集成 API 或 MCP 启动工作流。示例建模了以下流程：

```text
用户请求
  -> 计划执行器
  -> 安全、架构和成本审查者
  -> 聚合器
  -> 人工审批 RequestPort
  -> 执行批准的操作
  -> 审计追踪输出
```

该示例将编排代码保持在网关外部。生产主机可以用 Microsoft Agent Framework Durable Workflows、Azure Functions 和 Durable Task 存储替换内存中的示例状态，同时保留相同的网关合约。
