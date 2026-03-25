# Microsoft Teams Channel Setup

OpenClaw supports Microsoft Teams as a native channel via the Azure Bot Framework. The bot receives messages through an HTTPS webhook and replies via the Bot Connector REST API.

## Prerequisites

- An Azure account (free tier works)
- A publicly accessible HTTPS endpoint (Cloudflare Tunnel, ngrok, or Tailscale Funnel)
- About 1-2 hours for initial setup

## Step 1: Create an Azure Bot

1. Go to the [Azure Portal](https://portal.azure.com) and create an **Azure Bot** resource
2. Settings:
   - **Pricing tier**: Free (F0)
   - **Type of App**: Single Tenant
   - **Creation type**: Create new Microsoft App ID
3. Once created, collect three credentials:
   - **App ID** — from the Bot Configuration page
   - **Client Secret** — create one under Certificates & Secrets in the App Registration
   - **Tenant ID** — from the App Registration Overview page

## Step 2: Configure OpenClaw

Add to `appsettings.json`:

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

Set environment variables:

```bash
export TEAMS_APP_ID="<your-app-id>"
export TEAMS_APP_PASSWORD="<your-client-secret>"
export TEAMS_TENANT_ID="<your-tenant-id>"
```

## Step 3: Expose Your Webhook

Teams sends messages to your bot via HTTPS. You need a public URL pointing to your gateway.

**Cloudflare Tunnel** (recommended for persistence):
```bash
brew install cloudflared
cloudflared tunnel create your-bot-name
# Configure routing to localhost:18789 (default gateway port)
```

**ngrok** (quick dev testing):
```bash
ngrok http 18789
```

## Step 4: Set the Messaging Endpoint

In the Azure Portal → your Bot resource → **Configuration**:

Set **Messaging endpoint** to: `https://yourdomain.com/api/messages`

## Step 5: Enable the Teams Channel

Azure Portal → your Bot → **Channels** → click **Microsoft Teams** → Configure → Accept Terms → Save.

## Step 6: Create the Teams App Package

Create a `manifest.json`:

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
    "short": "AI assistant powered by OpenClaw",
    "full": "An AI assistant that lives in your Teams workspace."
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

Create two icon files:
- `outline.png` (32x32, transparent background)
- `color.png` (192x192)

Zip all three files into a `.zip` package.

## Step 7: Upload to Teams

In Teams → **Apps** → **Manage your apps** → **Upload a custom app** → select your ZIP.

If sideloading is restricted, use the **Teams Admin Center** instead.

**Important**: After uploading, install the app into each team where you want it active. RSC permissions only take effect per-installation.

## Configuration Reference

| Field | Default | Description |
|-------|---------|-------------|
| `Enabled` | `false` | Master toggle |
| `DmPolicy` | `"pairing"` | `"open"`, `"pairing"`, or `"closed"` for 1:1 DMs |
| `GroupPolicy` | `"allowlist"` | `"open"`, `"allowlist"`, or `"disabled"` for channels/groups |
| `AppId` / `AppIdRef` | `env:TEAMS_APP_ID` | Azure Bot App ID |
| `AppPassword` / `AppPasswordRef` | `env:TEAMS_APP_PASSWORD` | Azure Bot Client Secret |
| `TenantId` / `TenantIdRef` | `env:TEAMS_TENANT_ID` | Azure AD Tenant ID |
| `WebhookPath` | `"/api/messages"` | Inbound webhook route |
| `ValidateToken` | `true` | Validate JWT on inbound requests (disable for local dev) |
| `RequireMention` | `true` | Require @mention in team channels and group chats |
| `ReplyStyle` | `"thread"` | `"thread"` (reply in thread) or `"top-level"` (new message) |
| `TextChunkLimit` | `4000` | Max characters per outbound message before chunking |
| `ChunkMode` | `"length"` | `"length"` or `"newline"` |
| `AllowedTenantIds` | `[]` | Restrict to specific Azure AD tenants |
| `AllowedFromIds` | `[]` | Sender allowlist (AAD object IDs) |
| `AllowedTeamIds` | `[]` | Team ID allowlist for group policy |
| `AllowedConversationIds` | `[]` | Conversation ID allowlist for group policy |

## Access Control

### DM Policy

- **`pairing`** (default): Unknown senders receive a pairing code. Messages are ignored until an admin approves.
- **`open`**: Anyone can DM the bot.
- **`closed`**: All DMs are silently dropped.

### Group Policy

- **`allowlist`** (default): Only teams/conversations in `AllowedTeamIds` or `AllowedConversationIds` receive responses.
- **`open`**: Bot responds in any team where it's installed (still mention-gated by default).
- **`disabled`**: No channel/group responses.

### Mention Behavior

When `RequireMention` is `true` (default), the bot only responds in channels and group chats when explicitly @mentioned. The `<at>BotName</at>` tag is automatically stripped from the message text before processing.

In 1:1 DMs, @mention is never required.

## Troubleshooting

### 401 Unauthorized from webhook

This is expected when testing manually (e.g., with curl) without a valid Azure JWT. The webhook only accepts authenticated requests from the Bot Framework. Use **Azure Web Chat** (in the Azure Portal) to test independently of Teams.

### Bot doesn't respond in Teams

1. Verify the messaging endpoint is set correctly in Azure Portal
2. Verify the app is installed in the specific team (RSC permissions are per-installation)
3. Fully quit and relaunch Teams — it caches aggressively
4. Check `ValidateToken` is `false` if testing locally without proper JWT

### "Something went wrong" on app upload

Try uploading through the **Teams Admin Center** instead. Check browser DevTools for the actual error. Common causes:
- `botId` doesn't match your App ID
- Missing or incorrectly sized icon files
- Invalid manifest JSON

### Proactive messaging not working

The bot can only send proactive messages after a user has interacted with it (the conversation reference is stored on first inbound message). Ensure the user has sent at least one message to the bot.
