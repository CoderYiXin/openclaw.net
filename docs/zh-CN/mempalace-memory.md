# MemPalace.NET 记忆提供商

OpenClaw 可以使用 [ElBruno.MempalaceNet](https://github.com/elbruno/ElBruno.MempalaceNet) 作为可选的 JIT 专用记忆后端，用于持久化笔记和时间知识图谱事实。

MemPalace 不受默认的 NativeAOT 网关构建支持。如果 `OpenClaw:Memory:Provider` 为 `mempalace`，则以 JIT 模式运行网关，并将 `OpenClaw.Plugins.Mempalace` 作为动态原生插件加载。

## 启用方式

将记忆提供商设置为 `mempalace`：

```json
{
  "OpenClaw": {
    "Runtime": {
      "Mode": "jit"
    },
    "Memory": {
      "Provider": "mempalace",
      "Mempalace": {
        "BasePath": "./memory/mempalace",
        "PalaceId": "openclaw",
        "CollectionName": "memories",
        "EmbeddingDimensions": 384,
        "KnowledgeGraphDbPath": "./memory/mempalace/kg.db",
        "SessionDbPath": "./memory/mempalace/openclaw-sessions.db"
      }
    },
    "Plugins": {
      "DynamicNative": {
        "Enabled": true,
        "Load": {
          "Paths": ["./plugins/openclaw-mempalace"]
        }
      }
    }
  }
}
```

现有的 `file` 和 `sqlite` 提供商仍然是默认值，保持不变。

将 MemPalace 插件构建或发布到配置的插件目录中：

```bash
dotnet publish src/OpenClaw.Plugins.Mempalace -c Release -o ./plugins/openclaw-mempalace
```

使用该配置以 JIT 模式运行网关：

```bash
dotnet run --project src/OpenClaw.Gateway -c Release -- --config <path-to-config>
```

如果为此通道发布网关，请禁用该发布的 NativeAOT，因为动态原生插件在 AOT 模式下被阻止：

```bash
dotnet publish src/OpenClaw.Gateway -c Release -p:PublishAot=false -o <output-dir>
```

除非动态原生插件已启用且有效运行时模式为 JIT，否则默认的 NativeAOT 网关对此提供商会故意快速失败。

## 插件架构

MemPalace 集成位于 `src/OpenClaw.Plugins.Mempalace`，而不是 `src/OpenClaw.Gateway`。Gateway 项目不得在编译时引用 MemPalace 包或 MemPalace 类型。

`OpenClaw.Plugins.Mempalace` 是一个动态原生插件程序集，插件 DLL 旁有 `openclaw.native-plugin.json`。其清单指向 `OpenClaw.Plugins.Mempalace.MempalaceMemoryPlugin`，并声明 `memory` 和 `tools` 两种能力。

启动时，当 `OpenClaw:Memory:Provider` 为 `mempalace` 时，Gateway 创建一个 `NativeDynamicPluginHost` 并调用 `LoadMemoryProvidersAsync`。该方法加载完整的动态原生插件表面，因此同一个插件实例注册了：

- `RegisterMemoryProvider("mempalace", ...)` 用于 `IMemoryStore` 工厂。
- `RegisterTool(...)` 用于 `mempalace_kg` 工具。

Gateway 在启动上下文中存储那个已加载的 `NativeDynamicPluginHost`，并在正常的插件组合过程中复用它。这避免了加载 MemPalace 插件两次，并保持注册的工具与记忆提供商共享同一个延迟创建的 MemPalace 存储。

`MempalaceKnowledgeGraphTool` 是唯一的知识图谱工具类。它可以为聚焦测试直接使用 `IKnowledgeGraph` 构造，或者使用 `MempalaceMemoryPlugin` 使用的延迟提供程序，以便工具执行可以在记忆提供商创建后解析活动的 MemPalace 存储。

## MemPalace 中存储的内容

- 通过 `memory`、`memory_get`、`memory_search` 和 `project_memory` 写入的持久化记忆笔记。
- 笔记记录存储在配置中的 palace/collection 名称下的 MemPalace SQLite 集合中。
- 笔记被投影到 wings / rooms / drawers 层次结构中：
  - `project:demo:decision` 变为 wing `project`，room `demo`，drawer `decision`。
  - 段数不足的键使用 `DefaultWing` 和 `DefaultRoom`。
- 每个保存的笔记记录时间 KG 关系：
  - `memory:<key> stored-in drawer:<drawer>`
  - `drawer:<drawer> located-in room:<room>`
  - `room:<room> located-in wing:<wing>`

会话历史、分支、管理列表/搜索和保留通过 OpenClaw 现有的 SQLite 会话存储继续，以保持网关兼容性。

## 工具

当提供商通过动态原生插件加载时，OpenClaw 也会注册 `mempalace_kg`：

- `add` 使用 `subject`、`predicate` 和 `object` 写入时间三元组。
- `query` 通过可选的 `subject`、`predicate`、`object` 和 `at` 读取三元组。
- `timeline` 列出 `entity` 的关系，可选以 `from` 和 `to` 限定。

实体使用 MemPalace 的 `type:id` 格式，例如 `agent:openclaw` 或 `memory:project:demo:decision`。

## AOT 和依赖影响

集成隔离在 `src/OpenClaw.Plugins.Mempalace` 中，并通过 `INativeDynamicPlugin` 加载。`src/OpenClaw.Gateway` 在编译时不引用 MemPalace 程序集或包。插件仅将 `MemPalace.Core`、`MemPalace.Backends.Sqlite` 和 `MemPalace.KnowledgeGraph` 添加到可选的 JIT 插件输出，而不是默认的 NativeAOT 网关构建。

适配器使用确定性的本地哈希嵌入器，因此启用它不需要云调用或 API 密钥要求。将 MemPalace 提供商视为 JIT 专用可选通道，除非 MemPalace 本身获得验证的 NativeAOT 支持。

插件项目设置 `CopyLocalLockFileAssemblies=true`，以便构建输出包含 NuGet 依赖程序集，如 `MemPalace.KnowledgeGraph.dll`、`MemPalace.Core.dll`、`MemPalace.Backends.Sqlite.dll`、`Microsoft.Data.Sqlite.dll`、`SQLitePCLRaw.*` 和 `runtimes/` 文件夹。更改项目文件时保持该设置；`AssemblyDependencyResolver` 从插件输出目录解析动态插件依赖。

## 故障排除

如果 Gateway 在启动期间记录如下错误：

```text
Failed to load dynamic native plugin 'openclaw-mempalace-memory'
System.IO.FileNotFoundException: Could not load file or assembly 'MemPalace.KnowledgeGraph, Version=0.15.0.0'
```

这意味着找到了插件 DLL，但配置的插件目录中缺少一个或多个插件依赖 DLL。重新构建或发布插件，并验证配置的 `OpenClaw:Plugins:DynamicNative:Load:Paths` 目录包含 `openclaw.native-plugin.json`、`OpenClaw.Plugins.Mempalace.dll` 和 `MemPalace.*.dll` 依赖项。

如果 Gateway 报告没有动态原生记忆提供商注册 `mempalace`，请检查以下项目：

- `OpenClaw:Runtime:Mode` 为 `jit`。
- `OpenClaw:Plugins:DynamicNative:Enabled` 为 `true`。
- `OpenClaw:Plugins:DynamicNative:Load:Paths` 指向插件输出目录，而不仅仅是包含源代码但没有构建 DLL 的目录。
- `openclaw.native-plugin.json` 中 `assemblyPath` 设置为 `OpenClaw.Plugins.Mempalace.dll`，`typeName` 设置为 `OpenClaw.Plugins.Mempalace.MempalaceMemoryPlugin`。
