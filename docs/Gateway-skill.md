SharpClaw Gateway API — Agent Skill Reference

Base: https://your-domain.example.com (user-configured)
Auth: same JWT flow as internal API. POST /api/auth/login → Bearer token.
Gateway auto-attaches X-Api-Key when forwarding to internal API (localhost:48923).
All bodies JSON. Enums serialised as strings. Timestamps ISO 8601.

The gateway is a thin reverse proxy — it never interprets business logic.
It adds: endpoint toggles, rate limiting, IP banning, anti-spam, bot integrations.
Depends only on SharpClaw.Contracts (DTOs, enums).

────────────────────────────────────────
SECURITY
────────────────────────────────────────
Middleware pipeline (order matters):
  1. EndpointGateMiddleware — per-group enable/disable → 503
  2. IpBanMiddleware — banned IPs → 403
  3. AntiSpamMiddleware — body >64KB → 413, missing Content-Type on POST/PUT → 415
  4. RateLimiter — per-IP sliding/fixed windows → 429

IP banning: 10 violations in 5 minutes → 1-hour ban. In-memory, resets on restart.

Rate limit policies:
  global   60 req/min  sliding window (6 segments)   most endpoints
  auth      5 req/min  fixed window                   /api/auth/*
  chat     20 req/min  sliding window (4 segments)    chat send, stream, cost, thread chat

────────────────────────────────────────
ENDPOINT TOGGLES
────────────────────────────────────────
Configured in Gateway:Endpoints section of .env.
Master kill-switch: Enabled=false → 503 for all requests.
Per-group toggles: Auth, Agents, Channels, ChannelContexts, Chat, ChatStream, Threads, ThreadChat, Jobs, Models, Providers, Roles, Users, AudioDevices, Transcription, TranscriptionStreaming, Cost, Bots.

ResolveGroup priority (first match wins):
  /api/auth*                       → Auth
  */chat/stream*, */chat/sse*      → ChatStream
  */chat/cost*, */cost*            → Cost
  */threads/*/chat*                → ThreadChat
  */chat*                          → Chat
  */threads*                       → Threads
  */jobs*                          → Jobs
  /api/agents*                     → Agents
  /api/channels*                   → Channels
  /api/channelcontexts*, /api/channel-contexts*  → ChannelContexts
  /api/models*                     → Models
  /api/providers*                  → Providers
  /api/roles*                      → Roles
  /api/users*                      → Users
  /api/audio-devices*              → AudioDevices
  */ws*, */stream*                 → TranscriptionStreaming
  /api/transcription*              → Transcription
  /api/bots*                       → Bots

────────────────────────────────────────
AUTH
────────────────────────────────────────
POST /api/auth/register            { username, password }  → { id, username }
POST /api/auth/login               { username, password, rememberMe }  → { accessToken, accessTokenExpiresAt, refreshToken?, refreshTokenExpiresAt? }
POST /api/auth/refresh             { refreshToken }  → same shape as login
GET  /api/auth/me                  → { id, username, bio, roleId, roleName, isUserAdmin }
GET  /api/auth/me/role             → RolePermissionsResponse

────────────────────────────────────────
AGENTS
────────────────────────────────────────
GET  /api/agents                   → AgentResponse[]
GET  /api/agents/{id}              → AgentResponse

Read-only. Create/update/delete is internal-only.

────────────────────────────────────────
CHANNELS
────────────────────────────────────────
POST   /api/channels               { agentId?, title?, contextId?, permissionSetId?, allowedAgentIds?, disableChatHeader?, customChatHeader? }
GET    /api/channels?agentId={guid}
GET    /api/channels/{id}
PUT    /api/channels/{id}          { title?, contextId?, permissionSetId?, allowedAgentIds?, disableChatHeader?, customChatHeader? }
DELETE /api/channels/{id}

────────────────────────────────────────
CHANNEL CONTEXTS
────────────────────────────────────────
POST   /api/channelcontexts        { agentId, name?, permissionSetId?, disableChatHeader?, allowedAgentIds? }
GET    /api/channelcontexts?agentId={guid}
GET    /api/channelcontexts/{id}
PUT    /api/channelcontexts/{id}   { name?, permissionSetId?, disableChatHeader?, allowedAgentIds? }
DELETE /api/channelcontexts/{id}

────────────────────────────────────────
THREADS
────────────────────────────────────────
POST   /api/channels/{channelId}/threads           { name?, maxMessages?, maxCharacters? }
GET    /api/channels/{channelId}/threads
GET    /api/channels/{channelId}/threads/{threadId}
PUT    /api/channels/{channelId}/threads/{threadId} { name?, maxMessages?, maxCharacters? }
DELETE /api/channels/{channelId}/threads/{threadId}

Defaults: maxMessages=50, maxCharacters=100000.

────────────────────────────────────────
CHAT (per-channel)
────────────────────────────────────────
POST /api/channels/{channelId}/chat                { message, agentId?, clientType?, editorContext? }
GET  /api/channels/{channelId}/chat/history         → ChatMessageResponse[]

Without a thread: one-shot (no history sent to model).

────────────────────────────────────────
THREAD CHAT
────────────────────────────────────────
POST /api/channels/{channelId}/chat/threads/{threadId}           { message, agentId?, clientType?, editorContext? }
GET  /api/channels/{channelId}/chat/threads/{threadId}           → ChatMessageResponse[] (history)

With a thread: history included, trimmed by thread's maxMessages and maxCharacters.

────────────────────────────────────────
CHAT STREAMING (SSE)
────────────────────────────────────────
GET  /api/channels/{channelId}/chat/stream                                      → text/event-stream
GET  /api/channels/{channelId}/chat/threads/{threadId}/stream                   → text/event-stream
POST /api/channels/{channelId}/chat/stream/approve/{jobId}                      { approved }
POST /api/channels/{channelId}/chat/threads/{threadId}/stream/approve/{jobId}   { approved }

SSE event types: TextDelta, ToolCallStart, ToolCallResult, ApprovalRequired, ApprovalResult, Error, Done.
Registered via minimal API (MapChatStreamProxy), not MVC controllers.

────────────────────────────────────────
AGENT JOBS
────────────────────────────────────────
POST /api/channels/{channelId}/jobs                    (SubmitAgentJobRequest)
GET  /api/channels/{channelId}/jobs                    → AgentJobResponse[]
GET  /api/channels/{channelId}/jobs/summaries          → AgentJobSummaryResponse[] (lightweight)
GET  /api/channels/{channelId}/jobs/{jobId}            → AgentJobResponse
POST /api/channels/{channelId}/jobs/{jobId}/approve    { approverAgentId? }
POST /api/channels/{channelId}/jobs/{jobId}/stop       (graceful stop; also accepts Paused)
POST /api/channels/{channelId}/jobs/{jobId}/cancel     (force cancel; also accepts Paused)
PUT  /api/channels/{channelId}/jobs/{jobId}/pause      (stops capture/inference while paused)
PUT  /api/channels/{channelId}/jobs/{jobId}/resume     (restarts paused job)

SubmitAgentJobRequest: actionType (required), resourceId?, agentId?, safeShellType?, scriptJson?, transcriptionModelId?, language?, transcriptionMode?, windowSeconds?, stepSeconds?

AgentJobStatus: Queued, Executing, AwaitingApproval, Completed, Failed, Denied, Cancelled, Paused.

────────────────────────────────────────
MODELS
────────────────────────────────────────
GET /api/models?providerId={guid}   → ModelResponse[]
GET /api/models/{id}                → ModelResponse

Read-only. Create/update/delete is internal-only.

────────────────────────────────────────
PROVIDERS
────────────────────────────────────────
GET /api/providers                  → ProviderResponse[]
GET /api/providers/{id}             → ProviderResponse

Read-only. Create/update/delete/sync/set-key is internal-only.

────────────────────────────────────────
COST TRACKING
────────────────────────────────────────
Toggled by the Cost endpoint group (separate from Chat/Providers).

GET /api/channels/{channelId}/chat/cost                          → ChannelCostResponse
GET /api/channels/{channelId}/chat/threads/{threadId}/cost       → ThreadCostResponse
GET /api/providers/{id}/cost?days=30&startDate=...&endDate=...   → ProviderCostResponse
GET /api/providers/cost/total?days=30&startDate=...&endDate=...&all=true&simple=true
    → ProviderCostTotalResponse (default) or ProviderCostSimpleResponse (?simple=true)

────────────────────────────────────────
ROLES
────────────────────────────────────────
GET /api/roles                      → RoleResponse[]
GET /api/roles/{id}                 → RoleResponse
GET /api/roles/{id}/permissions     → RolePermissionsResponse

Read-only. Permission updates (PUT /roles/{id}/permissions) are internal-only.

────────────────────────────────────────
USERS
────────────────────────────────────────
GET /api/users                      → UserEntry[] (admin-only)

────────────────────────────────────────
AUDIO DEVICES
────────────────────────────────────────
GET /api/audio-devices              → AudioDeviceResponse[]
GET /api/audio-devices/{id}         → AudioDeviceResponse

Read-only.

────────────────────────────────────────
TRANSCRIPTION
────────────────────────────────────────
GET  /api/transcription/{jobId}              → AgentJobResponse
POST /api/transcription/{jobId}/stop
POST /api/transcription/{jobId}/cancel
GET  /api/transcription/{jobId}/segments?since={ISO8601}  → TranscriptionSegmentResponse[]

────────────────────────────────────────
TRANSCRIPTION STREAMING
────────────────────────────────────────
GET /api/jobs/{jobId}/ws       → WebSocket proxy (JSON text frames with segment objects)
GET /api/jobs/{jobId}/stream   → SSE proxy (data: frames with segment JSON)

Registered via minimal API (MapTranscriptionStreamingProxy).

────────────────────────────────────────
BOTS
────────────────────────────────────────
GET /api/bots/status  → { telegram: { enabled, configured }, discord: { enabled, configured } }

enabled reflects .env toggle. configured is true when a non-empty BotToken is present.

Telegram: BackgroundService, long-polling via Bot API, validates token with getMe on startup.
Discord: BackgroundService, WebSocket Gateway v10, validates with /users/@me, Identify with GUILDS|GUILD_MESSAGES|MESSAGE_CONTENT intents.
Both: message reception and logging implemented. Core relay NOT YET implemented — placeholder acknowledgement only.

────────────────────────────────────────
CONFIGURATION (.env)
────────────────────────────────────────
File: SharpClaw.Gateway/Environment/.env (JSON-with-comments, auto-created if missing).
Loaded via GatewayEnvironment.AddGatewayEnvironment() with PhysicalFileProvider + ExclusionFilters.None.

InternalApi:BaseUrl         http://127.0.0.1:48923
InternalApi:ApiKey          (auto-read from %LOCALAPPDATA%/SharpClaw/.api-key; explicit override)
Gateway:Endpoints:Enabled   true (master kill-switch)
Gateway:Endpoints:{Group}   true (per-group toggles, all default true)
Gateway:Bots:Telegram:Enabled   false
Gateway:Bots:Telegram:BotToken  (empty)
Gateway:Bots:Discord:Enabled    false
Gateway:Bots:Discord:BotToken   (empty)

────────────────────────────────────────
ERROR RESPONSES
────────────────────────────────────────
All errors: { "error": "description" }

200  Success with body
204  Success, no body (DELETE)
400  Bad request
401  Unauthorized
403  Forbidden (admin-only, IP ban)
404  Not found
413  Payload too large (>64KB)
415  Missing Content-Type on POST/PUT
429  Rate limited (Retry-After header)
502  Bad gateway (internal API unreachable)
503  Service unavailable (endpoint group disabled)

────────────────────────────────────────
SCOPE
────────────────────────────────────────
The gateway is a read-heavy public proxy. It intentionally does NOT proxy:
  Provider create/update/delete, Model create/update/delete, Agent create/update/delete,
  Role permission updates, Resource create/update/delete/sync, Local model management,
  Editor bridge WebSocket, Env file management, Task definitions & instances.
These are accessible only via the internal API on localhost.
