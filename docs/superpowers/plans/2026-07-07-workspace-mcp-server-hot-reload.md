# Workspace MCP Server Hot Reload Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add workspace-scoped non-MCP-App server configuration and hot reload to OpenClaw.NET so operators can add, remove, and update ordinary MCP plugin servers without restarting the gateway.

**Architecture:** Reuse the existing `Plugins:Mcp` registry and tool execution path rather than building a parallel MCP stack. Persist workspace MCP config to the gateway storage path, watch and reload that config into `McpServerToolRegistry`, then atomically swap the live tool surface in the runtime so in-flight requests stay safe.

**Tech Stack:** .NET 10, ASP.NET Core minimal APIs, `ModelContextProtocol.Client`, existing `NativePluginRegistry` and `OpenClawToolExecutor`, xUnit

## Global Constraints

- Keep the scope on ordinary MCP plugin servers configured through `Plugins:Mcp`; do not mix this work with `OpenClaw.McpApp` discovery/manifest hosting.
- Preserve the current MCP App host/proxy behavior on `/apps/health`, `/apps/chat`, and `/apps/mcp/{appId}`.
- Prefer incremental reuse of existing `McpServerToolRegistry`, runtime composition, and admin endpoint patterns over introducing a second runtime registry.
- Hot reload must be safe while requests are in flight; do not require a gateway restart for tool table updates.
- New admin endpoints must continue to use operator auth and CSRF/rate-limit patterns already used by admin mutation endpoints.
- Validation must stay focused: narrow unit/integration tests first, then targeted `dotnet test` filters.

---

## File Structure

### Existing files to modify

- `src/OpenClaw.Agent/IAgentRuntime.cs`
  Responsibility: expose a runtime-level hook for hot-swapping workspace MCP tools.
- `src/OpenClaw.Agent/OpenClawToolExecutor.cs`
  Responsibility: own the live tool dispatch table and atomically replace workspace MCP tools.
- `src/OpenClaw.Agent/Plugins/McpServerToolRegistry.cs`
  Responsibility: extend the existing MCP server registry with workspace-scoped clients, reload diffing, and client lookup by server id.
- `src/OpenClaw.Gateway/Composition/CoreServicesExtensions.cs`
  Responsibility: register workspace MCP config persistence services into DI.
- `src/OpenClaw.Gateway/Composition/RuntimeInitializationExtensions.cs`
  Responsibility: start the watcher during gateway initialization and connect it to the live runtime.
- `src/OpenClaw.Gateway/Endpoints/AdminEndpoints.PluginsAndChannels.cs`
  Responsibility: host the new admin routes for reading and updating workspace MCP server config, since `openclaw.net` no longer has a standalone workspace file endpoint module.
- `src/OpenClaw.Tests/McpServerToolRegistryTests.cs`
  Responsibility: cover reload diffing and client lookup for ordinary MCP servers.
- `src/OpenClaw.Tests/GatewayAdminEndpointTests.cs`
  Responsibility: cover `/admin/workspace/mcp` auth, read shape, validation, persistence, and reload trigger behavior.
- `src/OpenClaw.Tests/GatewayRuntimeLifecycleTests.cs`
  Responsibility: cover watcher lifecycle and disposal behavior if watcher startup/disposal becomes a runtime-owned component.

### New files to create

- `src/OpenClaw.Gateway/Mcp/McpConfigStore.cs`
  Responsibility: persist workspace MCP JSON config to the memory storage path and load it back safely.
- `src/OpenClaw.Gateway/Mcp/McpWatcherHolder.cs`
  Responsibility: bridge DI and admin endpoints to the live watcher instance for explicit reload triggering.
- `src/OpenClaw.Gateway/McpWorkspaceWatcherService.cs`
  Responsibility: debounce reload requests, read workspace MCP config from the config store (and optional workspace-file fallback if adopted), call registry reload, and apply tool changes to the runtime.

## Task 1: Add runtime tool hot-swap primitives

**Files:**
- Modify: `src/OpenClaw.Agent/IAgentRuntime.cs`
- Modify: `src/OpenClaw.Agent/OpenClawToolExecutor.cs`
- Test: `src/OpenClaw.Tests/GatewayRuntimeLifecycleTests.cs`

**Interfaces:**
- Consumes: existing `ITool`, `OpenClawToolExecutor`, runtime implementations (`AgentRuntime` and/or `MafAgentRuntime`)
- Produces: `Task ApplyMcpToolChangesAsync(IReadOnlyList<ITool> toAdd, IReadOnlyList<string> toRemove, CancellationToken ct = default)` on `IAgentRuntime`, plus `ReplaceMcpTools(IReadOnlyList<ITool> toAdd, IReadOnlyList<string> toRemove)` on `OpenClawToolExecutor`

- [ ] **Step 1: Write the failing lifecycle test**

```csharp
[Fact]
public async Task WorkspaceMcpToolSwap_ReplacesDispatchTableWithoutRestart()
{
    var toolA = Substitute.For<ITool>();
    toolA.Name.Returns("workspace_tool_a");
    toolA.Description.Returns("A");
    toolA.ParameterSchema.Returns("{}");

    var toolB = Substitute.For<ITool>();
    toolB.Name.Returns("workspace_tool_b");
    toolB.Description.Returns("B");
    toolB.ParameterSchema.Returns("{}");

    var executor = CreateToolExecutorForTests([toolA]);
    executor.ReplaceMcpTools([toolB], ["workspace_tool_a"]);

    Assert.False(executor.SupportsStreaming("workspace_tool_a"));
    Assert.False(executor.GetToolDeclarations(CreateSession()).Any(t => t.Name == "workspace_tool_a"));
    Assert.True(executor.GetToolDeclarations(CreateSession()).Any(t => t.Name == "workspace_tool_b"));
}
```

- [ ] **Step 2: Run the targeted test to verify it fails**

Run: `dotnet test .\src\OpenClaw.Tests\OpenClaw.Tests.csproj --filter "FullyQualifiedName~WorkspaceMcpToolSwap_ReplacesDispatchTableWithoutRestart" -p:OpenClawSkipDashboardBuild=true`
Expected: FAIL because `ReplaceMcpTools` and/or the test helper path does not exist yet.

- [ ] **Step 3: Add the runtime hook to `IAgentRuntime`**

```csharp
Task ApplyMcpToolChangesAsync(
    IReadOnlyList<OpenClaw.Core.Abstractions.ITool> toAdd,
    IReadOnlyList<string> toRemove,
    CancellationToken ct = default) => Task.CompletedTask;
```

- [ ] **Step 4: Implement atomic MCP tool replacement in `OpenClawToolExecutor`**

```csharp
public void ReplaceMcpTools(IReadOnlyList<ITool> toAdd, IReadOnlyList<string> toRemove)
{
    lock (_toolsMutationLock)
    {
        foreach (var name in toRemove)
            _toolsByName.Remove(name);

        foreach (var tool in toAdd)
            _toolsByName[tool.Name] = tool;

        _toolDeclarations = _toolsByName.Values
            .Select(CreateDeclaration)
            .Cast<AITool>()
            .ToArray();
    }
}
```

- [ ] **Step 5: Wire the concrete runtime implementation**

```csharp
public Task ApplyMcpToolChangesAsync(
    IReadOnlyList<ITool> toAdd,
    IReadOnlyList<string> toRemove,
    CancellationToken ct = default)
{
    _toolExecutor.ReplaceMcpTools(toAdd, toRemove);
    return Task.CompletedTask;
}
```

- [ ] **Step 6: Run the focused lifecycle test again**

Run: `dotnet test .\src\OpenClaw.Tests\OpenClaw.Tests.csproj --filter "FullyQualifiedName~WorkspaceMcpToolSwap_ReplacesDispatchTableWithoutRestart" -p:OpenClawSkipDashboardBuild=true`
Expected: PASS

- [ ] **Step 7: Commit**

```bash
git add src/OpenClaw.Agent/IAgentRuntime.cs src/OpenClaw.Agent/OpenClawToolExecutor.cs src/OpenClaw.Tests/GatewayRuntimeLifecycleTests.cs
git commit -m "feat: add runtime MCP tool hot-swap hook"
```

## Task 2: Extend `McpServerToolRegistry` with workspace reload support

**Files:**
- Modify: `src/OpenClaw.Agent/Plugins/McpServerToolRegistry.cs`
- Test: `src/OpenClaw.Tests/McpServerToolRegistryTests.cs`

**Interfaces:**
- Consumes: existing `McpPluginsConfig`, `McpServerConfig`, `McpNativeTool`, `NativePluginRegistry`
- Produces: `McpClient? GetClientByServerId(string serverId)` and `Task<McpWorkspaceReloadResult> ReloadWorkspaceServersAsync(Dictionary<string, McpServerConfig>? newServers, CancellationToken ct)`

- [ ] **Step 1: Write the failing registry reload tests**

```csharp
[Fact]
public async Task ReloadWorkspaceServersAsync_AddsNewWorkspaceTools_AndRemovesDeletedOnes()
{
    using var registry = CreateRegistryWithConfig(enabled: false);
    var initial = await registry.ReloadWorkspaceServersAsync(new Dictionary<string, McpServerConfig>
    {
        ["alpha"] = CreateHttpServerConfig("https://localhost:5001/mcp")
    }, TestContext.Current.CancellationToken);

    Assert.NotEmpty(initial.AddedTools);

    var second = await registry.ReloadWorkspaceServersAsync(new Dictionary<string, McpServerConfig>(), TestContext.Current.CancellationToken);
    Assert.NotEmpty(second.RemovedToolNames);
}

[Fact]
public async Task GetClientByServerId_ReturnsWorkspaceClientAfterReload()
{
    using var registry = CreateRegistryWithConfig(enabled: false);
    await registry.ReloadWorkspaceServersAsync(new Dictionary<string, McpServerConfig>
    {
        ["alpha"] = CreateHttpServerConfig("https://localhost:5001/mcp")
    }, TestContext.Current.CancellationToken);

    Assert.NotNull(registry.GetClientByServerId("alpha"));
}
```

- [ ] **Step 2: Run the targeted registry tests to verify they fail**

Run: `dotnet test .\src\OpenClaw.Tests\OpenClaw.Tests.csproj --filter "FullyQualifiedName~McpServerToolRegistryTests" -p:OpenClawSkipDashboardBuild=true`
Expected: FAIL because workspace reload APIs do not exist yet.

- [ ] **Step 3: Add workspace-scoped client tracking fields**

```csharp
private readonly Dictionary<string, (McpClient Client, List<DiscoveredMcpTool> Tools, McpServerConfig Config)> _workspaceServers
    = new(StringComparer.Ordinal);

private readonly Dictionary<string, McpClient> _clientsByServerId = new(StringComparer.Ordinal);
```

- [ ] **Step 4: Preserve server-id lookup for built-in configured servers**

```csharp
_clientsByServerId[serverId] = client;
```

- [ ] **Step 5: Add `GetClientByServerId`**

```csharp
public McpClient? GetClientByServerId(string serverId)
{
    if (_clientsByServerId.TryGetValue(serverId, out var configured))
        return configured;

    return _workspaceServers.TryGetValue(serverId, out var workspace) ? workspace.Client : null;
}
```

- [ ] **Step 6: Implement reload diffing and result shape**

```csharp
public async Task<McpWorkspaceReloadResult> ReloadWorkspaceServersAsync(
    Dictionary<string, McpServerConfig>? newServers,
    CancellationToken ct)
{
    // remove changed/deleted servers, connect new ones, build added tools list,
    // and return removed tool names for runtime swap
}
```

Include an explicit result type:

```csharp
public sealed record McpWorkspaceReloadResult(
    IReadOnlyList<OpenClaw.Core.Abstractions.ITool> AddedTools,
    IReadOnlyList<string> RemovedToolNames);
```

- [ ] **Step 7: Run the focused registry tests again**

Run: `dotnet test .\src\OpenClaw.Tests\OpenClaw.Tests.csproj --filter "FullyQualifiedName~McpServerToolRegistryTests" -p:OpenClawSkipDashboardBuild=true`
Expected: PASS

- [ ] **Step 8: Commit**

```bash
git add src/OpenClaw.Agent/Plugins/McpServerToolRegistry.cs src/OpenClaw.Tests/McpServerToolRegistryTests.cs
git commit -m "feat: add workspace MCP registry reload support"
```

## Task 3: Add workspace MCP config persistence and watcher services

**Files:**
- Create: `src/OpenClaw.Gateway/Mcp/McpConfigStore.cs`
- Create: `src/OpenClaw.Gateway/Mcp/McpWatcherHolder.cs`
- Create: `src/OpenClaw.Gateway/McpWorkspaceWatcherService.cs`
- Modify: `src/OpenClaw.Gateway/Composition/CoreServicesExtensions.cs`
- Modify: `src/OpenClaw.Gateway/Composition/RuntimeInitializationExtensions.cs`
- Test: `src/OpenClaw.Tests/GatewayRuntimeLifecycleTests.cs`

**Interfaces:**
- Consumes: `McpServerToolRegistry`, `IAgentRuntime`, `GatewayConfig.Memory.StoragePath`
- Produces: `McpConfigStore.TryLoadRawAsync`, `TryLoadServersAsync`, `SaveAsync`; `McpWorkspaceWatcherService.TriggerReload()` and `Start(CancellationToken)`

- [ ] **Step 1: Write the failing watcher lifecycle tests**

```csharp
[Fact]
public async Task McpWorkspaceWatcherService_TriggerReload_AppliesToolChanges()
{
    var registry = Substitute.For<McpServerToolRegistry>(CreateRegistryCtorArgs());
    var runtime = Substitute.For<IAgentRuntime>();
    var store = Substitute.For<McpConfigStore>("memory", NullLogger<McpConfigStore>.Instance);
    store.TryLoadServersAsync(Arg.Any<CancellationToken>()).Returns(new Dictionary<string, McpServerConfig>());

    var service = new McpWorkspaceWatcherService(
        registry,
        runtime,
        workspacePath: null,
        NullLogger<McpWorkspaceWatcherService>.Instance,
        store);

    using var cts = new CancellationTokenSource();
    service.Start(cts.Token);
    service.TriggerReload();

    await runtime.Received().ApplyMcpToolChangesAsync(Arg.Any<IReadOnlyList<ITool>>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
}
```

- [ ] **Step 2: Run the focused lifecycle tests to verify they fail**

Run: `dotnet test .\src\OpenClaw.Tests\OpenClaw.Tests.csproj --filter "FullyQualifiedName~GatewayRuntimeLifecycleTests" -p:OpenClawSkipDashboardBuild=true`
Expected: FAIL because watcher/config store types do not exist.

- [ ] **Step 3: Create `McpConfigStore`**

```csharp
internal sealed class McpConfigStore
{
    public Task<string?> TryLoadRawAsync(CancellationToken ct = default) { ... }
    public Task<Dictionary<string, McpServerConfig>?> TryLoadServersAsync(CancellationToken ct = default) { ... }
    public Task SaveAsync(string json, CancellationToken ct = default) { ... }
}
```

- [ ] **Step 4: Create `McpWatcherHolder`**

```csharp
internal sealed class McpWatcherHolder
{
    public McpWorkspaceWatcherService? Watcher { get; set; }
}
```

- [ ] **Step 5: Create `McpWorkspaceWatcherService`**

```csharp
internal sealed class McpWorkspaceWatcherService : IDisposable
{
    public void TriggerReload() => _reloadChannel.Writer.TryWrite(true);
    public void Start(CancellationToken stoppingToken) { ... }
}
```

Implement the loop so it:
- reads config from `McpConfigStore`
- optionally falls back to a workspace file path only if you deliberately keep that compatibility path
- calls `ReloadWorkspaceServersAsync`
- applies `AddedTools` and `RemovedToolNames` through `IAgentRuntime.ApplyMcpToolChangesAsync`

- [ ] **Step 6: Register and start the watcher**

In `CoreServicesExtensions`:

```csharp
services.AddSingleton(sp =>
    new McpConfigStore(
        config.Memory.StoragePath,
        sp.GetRequiredService<ILogger<McpConfigStore>>()));
services.AddSingleton<McpWatcherHolder>();
```

In `RuntimeInitializationExtensions`:

```csharp
var mcpWatcher = new McpWorkspaceWatcherService(
    services.McpRegistry,
    agentRuntime,
    startup.WorkspacePath,
    app.Services.GetRequiredService<ILogger<McpWorkspaceWatcherService>>(),
    app.Services.GetRequiredService<McpConfigStore>());
app.Services.GetRequiredService<McpWatcherHolder>().Watcher = mcpWatcher;
mcpWatcher.Start(app.Lifetime.ApplicationStopping);
```

- [ ] **Step 7: Run the focused lifecycle tests again**

Run: `dotnet test .\src\OpenClaw.Tests\OpenClaw.Tests.csproj --filter "FullyQualifiedName~GatewayRuntimeLifecycleTests" -p:OpenClawSkipDashboardBuild=true`
Expected: PASS

- [ ] **Step 8: Commit**

```bash
git add src/OpenClaw.Gateway/Mcp/McpConfigStore.cs src/OpenClaw.Gateway/Mcp/McpWatcherHolder.cs src/OpenClaw.Gateway/McpWorkspaceWatcherService.cs src/OpenClaw.Gateway/Composition/CoreServicesExtensions.cs src/OpenClaw.Gateway/Composition/RuntimeInitializationExtensions.cs src/OpenClaw.Tests/GatewayRuntimeLifecycleTests.cs
git commit -m "feat: add workspace MCP watcher services"
```

## Task 4: Expose admin endpoints for workspace MCP config

**Files:**
- Modify: `src/OpenClaw.Gateway/Endpoints/AdminEndpoints.PluginsAndChannels.cs`
- Test: `src/OpenClaw.Tests/GatewayAdminEndpointTests.cs`

**Interfaces:**
- Consumes: `McpConfigStore`, `McpWatcherHolder`, `EndpointHelpers.AuthorizeOperatorRequest`, operator rate-limit helpers
- Produces: `GET /admin/workspace/mcp` and `PUT /admin/workspace/mcp`

- [ ] **Step 1: Write the failing admin endpoint tests**

```csharp
[Fact]
public async Task WorkspaceMcp_AdminApi_RequiresAuth_AndPersistsConfig()
{
    await using var harness = await CreateHarnessAsync(nonLoopbackBind: true);

    var anonymous = await harness.Client.GetAsync("/admin/workspace/mcp");
    Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

    using var getRequest = new HttpRequestMessage(HttpMethod.Get, "/admin/workspace/mcp");
    getRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", harness.AuthToken);
    var getResponse = await harness.Client.SendAsync(getRequest);
    Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);

    var (cookie, csrfToken) = await LoginAsync(harness.Client, harness.AuthToken);
    using var putRequest = new HttpRequestMessage(HttpMethod.Put, "/admin/workspace/mcp")
    {
        Content = JsonContent("""{"enabled":true,"servers":{}}""")
    };
    putRequest.Headers.Add("Cookie", cookie);
    putRequest.Headers.Add(BrowserSessionAuthService.CsrfHeaderName, csrfToken);

    var putResponse = await harness.Client.SendAsync(putRequest);
    Assert.Equal(HttpStatusCode.OK, putResponse.StatusCode);
}
```

- [ ] **Step 2: Run the targeted admin tests to verify they fail**

Run: `dotnet test .\src\OpenClaw.Tests\OpenClaw.Tests.csproj --filter "FullyQualifiedName~GatewayAdminEndpointTests.WorkspaceMcp" -p:OpenClawSkipDashboardBuild=true`
Expected: FAIL because the routes do not exist.

- [ ] **Step 3: Add `GET /admin/workspace/mcp`**

Return:
- sanitized built-in `Plugins:Mcp` config from appsettings, with auth-bearing headers stripped/reduced to `HasToken`
- raw persisted user config from `McpConfigStore` if present

- [ ] **Step 4: Add `PUT /admin/workspace/mcp`**

Behavior:
- require operator auth with CSRF
- enforce existing operator rate limit helper
- require a non-empty request body
- validate parseable JSON
- persist through `McpConfigStore.SaveAsync`
- trigger watcher reload through `McpWatcherHolder.Watcher?.TriggerReload()`
- append an audit entry named `workspace_mcp_update`

- [ ] **Step 5: Run the focused admin tests again**

Run: `dotnet test .\src\OpenClaw.Tests\OpenClaw.Tests.csproj --filter "FullyQualifiedName~GatewayAdminEndpointTests.WorkspaceMcp" -p:OpenClawSkipDashboardBuild=true`
Expected: PASS

- [ ] **Step 6: Commit**

```bash
git add src/OpenClaw.Gateway/Endpoints/AdminEndpoints.PluginsAndChannels.cs src/OpenClaw.Tests/GatewayAdminEndpointTests.cs
git commit -m "feat: add admin workspace MCP config endpoints"
```

## Task 5: End-to-end regression pass and docs follow-up

**Files:**
- Modify: `docs/MCPAPP.md` (only if needed to clarify that this new feature is for ordinary MCP plugin servers, not MCP Apps)
- Modify: `docs/TOOLS_GUIDE.md` or a more precise admin/workspace doc if a new operator-facing note is warranted
- Test: `src/OpenClaw.Tests/McpServerToolRegistryTests.cs`
- Test: `src/OpenClaw.Tests/GatewayAdminEndpointTests.cs`
- Test: `src/OpenClaw.Tests/GatewayRuntimeLifecycleTests.cs`

**Interfaces:**
- Consumes: all earlier tasks
- Produces: documented distinction between workspace MCP plugin servers and MCP Apps, plus focused regression evidence

- [ ] **Step 1: Run the focused combined regression slice**

Run: `dotnet test .\src\OpenClaw.Tests\OpenClaw.Tests.csproj --filter "FullyQualifiedName~McpServerToolRegistryTests|FullyQualifiedName~GatewayAdminEndpointTests|FullyQualifiedName~GatewayRuntimeLifecycleTests" -p:OpenClawSkipDashboardBuild=true`
Expected: PASS

- [ ] **Step 2: Add a doc clarification if the admin surface would otherwise be confused with MCP Apps**

Suggested note:

```md
Workspace MCP server configuration under `/admin/workspace/mcp` applies to ordinary `Plugins:Mcp` servers and hot-reloads their tool surface. It is separate from manifest-discovered MCP Apps hosted through `OpenClaw.McpApp` and `/apps/*`.
```

- [ ] **Step 3: Re-run the focused regression slice after any doc-adjacent code changes**

Run: `dotnet test .\src\OpenClaw.Tests\OpenClaw.Tests.csproj --filter "FullyQualifiedName~McpServerToolRegistryTests|FullyQualifiedName~GatewayAdminEndpointTests|FullyQualifiedName~GatewayRuntimeLifecycleTests" -p:OpenClawSkipDashboardBuild=true`
Expected: PASS

- [ ] **Step 4: Commit**

```bash
git add docs/MCPAPP.md docs/TOOLS_GUIDE.md src/OpenClaw.Tests/McpServerToolRegistryTests.cs src/OpenClaw.Tests/GatewayAdminEndpointTests.cs src/OpenClaw.Tests/GatewayRuntimeLifecycleTests.cs
git commit -m "docs: clarify workspace MCP server hot reload surface"
```

## Self-Review

- Spec coverage: this plan covers the missing larger capability slice identified during the audit: workspace-scoped ordinary MCP server config persistence, watcher-driven reload, runtime tool replacement, and admin mutation surfaces. It intentionally excludes already-complete MCP App host/proxy behavior.
- Placeholder scan: no `TODO`/`TBD` placeholders remain; remaining implementation-heavy steps name exact target types and commands.
- Type consistency: `ApplyMcpToolChangesAsync`, `ReplaceMcpTools`, `GetClientByServerId`, `ReloadWorkspaceServersAsync`, `McpWorkspaceReloadResult`, `McpConfigStore`, and `McpWorkspaceWatcherService` are defined consistently across tasks.

## Execution Handoff

Plan complete and saved to `docs/superpowers/plans/2026-07-07-workspace-mcp-server-hot-reload.md`. Two execution options:

**1. Subagent-Driven (recommended)** - I dispatch a fresh subagent per task, review between tasks, fast iteration

**2. Inline Execution** - Execute tasks in this session using executing-plans, batch execution with checkpoints

Which approach?