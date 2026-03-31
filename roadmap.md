# OpenClaw.NET Roadmap

Prioritized feature adoptions from Hermes Agent analysis, plus internal improvements.

## Phase 1: High-Value Channel & Backend Expansion

### Discord Channel Adapter
- New `IChannelAdapter` implementation using Discord bot API
- DM and server channel support with allowlist semantics
- Slash command registration for `/new`, `/model`, `/compress`
- Voice channel awareness (defer to Phase 3 STT)
- Thread-based conversation continuity

### Slack Channel Adapter
- Bot and app-level token support
- Workspace allowlisting
- Thread-based conversation mapping to sessions
- Slash command registration
- Block Kit message formatting

### Daytona Execution Backend
- New `IExecutionBackend` + `IExecutionProcessBackend` implementation
- REST API integration for workspace creation, command execution, hibernation
- Workspace hibernation on idle with configurable timeout
- Resume from hibernated state on next tool invocation
- Config: `ExecutionBackendType.Daytona` with endpoint, API key, workspace template

### Modal Execution Backend
- Serverless function invocation via Modal API
- Per-invocation billing model (no idle cost)
- GPU-capable execution for compute-heavy tools
- Config: `ExecutionBackendType.Modal` with token, app name, environment

## Phase 2: Multimodal Input & Multi-Model Coordination

### Voice Memo Transcription (STT)
- Inbound audio detection in channel adapters (WhatsApp voice notes, Telegram voice, Discord audio)
- Transcription service using Gemini multimodal or Whisper API
- Automatic transcription injection before agent processing
- Config: `OpenClaw:Multimodal:Transcription` with provider, model, language hints
- Fallback: return "voice message received but transcription unavailable" when disabled

### Mixture of Agents
- Fan-out a prompt to N providers in parallel via `LlmProviderRegistry`
- Collect responses, pass to a synthesizer model for best-answer extraction
- Configurable provider list per mixture profile
- Expose as `mixture_of_agents` tool and as a runtime option for high-stakes turns
- Config: `OpenClaw:Delegation:Mixtures` with named profiles

### Checkpoint / Resume for Long Tasks
- Structured save points during multi-step agent execution
- Checkpoint store (session metadata extension) with tool state, intermediate results
- Resume from last checkpoint on session reload or after interruption
- Agent runtime integration: auto-checkpoint after each successful tool batch

## Phase 3: Analytics, Safety, and Training Export

### CLI Insights Command
- `openclaw insights [--days N]` command querying existing metrics endpoints
- Provider usage breakdown, token spend, session counts, tool frequency
- TUI panel equivalent in the Spectre.Console interface
- Render as table or chart (Spectre.Console bar charts)

### URL Safety Validation
- Pre-fetch URL validation in `web_fetch` and `browser` tools
- Block private/loopback IPs (SSRF prevention)
- Optional blocklist for known-bad domains
- Config: `OpenClaw:Tooling:UrlSafety` with enabled flag and custom blocklist path

### Trajectory Export for RL Training
- Export complete agent interactions (prompt, tools, results, response) as JSONL
- Compatible with standard fine-tuning formats (OpenAI, Anthropic)
- Session-level or date-range export via admin endpoint and CLI
- Optional compression and anonymization
- Endpoint: `POST /admin/export/trajectories`

### Signal Channel Adapter
- Signal CLI or signald bridge integration
- DM-only support (Signal groups are complex)
- Pairing via QR code or linking
- Privacy-focused: no message content logging option
