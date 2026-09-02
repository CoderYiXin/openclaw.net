# Harness 回归套件

Harness 回归套件检查重要的 OpenClaw.NET 运行时保证是否仍然成立。

它专为以下场景设计：

- 维护者
- 贡献者
- CI
- 发布准备
- 先审查的 Harness 更改
- 未来的 Harness 演进提案

它帮助回答：

- 快速启动仍然有效吗？
- 安全默认值仍然安全吗？
- 审批仍然被强制执行吗？
- 记忆仍然能往返吗？
- 学习提案和 Harness 模型仍然可以序列化吗？
- 自动化建议仍然是"先审查"且经过质量把关的吗？
- 提供商配置有效吗？
- MCP/OpenAI 兼容表面在结构上完整吗？

## 使用方法

从检出或已安装的 CLI 运行套件：

```bash
openclaw harness test
openclaw harness test --offline
openclaw harness test --category security
openclaw harness test --json
openclaw harness test --strict
```

从源代码使用：

```bash
dotnet run --project src/OpenClaw.Cli -c Release -- harness test
```

该命令是离线优先的，不需要云提供商密钥。它不会启动网关、调用模型提供商或联系外部 MCP 服务器。

## 输出

默认文本输出面向人类：

```text
OpenClaw Harness Regression

PASS onboarding.quickstart_config - Config loaded successfully.
PASS security.url_safety_defaults - Default URL safety blocks loopback, private, and metadata targets.
PASS providers.config_shape - Provider/model shape is valid without external network calls.
PASS mcp.initialize_shape - MCP initialize request shape serializes without running a gateway.
FAIL security.public_bind_hardening - Public/non-loopback bind is missing required hardening.

Summary:
14 passed, 1 failed, 1 skipped, 0 warning, 0 not applicable
```

使用 `--json` 发出 `HarnessRegressionReport` 模型。使用 `--output <path>` 也将选定的输出格式写入文件。

## 类别

使用 `--category <name>` 运行聚焦的子集：

- `onboarding`
- `security`
- `approvals`
- `memory`
- `providers`
- `tools`
- `mcp`
- `openai_compat`
- `sessions`
- `harness`
- `deployment`
- `docs`

## 学习相关覆盖

学习行为目前通过现有的 Harness 和单元测试层覆盖，而不是单独的 `learning` 类别。最相关的检查包括：

- 源生成的 JSON 往返用于学习提案模型
- 提案元数据和反馈事件的文件存储持久化
- 先审查的批准和拒绝行为
- 自动化建议质量把关，将模糊的重复提示保持为仅学习信号
- Harness 演进提案验证、治理账本链接和仅手动批准语义

有关学习提案模型、自动化建议质量管道以及信任学习更改前推荐的检查，请参阅 [LEARNING.md](LEARNING.md)。

## 退出码

- `0`：所有必需检查通过或被适当地跳过
- 非零：至少一个必需检查失败
- `--strict`：将必需的警告和跳过视为失败

## 目前不做的事情

- 不是单元测试的替代品。
- 不是完整的模型/提供商集成测试。
- 不保证每个 Agent 结果都是正确的。
- 默认不需要提供商密钥。
- 在正常运行时不会自动运行。
- 默认不会创建证据包。

该套件是 CLI/检查表面。除非显式调用该命令，否则正常聊天、提供商、工具执行、审批、记忆、Companion 设置、MCP 和 OpenAI 兼容路由保持不变。
