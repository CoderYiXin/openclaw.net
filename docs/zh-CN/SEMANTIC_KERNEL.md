# Semantic Kernel 互操作

OpenClaw.NET 可以在 OpenClaw 的工具执行模型后面托管 `Microsoft.SemanticKernel` 代码。

此仓库故意保持 Semantic Kernel 集成**可选**：
- `src/OpenClaw.Gateway` 保持无 SK 并保持 **NativeAOT 友好**。
- SK 支持位于 `src/OpenClaw.SemanticKernelAdapter` 中，专为非 AOT 主机设计。

## 此处"互操作"的含义

OpenClaw 不试图替换 Semantic Kernel。

相反，SK 在工具调用*内部*运行，因此 OpenClaw 仍然可以强制执行：
- 认证和网关策略（在主机中）
- 工具审批和允许/拒绝策略
- 速率限制 / 预算（在主机中）
- 围绕执行的 OpenTelemetry 追踪

## 已知良好的配置

| 场景 | 支持 | 说明 |
|---|---:|---|
| OpenClaw 网关 NativeAOT 发布 | 是 | 网关中无 SK 依赖。 |
| 通过适配器库进行 SK 互操作 | 是 | 适用于普通 .NET 应用（非 AOT）。 |
| 示例主机 (`samples/OpenClaw.SemanticKernelInteropHost`) | 是 | 自包含演示；不适用于 NativeAOT。 |

## NativeAOT / 裁剪指导

Semantic Kernel 和一些 SK 插件模式可能依赖反射和动态行为。

建议：
1. 将 SK 互操作保持在单独的、非 AOT 主机进程中（推荐）。
2. 如果你仍然想要 AOT：为 SK 主机项目禁用裁剪，并接受更大的二进制文件。
3. 将 SK 互操作视为尽力而为，并在你选择的发布设置下验证你的确切插件集。

## 包

- `OpenClaw.SemanticKernelAdapter`
  - 提供：
    - `semantic_kernel` 入口工具（按插件/函数调用）
    - 名为 `sk_<plugin>_<function>` 的每个函数工具
    - 可选的治理钩子（`SemanticKernelPolicyHook`），使用 `IToolHookWithContext`

## 生产就绪
`OpenClaw.SemanticKernelAdapter` 当前实现了 Semantic Kernel 互操作路线图的**所有阶段**。它被认为是健壮的且生产就绪的，可处理选择性映射、速率限制和 `IStreamingTool` 上下文钩子。
