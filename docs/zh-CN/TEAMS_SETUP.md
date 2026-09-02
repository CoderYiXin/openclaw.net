# Microsoft Teams 频道设置

OpenClaw 通过 Azure Bot Framework 支持 Microsoft Teams 作为原生频道。机器人通过 HTTPS webhook 接收消息，并通过 Bot Connector REST API 回复。

## 先决条件

- Azure 账户（免费层即可）
- 一个公网可访问的 HTTPS 端点（Cloudflare Tunnel、ngrok 或 Tailscale Funnel）
- 初始设置约需 1-2 小时

## 步骤 1：创建 Azure Bot

1. 前往 [Azure Portal](https://portal.azure.com) 并创建 **Azure Bot** 资源
2. 设置：
   - **定价层**：Free (F0)
   - **应用类型**：Single Tenant
   - **创建类型**：Create new Microsoft App ID
3. 创建后，收集三个凭据：
   - **App ID** — 来自 Bot 配置页面
   - **Client Secret** — 在应用注册的 Certificates & Secrets 下创建
   - **Tenant ID** — 来自应用注册 Overview 页面

## 步骤 2：配置 OpenClaw

添加到 `appsettings.json`：

```json
{
  "Channels": {
    "Teams": {
      "Enabled": true,
      "AppId": null,
      "AppIdRef": "env:TEAMS_APP_ID",
      "AppPassword": null,
      "AppPasswordRef": "env:TEAMS_APP_PASSWORD",
      "TenantId": null,
      "TenantIdRef": "env:TEAMS_TENANT_ID",
      "WebhookPath": "/api/messages",
      "DmPolicy": "pairing",
      "GroupPolicy": "allowlist",
      "RequireMention": true
    }
  }
}
```

设置环境变量：

```bash
export TEAMS_APP_ID="<your-app-id>"
export TEAMS_APP_PASSWORD="<your-client-secret>"
export TEAMS_TENANT_ID="<your-tenant-id>"
```

## 步骤 3：暴露你的 Webhook

Teams 通过 HTTPS 向你的机器人发送消息。你需要一个指向网关的公共 URL。

**Cloudflare Tunnel**（推荐用于持久性）：
```bash
brew install cloudflared
cloudflared tunnel create your-bot-name
# 配置路由到 localhost:18789（默认网关端口）
```

**ngrok**（快速开发测试）：
```bash
ngrok http 18789
```

## 步骤 4：设置消息端点

在 Azure Portal → 你的 Bot 资源 → **Configuration**：

将 **Messaging endpoint** 设置为：`https://yourdomain.com/api/messages`

## 步骤 5：启用 Teams 频道

Azure Portal → 你的 Bot → **Channels** → 点击 **Microsoft Teams** → Configure → Accept Terms → Save。

## 步骤 6：创建 Teams 应用包

创建 `manifest.json`：

```json
{
  "$schema": "https://developer.microsoft.com/en-us/json-schemas/teams/v1.17/MicrosoftTeams.schema.json",
  "manifestVersion": "1.17",
  "version": "1.0.0",
  "id": "<YOUR_APP_ID>",
  "developer": {
    "name": "OpenClaw",
    "websiteUrl": "https://openclaw.ai",
    "privacyUrl": "https://openclaw.ai/privacy",
    "termsOfUseUrl": "https://openclaw.ai/terms"
  },
  "name": { "short": "OpenClaw Bot" },
  "description": {
    "short": "由 OpenClaw 驱动的 AI 助手",
    "full": "一个驻留在你的 Teams 工作区中的 AI 助手。"
  },
  "icons": {
    "outline": "outline.png",
    "color": "color.png"
  },
  "accentColor": "#4F46E5",
  "bots": [{
    "botId": "<YOUR_APP_ID>",
    "scopes": ["personal", "team", "groupChat"],
    "supportsFiles": true,
    "commandLists": []
  }],
  "permissions": ["messageTeamMembers"],
  "validDomains": [],
  "authorization": {
    "permissions": {
      "resourceSpecific": [
        { "name": "ChannelMessage.Read.Group", "type": "Application" },
        { "name": "ChannelMessage.Send.Group", "type": "Application" },
        { "name": "ChatMessage.Read.Chat", "type": "Application" }
      ]
    }
  }
}
```

创建两个图标文件：
- `outline.png`（32x32，透明背景）
- `color.png`（192x192）

将所有三个文件打包成 `.zip` 文件。

## 步骤 7：上传到 Teams

在 Teams 中 → **Apps** → **Manage your apps** → **Upload a custom app** → 选择你的 ZIP。

如果侧载受限，改为使用 **Teams Admin Center**。

**重要**：上传后，将应用安装到你要在其中激活它的每个团队中。RSC 权限仅按安装生效。

## 配置参考

| 字段 | 默认值 | 描述 |
|-------|---------|-------------|
| `Enabled` | `false` | 主开关 |
| `DmPolicy` | `"pairing"` | 一对一 DM 使用 `"open"`、`"pairing"` 或 `"closed"` |
| `GroupPolicy` | `"allowlist"` | 频道/群组使用 `"open"`、`"allowlist"` 或 `"disabled"` |
| `AppId` / `AppIdRef` | `env:TEAMS_APP_ID` | Azure Bot App ID |
| `AppPassword` / `AppPasswordRef` | `env:TEAMS_APP_PASSWORD` | Azure Bot Client Secret |
| `TenantId` / `TenantIdRef` | `env:TEAMS_TENANT_ID` | Azure AD Tenant ID |
| `WebhookPath` | `"/api/messages"` | 入站 webhook 路由 |
| `ValidateToken` | `true` | 验证入站请求的 JWT（本地开发可禁用） |
| `RequireMention` | `true` | 在团队频道和群聊中要求 @提及 |
| `ReplyStyle` | `"thread"` | `"thread"`（在帖子中回复）或 `"top-level"`（新消息） |
| `TextChunkLimit` | `4000` | 每个出站消息的最大字符数，超过则分块 |
| `ChunkMode` | `"length"` | `"length"` 或 `"newline"` |
| `AllowedTenantIds` | `[]` | 限制到特定的 Azure AD 租户 |
| `AllowedFromIds` | `[]` | 发送者允许列表（AAD 对象 ID） |
| `AllowedTeamIds` | `[]` | 群组策略的团队 ID 允许列表 |
| `AllowedConversationIds` | `[]` | 群组策略的对话 ID 允许列表 |

## 访问控制

### DM 策略

- **`pairing`**（默认）：未知发送者收到配对代码。消息被忽略，直到管理员批准。
- **`open`**：任何人都可以给机器人发 DM。
- **`closed`**：所有 DM 被静默丢弃。

### 群组策略

- **`allowlist`**（默认）：只有 `AllowedTeamIds` 或 `AllowedConversationIds` 中的团队/对话收到回复。
- **`open`**：机器人在安装的任何团队中回复（默认仍受提及限制）。
- **`disabled`**：无频道/群组回复。

### 提及行为

当 `RequireMention` 为 `true`（默认）时，机器人仅在频道和群聊中被显式 @提及时才回复。`<at>BotName</at>` 标签在处理前自动从消息文本中剥离。

在一对一 DM 中，永远不需要 @提及。

## 故障排除

### Webhook 返回 401 Unauthorized

当手动测试时（例如使用 curl）如果没有有效的 Azure JWT，这是预期的。Webhook 仅接受来自 Bot Framework 的认证请求。使用 **Azure Web Chat**（在 Azure Portal 中）独立于 Teams 进行测试。

### 机器人在 Teams 中不回复

1. 验证 Azure Portal 中消息端点设置正确
2. 验证应用已安装在特定团队中（RSC 权限按安装生效）
3. 完全退出并重新启动 Teams——它缓存非常激进
4. 如果本地测试没有正确的 JWT，检查 `ValidateToken` 为 `false`

### 应用上传时"出了点问题"

尝试改为通过 **Teams Admin Center** 上传。检查浏览器 DevTools 的实际错误。常见原因：
- `botId` 与你的 App ID 不匹配
- 图标文件缺失或尺寸不正确
- 清单 JSON 无效
