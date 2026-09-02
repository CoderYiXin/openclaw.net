# 模型配置文件与 Gemma

OpenClaw 通过现有的提供商接缝（而非创建 Gemma 特定的运行时分支）集成 **Gemma 系列模型,包括 Gemma 4**。

该设计保持：

- 一个执行栈
- 一个工具调用栈
- 一个会话/压缩/中间件栈
- 一个 MAF 集成路径

Gemma 被视为一个**模型后端**，可以通过以下方式访问：

1. **Ollama** 用于本地和开发工作流
2. **OpenAI 兼容端点** 用于生产或自托管推理网关
3. **embedded** 用于 OpenClaw 管理的本地包和 sidecar 推理
4. 未来如有需要可添加提供商扩展，而不改变运行时架构

## 为什么存在配置文件

提供商和模型不暴露相同的能力。一个需要工具调用、结构化输出和图像输入的路由不应静默地运行在仅支持纯文本聊天的模型上。

模型配置文件让 OpenClaw 可以独立于提供商传输来描述模型实例：

- 配置文件 id
- 提供商 id
- 模型 id
- 基础 URL
- API 密钥或环境引用
- 能力
- 上下文/输出提示
- 标签，如 `local`、`private`、`cheap`、`tool-reliable`、`vision`

运行时使用这些配置文件来：

- 显式选择配置文件
- 根据路由/会话能力需求选择配置文件
- 优先选择 `local` 或 `private` 等标签
- 在允许时回退到另一个配置文件
- 当没有配置文件能安全满足请求时明确失败

## OpenAI 兼容请求映射

当客户端调用 OpenClaw 的 OpenAI 兼容 HTTP 路由时，请求的 `model` 字段被解释为：

- 如果匹配配置的 OpenClaw 配置文件，则为模型配置文件 id，或
- 如果不匹配，则为字面上游模型 id 覆盖。

如果请求省略 `model`，OpenClaw 回退到配置的默认配置文件或 `OpenClaw:Llm:Model`。

这对下游集成很重要：

- `"default"` 不是"使用你配置的默认值"的内置哨兵。
- `"default"` 仅在你定义了 id 为 `default` 的配置文件时才有效。
- 如果你想要网关默认路由，省略 `model`。

## 配置示例

```json
{
  "OpenClaw": {
    "Llm": {
      "Provider": "openai",
      "Model": "gpt-4.1"
    },
    "Models": {
      "DefaultProfile": "gemma4-prod",
      "Profiles": [
        {
          "Id": "gemma4-local",
          "Provider": "ollama",
          "Model": "gemma4",
          "BaseUrl": "http://localhost:11434/v1",
          "Tags": ["local", "private", "cheap"],
          "Capabilities": {
            "SupportsTools": false,
            "SupportsVision": true,
            "SupportsJsonSchema": false,
            "SupportsStructuredOutputs": false,
            "SupportsStreaming": true,
            "SupportsParallelToolCalls": false,
            "SupportsReasoningEffort": false,
            "SupportsSystemMessages": true,
            "SupportsImageInput": true,
            "SupportsAudioInput": false,
            "MaxContextTokens": 131072,
            "MaxOutputTokens": 8192
          }
        },
        {
          "Id": "gemma4-prod",
          "Provider": "openai-compatible",
          "Model": "gemma-4",
          "BaseUrl": "https://your-inference-gateway.example.com/v1",
          "ApiKey": "env:MODEL_PROVIDER_KEY",
          "Tags": ["private", "prod", "vision"],
          "FallbackProfileIds": ["frontier-tools"],
          "Capabilities": {
            "SupportsTools": true,
            "SupportsVision": true,
            "SupportsJsonSchema": true,
            "SupportsStructuredOutputs": true,
            "SupportsStreaming": true,
            "SupportsParallelToolCalls": true,
            "SupportsReasoningEffort": false,
            "SupportsSystemMessages": true,
            "SupportsImageInput": true,
            "SupportsAudioInput": false,
            "MaxContextTokens": 262144,
            "MaxOutputTokens": 16384
          }
        },
        {
          "Id": "frontier-tools",
          "Provider": "openai",
          "Model": "gpt-4.1",
          "Tags": ["tool-reliable", "frontier"],
          "Capabilities": {
            "SupportsTools": true,
            "SupportsVision": true,
            "SupportsJsonSchema": true,
            "SupportsStructuredOutputs": true,
            "SupportsStreaming": true,
            "SupportsParallelToolCalls": true,
            "SupportsReasoningEffort": true,
            "SupportsSystemMessages": true,
            "SupportsImageInput": true,
            "SupportsAudioInput": true,
            "MaxContextTokens": 1000000,
            "MaxOutputTokens": 32768
          }
        }
      ]
    },
    "Routing": {
      "Enabled": true,
      "Routes": {
        "telegram:private-coder": {
          "ChannelId": "telegram",
          "SenderId": "private-coder",
          "ModelProfileId": "gemma4-local",
          "PreferredModelTags": ["local", "private"],
          "FallbackModelProfileIds": ["frontier-tools"],
          "ModelRequirements": {
            "SupportsTools": true,
            "SupportsStreaming": true
          }
        }
      }
    }
  }
}
```

## 通过 Ollama 使用 Gemma

当你想要为开发或工作站部署进行本地/私密推理时使用此方式。

```json
{
  "Id": "gemma4-local",
  "Provider": "ollama",
  "Model": "gemma4",
  "BaseUrl": "http://localhost:11434/v1",
  "Tags": ["local", "private", "cheap"]
}
```

注意事项：

- OpenClaw 通过现有的 OpenAI 兼容适配器路径与 Ollama 通信。
- 如果被旧版提供商配置省略，`BaseUrl` 默认为 `http://localhost:11434/v1`，但对于命名配置文件，显式设置更清晰。
- 如果配置文件不广告 `SupportsTools`，需要工具的路由将明确失败或回退。

## 通过嵌入式本地推理使用 Gemma

当你希望 OpenClaw 管理本地模型包、缓存和 sidecar 生命周期时使用此方式。

```json
{
  "Id": "embedded-local",
  "PresetId": "embedded-gemma-4-e4b",
  "Provider": "embedded",
  "Model": "gemma-4-e4b",
  "Tags": ["local", "private", "offline"],
  "FallbackProfileIds": ["frontier-tools"]
}
```

注意事项：

- `openclaw models packages` 列出可安装的包、后端、上下文、校验和和实验状态。
- GGUF 包通过受监督的 `llama-server` sidecar 运行。
- LiteRT-LM 包是实验性的，需要 `OpenClaw:LocalInference:LiteRtRuntimePath` 指向 OpenClaw 兼容的适配器二进制文件；OpenClaw 不假定使用通用 `litert-server`。
- 嵌入式视频支持在 v1 中是基于帧的：OpenClaw 使用 `ffprobe`/`ffmpeg` 对本地 `video/*` 内容进行采样，将帧写入媒体缓存，并向 sidecar 发送有序的 `image_url` 帧部分。
- 仅当嵌入式模型支持图像输入且 `OpenClaw:Multimodal:Video:Enabled` 为 true 时，配置文件才广告视频输入。

## 通过 OpenAI 兼容网关使用 Gemma

当 Gemma 托管在暴露 OpenAI 兼容 API 的生产推理服务后面时使用此方式。
