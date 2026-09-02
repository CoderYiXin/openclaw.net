# 提示缓存

OpenClaw.NET 支持提示缓存，作为在现有提供商和模型配置文件架构之上分层的提供商感知优化。运行时仍然通过相同的 `ILlmExecutionService` 和模型选择流程与提供商通信。提示缓存仅改变请求整形、规范化的用量核算和可选的 keep-warm 行为。

## 为什么存在

提示缓存在请求的大部分前缀在多个回合之间保持稳定时提供帮助：

- 基础系统提示
- 工具声明
- 技能提示内容
- 稳定的工作区提示文件

当上游提供商支持提示缓存时，OpenClaw 可以附加缓存提示并将返回的缓存用量规范化为：

- `cacheRead`
- `cacheWrite`

这改善了长时间运行会话的成本和延迟可见性，而无需引入提供商特定的运行时分支。

## 配置

提示缓存可以全局配置：

```json
{
  "OpenClaw": {
    "Llm": {
      "Provider": "openai",
      "Model": "gpt-4.1",
      "PromptCaching": {
        "Enabled": true,
        "Retention": "auto",
        "Dialect": "openai",
        "KeepWarmEnabled": false,
        "KeepWarmIntervalMinutes": 55,
        "TraceEnabled": false,
        "TraceFilePath": "./memory/logs/cache-trace.jsonl"
      }
    }
  }
}
```

或按模型配置文件：

```json
{
  "OpenClaw": {
    "Models": {
      "DefaultProfile": "gemma4-prod",
      "Profiles": [
        {
          "Id": "gemma4-prod",
          "Provider": "openai-compatible",
          "Model": "gemma-4",
          "BaseUrl": "https://gateway.example.com/v1",
          "ApiKey": "env:MODEL_PROVIDER_KEY",
          "PromptCaching": {
            "Enabled": true,
            "Dialect": "openai",
            "Retention": "auto"
          }
        },
        {
          "Id": "claude-research",
          "Provider": "anthropic",
          "Model": "claude-sonnet-4.5",
          "PromptCaching": {
            "Enabled": true,
            "Dialect": "anthropic",
            "Retention": "long",
            "KeepWarmEnabled": true,
            "KeepWarmIntervalMinutes": 55
          }
        }
      ]
    }
  }
}
```

配置文件设置逐字段覆盖全局 `OpenClaw:Llm:PromptCaching` 值。

## 支持的字段

- `Enabled`：为该范围打开提示缓存行为
- `Retention`：`none`、`short`、`long` 或 `auto`
- `Dialect`：`auto`、`openai`、`anthropic`、`gemini` 或 `none`
- `KeepWarmEnabled`：为符合条件的提供商启用选择性 keep-warm
- `KeepWarmIntervalMinutes`：最小预热间隔
- `TraceEnabled`：发出缓存追踪 JSONL 条目
- `TraceFilePath`：可选的追踪输出路径

## 提供商行为

### OpenAI 和 Azure OpenAI

- 通过请求附加属性使用确定性缓存键提示
- 将提供商报告的缓存提示令牌规范化为 `cacheRead`
- 当提供商未报告时不编造 `cacheWrite`

### OpenAI 兼容

- 仅当 `Dialect` 显式设置为 `openai` 时才启用提示缓存
- 如果启用了提示缓存但方言保持 `auto`，配置验证和医生模式在运行前发出警告

### Anthropic 和 Anthropic Vertex

- 使用 Anthropic 风格的缓存提示
- 在报告时映射提供商缓存读取和缓存创建/写入用量
- 当显式启用时有资格进行 keep-warm

### Amazon Bedrock

- Bedrock 作为提供商 id 可用于缓存策略路由和验证
- Anthropic 风格的缓存行为仅适用于 Bedrock 兼容端点或适配器后面的 Anthropic Claude 模型
- 非 Anthropic Bedrock 模型在保留/keep-warm 方面被视为无缓存

### Gemini

- 使用 Gemini 缓存方言提示和规范化的缓存核算
- 当显式启用时有资格进行 keep-warm

### Ollama

- v1 中无提示缓存行为
- 模型能力反映提示缓存不受支持

### 动态 / 插件提供商

- 提示缓存提示通过 `ChatOptions.AdditionalProperties` 传递
- 提供商必须显式选择缓存方言
- 如果提供商返回带有缓存字段的用量计数器，OpenClaw 将其规范化为 `cacheRead` / `cacheWrite`

## 诊断

提示缓存用量在以下位置显示：

- `/metrics/providers`
- `/doctor/text`
- 会话状态摘要
- `/status` 和 `/usage` 命令输出

如果实时会话缓存总计缺失，OpenClaw 回退到该会话提供商用量历史中记录的最新非零缓存计数器。

## 缓存追踪

缓存追踪可以通过配置启用：

```json
{
  "OpenClaw": {
    "Diagnostics": {
      "CacheTrace": {
        "Enabled": true,
        "FilePath": "./memory/logs/cache-trace.jsonl",
        "IncludeMessages": true,
        "IncludePrompt": true,
        "IncludeSystem": true
      }
    }
  }
}
```

或通过环境变量：

- `OPENCLAW_CACHE_TRACE=1`
- `OPENCLAW_CACHE_TRACE_FILE=/path/to/cache-trace.jsonl`
- `OPENCLAW_CACHE_TRACE_PROMPT=0|1`
- `OPENCLAW_CACHE_TRACE_SYSTEM=0|1`

追踪输出为 JSONL 格式，包括：

- 选择的配置文件/提供商/模型
- 方言和保留
- 稳定指纹
- 规范化的缓存用量计数器

## Keep-warm

Keep-warm 在 v1 中故意保持保守。

- 它在专用的后台服务中运行
- 仅预热具有近期稳定提示指纹的活动会话
- 仅预热明确设置 `KeepWarmEnabled=true` 的配置文件
- 仅适用于具有显式 TTL 或缓存资源语义的提供商

未明确符合条件的提供商将被跳过，不会使正常请求失败。
