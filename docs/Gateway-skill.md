SharpClaw Gateway API — Agent Skill Reference

Base: https://your-domain.example.com (user-configured)
Auth: same JWT flow as internal API. POST /api/auth/login → Bearer token.
Gateway auto-attaches X-Api-Key when forwarding to internal API (localhost:48923).
All bodies JSON. Enums serialised as strings. Timestamps ISO 8601.

The gateway is a thin reverse proxy — it never interprets business logic.
It adds endpoint toggles, rate limiting, IP banning, anti-spam checks,
queued mutation forwarding, and optional module-owned endpoint hosting.
Depends only on SharpClaw.Contracts for shared DTOs and enums.

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
Configured in the Gateway:Endpoints section of .env. The master switch is
Enabled; when it is false, public proxy routes return 503. The committed
template keeps every built-in endpoint group disabled by default, while the
development template enables the built-in groups for local testing.

Built-in groups are Auth, Agents, Channels, ChannelContexts, Chat,
ChatStream, Threads, ThreadChat, ThreadWatch, Jobs, Models, LocalModels,
Providers, Roles, Users, Cost, Tasks, TaskStreaming, ToolAwarenessSets, and
Resources. Module-owned routes under /api/modules/* are resolved through the
gateway module endpoint catalog and use `Gateway:Modules:Modules:{moduleId}`
plus `Gateway:Modules:Groups:{moduleId}/{groupId}`.

ResolveGroup priority (first match wins):
  /api/auth*                       → Auth
  */chat/stream*, */chat/sse*      → ChatStream
  */chat/cost*, */cost*            → Cost
  */threads/*/watch*               → ThreadWatch
  */threads/*/chat*                → ThreadChat
  */chat*                          → Chat
  */threads*                       → Threads
  */jobs*                          → Jobs
  /api/agents*                     → Agents
  /api/channels*                   → Channels
  /api/channelcontexts*, /api/channel-contexts*  → ChannelContexts
  /api/models/local*               → LocalModels
  /api/models*                     → Models
  /api/providers*                  → Providers
  /api/roles*                      → Roles
  /api/users*                      → Users
  /api/tasks* with /stream         → TaskStreaming
  /api/tasks*                      → Tasks
  /api/toolawarenesssets*          → ToolAwarenessSets
  /api/tool-awareness-sets*        → ToolAwarenessSets
  /api/resources*                  → Resources
  /api/modules/{module}/{group}/*  → module-owned group

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
POST /api/agents                    { name, modelId, systemPrompt?, maxCompletionTokens?, customChatHeader?, toolAwarenessSetId? }
GET  /api/agents                   → AgentResponse[]
GET  /api/agents/{id}              → AgentResponse
GET  /api/agents/{id}/cost         → AgentCostResponse
PUT  /api/agents/{id}              { name?, modelId?, systemPrompt?, maxCompletionTokens?, customChatHeader?, toolAwarenessSetId? }
DELETE /api/agents/{id}
PUT  /api/agents/{id}/role         { roleId }
POST /api/agents/sync-with-models  → AgentResponse[]

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
POST   /api/channel-contexts        { agentId, name?, permissionSetId?, disableChatHeader?, allowedAgentIds? }
GET    /api/channel-contexts?agentId={guid}
GET    /api/channel-contexts/{id}
PUT    /api/channel-contexts/{id}   { name?, permissionSetId?, disableChatHeader?, allowedAgentIds? }
DELETE /api/channel-contexts/{id}

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

SubmitAgentJobRequest: actionType (required), resourceId?, agentId?, safeShellType?, scriptJson?

AgentJobStatus: Queued, Executing, AwaitingApproval, Completed, Failed, Denied, Cancelled, Paused.

────────────────────────────────────────
MODELS
────────────────────────────────────────
POST /api/models                    { name, providerId, capabilities? }
GET /api/models?providerId={guid}   → ModelResponse[]
GET /api/models/{id}                → ModelResponse
PUT /api/models/{id}                { name?, capabilities? }
DELETE /api/models/{id}

────────────────────────────────────────
PROVIDERS
────────────────────────────────────────
POST /api/providers                  { name, providerKey, apiEndpoint?, apiKey? }
GET /api/providers                  → ProviderResponse[]
GET /api/providers/types            → ProviderTypeResponse[]
GET /api/providers/{id}             → ProviderResponse
PUT /api/providers/{id}             { name?, apiEndpoint? }
DELETE /api/providers/{id}
POST /api/providers/{id}/sync-models
POST /api/providers/{id}/set-key     { apiKey }
POST /api/providers/{id}/auth/device-code
POST /api/providers/{id}/auth/device-code/poll

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
POST /api/roles                      { name }
GET /api/roles                      → RoleResponse[]
GET /api/roles/{id}                 → RoleResponse
GET /api/roles/{id}/permissions     → RolePermissionsResponse
PUT /api/roles/{id}/name             { name }
PUT /api/roles/{id}/permissions      (SetRolePermissionsRequest)
DELETE /api/roles/{id}

────────────────────────────────────────
USERS
────────────────────────────────────────
GET /api/users                      → UserEntry[] (admin-only)
PUT /api/users/{id}/role            { roleId }

────────────────────────────────────────
MODULE-OWNED SURFACES
────────────────────────────────────────
The current gateway project exposes the routes listed in this skill. Extra gateway routes must be supplied by module-owned gateway extensions under /api/modules/* and enabled through Gateway:Modules.

────────────────────────────────────────
CONFIGURATION (.env)
────────────────────────────────────────
File: the deployed Gateway assembly's `Environment/.env` in canonical dotenv format.
`GatewayEnvironment.AddGatewayEnvironment()` loads it through
Supprocom.Secrets and applies the optional `.dev.env` overlay. The package
owns first-run creation, protection, and recovery.

InternalApi settings point the gateway at the core API. BaseUrl defaults to
http://127.0.0.1:48923 and TimeoutSeconds defaults to 300. ApiKey,
ApiKeyFilePath, GatewayToken, and GatewayTokenFilePath are optional overrides;
when they are empty, selected-backend discovery supplies the current runtime
.api-key and .gateway-token files.

Gateway:RequestQueue controls queued mutation forwarding. Gateway:Endpoints
contains the master switch, the built-in endpoint group switches, and the
module group map. Gateway:Modules controls external gateway module hosts,
their group flags, hot reload, and drain timeout.

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
The gateway is a public proxy in front of the core API. The current controllers
proxy both reads and mutations for the surfaces listed above, and queued
mutation forwarding can serialize POST, PUT, and DELETE traffic before it
reaches the core API. Endpoint group toggles decide which public surfaces are
exposed in a deployment.

Env file management, the editor bridge WebSocket, scheduler/task-definition
controllers, tool-awareness-set controllers, resource lookup controllers, and
module-owned surfaces exist here only when a concrete gateway controller or
gateway module maps them. A toggle by itself does not create a route.
