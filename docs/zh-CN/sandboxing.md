# 可选沙盒执行

> **停止。你几乎肯定不需要这个页面。**
>
> OpenSandbox 是一个可选的、高级的集成。如果你是第一次设置 OpenClaw.NET,试图让网关在本地运行，或正在按照 [QUICKSTART](QUICKSTART.md) 操作，**关闭此页面并返回**。沙盒不是首次运行路径的一部分。
>
> 代码库包含一个可选的 OpenSandbox 集成，但默认网关配置以 `OpenClaw:Sandbox:Provider=None` 开始，默认网关构建**不**包含该集成。仅当你故意使用 `-p:OpenClawEnableOpenSandbox=true` 构建时，它才会激活。在你决定为 `shell`、`code_exec` 或 `browser` 工具需要隔离执行之前，可以忽略每个与沙盒相关的设置。
>
> 如果你正在从原始配置运行，而沙盒设置的纯粹存在让你困惑，设置以下内容并继续：
>
> ```json
> { "OpenClaw": { "Sandbox": { "Provider": "None" } } }
> ```

---

本页面的其余部分记录了高级可选路径。仅当你特别想通过外部沙盒服务路由高风险原生工具，而不是在网关主机上执行它们时，才阅读它。

当前的可选后端是 [OpenSandbox](https://github.com/AIDotNet/OpenSandbox)，通过单独的 `OpenClawNet.Sandbox.OpenSandbox` 程序集集成，因此标准运行时工件保持轻量级和 NativeAOT 友好。

## 架构

运行时流程：

1. Agent 选择一个工具。
2. `OpenClawToolExecutor` 检查工具是否实现了 `ISandboxCapableTool`。
3. 有效沙盒模式从以下解析：
   - 每个工具的配置覆盖，或
   - 工具的代码级默认值（`shell`、`code_exec` 和 `browser` 为 `Prefer`）。
4. 执行器要么：
   - 本地运行，
   - 通过 `IToolSandbox` 运行，或
   - 如果工具配置为 `Require` 且没有可用的沙盒，则失败关闭。

`OpenClaw:Sandbox:Provider=None` 是全局关闭开关。设置后，沙盒能力工具在本地运行，即使每个工具的沙盒模式保持配置。

工具执行层：

- 原生进程内工具
- TS/JS 插件桥接
- OpenSandbox 支持的原生工具执行

## 支持的工具

V1 沙盒路由仅覆盖原生高风险工具：

- `shell`
- `code_exec`
- `browser`

JS/TS 桥接工具在此第一阶段不变。

## 构建和启用

OpenSandbox 集成不包含在默认网关/测试构建中。

使用以下命令构建支持沙盒的工件：

```bash
dotnet build -c Release -p:OpenClawEnableOpenSandbox=true src/OpenClaw.Gateway
```

或运行沙盒启用构建的测试：

```bash
dotnet test -c Release -p:OpenClawEnableOpenSandbox=true src/OpenClaw.Tests
```

如果你正在使用 Visual Studio 或直接在 `OpenClaw.Gateway` 上运行 `dotnet run` 而没有此构建标志，不要期望 OpenSandbox 可用，除非你也在配置中显式打开它。

## 配置

OpenSandbox 配置示例：

```json
{
  "OpenClaw": {
    "Sandbox": {
      "Provider": "OpenSandbox",
      "Endpoint": "http://localhost:5000",
      "ApiKey": "env:OPEN_SANDBOX_API_KEY",
      "DefaultTTL": 300,
      "Tools": {
        "shell": {
          "Mode": "Prefer",
          "Template": "alpine:3.20",
          "TTL": 300
        },
        "code_exec": {
          "Mode": "Prefer",
          "Template": "nikolaik/python-nodejs:python3.12-nodejs22-slim",
          "TTL": 300
        },
        "browser": {
          "Mode": "Prefer",
          "Template": "mcr.microsoft.com/playwright:v1.52.0-noble",
          "TTL": 600
        }
      }
    }
  }
}
```

全局强制本地执行：

```json
{
  "OpenClaw": {
    "Sandbox": {
      "Provider": "None"
    }
  }
}
```

这在以下情况下是推荐设置：

- 你正在进行首次本地运行
- 你正在调试核心运行时而不是沙盒
- 你从原始源配置开始并希望可预测的本地行为

更严格的公共绑定 Shell 部署示例：

```json
{
  "OpenClaw": {
    "Sandbox": {
      "Provider": "OpenSandbox",
      "Endpoint": "http://localhost:5000",
      "ApiKey": "env:OPEN_SANDBOX_API_KEY",
      "DefaultTTL": 300,
      "Tools": {
        "shell": {
          "Mode": "Require",
          "Template": "alpine:3.20",
          "TTL": 300
        },
        "code_exec": {
          "Mode": "Prefer",
          "Template": "nikolaik/python-nodejs:python3.12-nodejs22-slim",
          "TTL": 300
        },
        "browser": {
          "Mode": "Prefer",
          "Template": "mcr.microsoft.com/playwright:v1.52.0-noble",
          "TTL": 600
        }
      }
    }
  }
}
```

注意事项：

- `Provider=None` 对沙盒能力工具强制本地执行，是最简单的退出开关。
- `Prefer` 在可用时使用沙盒，如果提供商缺失或暂时不可达则回退到本地执行。
- `Require` 失败关闭，永不回退到本地执行。
- `Template` 当前直接映射到创建租约时传递给 OpenSandbox 的容器镜像 URI。
- `TTL` 是沙盒租约生命周期（秒）。
- 提供的镜像 URI 是起始默认值。如果你需要不同的运行时、加固的镜像或私有注册表控制，请覆盖它们。

## 安全优势

使用 OpenSandbox 降低了对可以执行代码或与不受信任内容交互的工具的主机风险：

- `shell` 命令不再在网关主机上执行
- `code_exec` 代码片段在一次性远程容器中运行
- `browser` 自动化可以将会话状态保持在复用的沙盒租约内，而不是在主机上

对于非环回/公共绑定，`shell` 在 `Require` 模式下使用 `Provider=OpenSandbox` 被网关加固检查视为沙盒化。`Prefer` 仍被视为不安全，因为它可以回退到本地执行。

## 运维注意事项

- 执行器按 `sessionId:toolName` 复用沙盒租约。
- 浏览器自动化在沙盒租约内使用持久配置文件目录，以便多步骤浏览保持状态。
- 网关 `--doctor` 命令在 `Provider=OpenSandbox` 时检查 OpenSandbox 可达性。
- 运行时指标现在暴露沙盒租约创建/复用/恢复计数器。
- 管理事件导出包括脱敏的沙盒执行和租约生命周期上下文用于调试。
- 集成使用原始 `HttpClient` 加源生成的 `System.Text.Json` 模型。没有 OpenSandbox SDK 依赖添加到核心运行时。
