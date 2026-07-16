# SharpClaw Gateway API Reference

> **Default URL:** `https://your-domain.example.com` (user-configured)
>
> **Authentication:** The gateway forwards all requests to the internal
> SharpClaw Runtime Host. The caller authenticates using the same
> JWT-based auth flow — `POST /api/auth/login` returns a token that
> must be sent as a `Bearer` header (or however the internal API
> expects it). The gateway itself attaches the internal `X-Api-Key`
> automatically when forwarding.

All request/response bodies are JSON. Enum fields are serialised as
strings. Timestamps are ISO 8601 (`DateTimeOffset`).

---

## Purpose

The **SharpClaw Gateway** (`SharpClaw.Gateway`) is a standalone
ASP.NET Core reverse proxy that sits between public clients and the
internal SharpClaw Runtime Host. The internal API stays bound to a
local or private address and protects itself with a per-instance runtime
API key. The gateway resolves that key from explicit configuration, an
auth file path, or selected-backend discovery metadata, then attaches it
when forwarding requests.

The gateway is intentionally thin. Controllers forward requests through
`InternalApiClient`, mutations can be serialized through the request queue,
and the security middleware adds endpoint gates, rate limits, IP bans,
body-size checks, and content-type checks. Gateway-side module endpoint groups are supported through `Gateway:Modules`.

The current gateway implementation is instance-scoped. A gateway process
resolves its own instance root, keeps its own manifest, logs, and discovery
entry, and binds to one selected backend instance. When launched by the
Uno frontend, the frontend passes the selected backend URL, backend
instance metadata, and runtime auth values directly to the gateway.

The gateway depends only on `SharpClaw.Contracts` for DTOs and enums; it has
no access to Core `DbContext`, domain services, or business logic.

---

## Table of Contents

This reference starts with [Architecture](#architecture),
[Configuration](#configuration-env), [Security middleware](#security-middleware),
and [Endpoint toggles](#endpoint-toggles). It then documents
[Health probes](#health-probes), [Gateway status](#gateway-status),
[Auth](#auth), [Agents](#agents), [Channels](#channels),
[Channel contexts](#channel-contexts), [Threads](#threads),
[Chat](#chat-per-channel), [Thread chat](#thread-chat),
[Chat streaming](#chat-streaming-sse), [Agent jobs](#agent-jobs),
[Models](#models), [Providers](#providers), [Cost tracking](#cost-tracking),
[Roles](#roles), [Users](#users),
[Module-owned surfaces](#module-owned-surfaces),
[Error handling](#error-handling), [Response headers](#response-headers),
[Rate limiting details](#rate-limiting-details),
[Project structure](#project-structure), and [Scope](#scope--non-goals).

---

## Architecture

```
  Public Client ──► SharpClaw Gateway ──► Internal Runtime Host
                   (public-facing)        (localhost:48923)
```

Every controller is a thin proxy:

1. Accept the request from the public client.
2. Forward it to the internal API — **reads** (GET) go directly
   through `InternalApiClient`; **mutations** (POST/PUT/DELETE) are
   routed through `GatewayRequestDispatcher` → `RequestQueueService`
   for sequential processing when the queue is enabled.
3. Return the internal API's response (or a gateway error).

The gateway never interprets business logic — it is a pass-through
with security layers.

### Internal API Client

`InternalApiClient` is a typed `HttpClient` registered via DI.
It resolves internal API auth in this order:

1. explicit `InternalApi:ApiKey` / `InternalApi:GatewayToken`
2. explicit `InternalApi:ApiKeyFilePath` /
   `InternalApi:GatewayTokenFilePath`
3. the selected backend instance's runtime auth files from backend
   discovery metadata

The gateway no longer assumes one machine-global
`%LOCALAPPDATA%/SharpClaw/.api-key` or `.gateway-token` file. An
optional `InternalApi:GatewayToken` still allows the gateway to
authenticate with the core API without a user JWT.

The `HttpClient.Timeout` is set from `InternalApi:TimeoutSeconds`
(default **300 seconds / 5 minutes**). This needs to be generous
because agent tool-call chains — wait, screenshot, click, type,
inference — can take several minutes per turn.

Methods: `GetAsync<T>`, `PostAsync<TReq, TRes>`, `PutAsync<TReq, TRes>`,
`DeleteAsync`, `SendRawAsync`. All serialise with `camelCase` +
`JsonStringEnumConverter`.

### Request queue

`RequestQueueService` buffers mutation requests (POST/PUT/DELETE) and
forwards them to the core API sequentially (or with bounded
concurrency). Configured via `Gateway:RequestQueue` in `.env`.

`GatewayRequestDispatcher` is the scoped service that controllers use.
GET requests bypass the queue and go directly through
`InternalApiClient`. Mutations are enqueued and processed by the
`RequestQueueProcessor` hosted service.

When the queue is full, the gateway returns `503 Service Unavailable`
with a `Retry-After` header estimated from average processing time.

---

## Configuration (.env)

The gateway uses canonical dotenv files in its deployed assembly-local
`Environment` directory. `GatewayEnvironment.AddGatewayEnvironment()`
loads `.env` and the optional `.dev.env` overlay through Supprocom.Secrets;
the package owns parsing, protection, and recovery.

A default `.env` is auto-created on first run if missing.

### Full default .env

The committed gateway template now mirrors the runtime options class. This
is the shape generated for a missing gateway env file; development mode may
override endpoint toggles from `.dev.env`.

```dotenv
InternalApi__BaseUrl="http://127.0.0.1:48923"
InternalApi__TimeoutSeconds="300"
InternalApi__ApiKey=""
InternalApi__ApiKeyFilePath=""
InternalApi__GatewayToken=""
InternalApi__GatewayTokenFilePath=""
Gateway__RequestQueue__Enabled="true"
Gateway__RequestQueue__MaxConcurrency="1"
Gateway__RequestQueue__TimeoutSeconds="30"
Gateway__RequestQueue__MaxRetries="2"
Gateway__RequestQueue__RetryDelayMs="500"
Gateway__RequestQueue__MaxQueueSize="500"
Gateway__Endpoints__Enabled="true"
Gateway__Endpoints__Auth="false"
Gateway__Endpoints__Agents="false"
Gateway__Endpoints__Channels="false"
Gateway__Endpoints__ChannelContexts="false"
Gateway__Endpoints__Chat="false"
Gateway__Endpoints__ChatStream="false"
Gateway__Endpoints__Threads="false"
Gateway__Endpoints__ThreadChat="false"
Gateway__Endpoints__ThreadWatch="false"
Gateway__Endpoints__Jobs="false"
Gateway__Endpoints__Models="false"
Gateway__Endpoints__LocalModels="false"
Gateway__Endpoints__Providers="false"
Gateway__Endpoints__Roles="false"
Gateway__Endpoints__Users="false"
Gateway__Endpoints__Cost="false"
Gateway__Endpoints__Tasks="false"
Gateway__Endpoints__TaskStreaming="false"
Gateway__Endpoints__ToolAwarenessSets="false"
Gateway__Endpoints__Resources="false"
Gateway__Endpoints__Modules__HotReloadEnabled="false"
Gateway__Endpoints__Modules__DrainTimeoutSeconds="30"
```

### Settings reference

`InternalApi:BaseUrl` and `InternalApi:TimeoutSeconds` configure the core
API client. `InternalApi:ApiKey` and `InternalApi:GatewayToken` are direct
secret overrides. `InternalApi:ApiKeyFilePath` and
`InternalApi:GatewayTokenFilePath` point to runtime auth files. Empty
values are ignored so selected-backend discovery can resolve the current
`.api-key` and `.gateway-token` files.

`Gateway:RequestQueue` controls queued mutation forwarding. The queue is
enabled by default, runs one request at a time unless `MaxConcurrency` is
increased, retries transient failures, and rejects new mutations with 503
when `MaxQueueSize` is reached.

`Gateway:Endpoints:Enabled` is the master switch. The base template leaves
each built-in public group false, which means a new gateway will answer
health and gateway-status requests but will return 503 for proxied public
API groups until the operator opts in. Development overrides may enable
those groups for local testing. `Gateway:Modules` separately controls
gateway-side module extension hosts and per-module endpoint groups.

## Security middleware

The middleware pipeline runs in order:

1. **EndpointGateMiddleware** — checks the master switch and per-group
   toggles. Returns `503 Service Unavailable` if disabled.
2. **IpBanMiddleware** — rejects banned IPs with `403 Forbidden`.
3. **AntiSpamMiddleware** — rejects bodies > 64 KB (`413`), rejects
   POST/PUT without `Content-Type` (`415`). Records violations.
4. **RateLimiter** — per-IP rate limiting (see below).

### IP banning

`IpBanService` tracks per-IP violations in a sliding window.
After **10 violations within 5 minutes**, the IP is banned for
**1 hour**. State is in-memory and resets on restart.

Rate-limit rejections, oversized bodies, and missing content-types all
count as violations.

---

## Endpoint toggles

`GatewayEndpointOptions` exposes a property for each built-in endpoint
group, and `EndpointGateMiddleware.ResolveGroup()` maps request paths to
those names before a controller runs. Auth requests map to `Auth`; channel
and thread chat stream paths map to `ChatStream`, `ThreadChat`, or `Chat`
depending on the path; job, model, provider, role, user, cost, task, tool
awareness, resource, and local-model paths map to their matching groups.
`ThreadWatch` handles the thread watch SSE path. Static groups are false in
the base `.env.template`; module endpoint groups are enabled separately by
`Gateway:Modules:Modules:{moduleId}` and
`Gateway:Modules:Groups:{moduleId}/{groupId}`.

Unmatched paths pass through. The `/api/gateway/*` endpoints and the
`/healthz` and `/readyz` probes are intentionally not assigned to a public
endpoint group, so they remain available regardless of toggle state.

## Health probes

Health probes short-circuit before security middleware (no rate
limiting, no IP ban check, no endpoint gate). They are always
available.

### GET /healthz

Liveness probe. Returns immediately.

**200** → `{ status: "healthy" }`

---

### GET /readyz

Readiness probe. Checks queue status and core API reachability.

**200** → `{ status: "ready", checks: { queue: "ok", coreApi: "ok" } }`
**503** → `{ status: "not_ready", checks: { ... } }`

Possible `checks` values:
- `queue` — `"ok"` or `"disabled"`
- `coreApi` — `"ok"`, `"status:{code}"`, or `"unreachable"`

---

## Gateway status

**Route prefix:** `/api/gateway`
**Rate limit policy:** `global`

These endpoints bypass the endpoint gate (no toggle) and are always
accessible.

### GET /api/gateway/status

Returns operational status including request queue metrics.

**200:**

```json
{
  "queue": {
    "enabled": true,
    "pending": 0,
    "averageProcessingMs": 42.3,
    "processedLastHour": 157,
    "totalEnqueued": 1024,
    "totalProcessed": 1024
  }
}
```

---

### GET /api/gateway/queue/stream

SSE stream of queue metrics, pushed every 2 seconds.
Returns `204 No Content` when the queue is disabled.

**Response:** `text/event-stream` with `no-cache`, `keep-alive`.

SSE payload:

```json
{ "pending": 0, "avgMs": 42.3, "processedLastHour": 157, "totalProcessed": 1024 }
```

---

## Auth

**Route prefix:** `/api/auth`
**Rate limit policy:** `auth` (5 req/min fixed window)

### POST /api/auth/register

```json
{ "username": "string", "password": "string" }
```

**200** → `{ id, username }`

---

### POST /api/auth/login

```json
{ "username": "string", "password": "string", "rememberMe": true }
```

**200** → `{ accessToken, accessTokenExpiresAt, refreshToken?, refreshTokenExpiresAt? }`
**401** → `{ error: "Invalid credentials." }`

---

### POST /api/auth/refresh

```json
{ "refreshToken": "string" }
```

**200** → same shape as login
**401** → `{ error: "Invalid or expired refresh token." }`

---

### GET /api/auth/me

Returns the authenticated user's identity.

**200** → `{ id, username, bio, roleId, roleName, isUserAdmin }`
**401** → `{ error: "Not authenticated." }`

---

### GET /api/auth/me/role

Returns the authenticated user's role and permissions.

**200** → `RolePermissionsResponse`
**401** → `{ error: "Not authenticated." }`
**404** → `{ error: "No role assigned." }`

---

## Agents

**Route prefix:** `/api/agents`
**Rate limit policy:** `global` (60 req/min sliding window)

### POST /api/agents

Forwards `CreateAgentRequest` to the core API and returns the created
`AgentResponse`. Invalid requests are returned as `400`; connectivity failures
to the core API are returned as `502`.

---

### GET /api/agents

**200** → `AgentResponse[]`

---

### GET /api/agents/{id}

**200** → `AgentResponse`
**404** → `{ error: "Agent not found." }`

---

### GET /api/agents/{id}/cost

**200** → `AgentCostResponse`
**404** → `{ error: "Agent not found." }`

---

### PUT /api/agents/{id}

Forwards `UpdateAgentRequest` to the core API.

**200** → `AgentResponse`
**404** → `{ error: "Agent not found." }`

---

### DELETE /api/agents/{id}

**204** No Content
**404** Not Found

---

### PUT /api/agents/{id}/role

Assigns a role to an agent by forwarding `AssignAgentRoleRequest`.

**200** → `AgentResponse`
**403** → `{ error: "Insufficient permissions." }`
**404** → `{ error: "Agent not found." }`

---

### POST /api/agents/sync-with-models

Synchronizes agents with available models and returns the resulting agent list.

**200** → `AgentResponse[]`

---

## Channels

**Route prefix:** `/api/channels`
**Rate limit policy:** `global`

### POST /api/channels

```json
{ "agentId?": "guid", "title?": "string", "contextId?": "guid",
  "permissionSetId?": "guid", "allowedAgentIds?": ["guid"],
  "disableChatHeader?": true, "customChatHeader?": "string" }
```

**200** → `ChannelResponse`
**400** → `{ error: "Invalid channel request." }`

---

### GET /api/channels

Optional query parameter: `?agentId={guid}`

**200** → `ChannelResponse[]`

---

### GET /api/channels/{id}

**200** → `ChannelResponse`
**404** → `{ error: "Channel not found." }`

---

### PUT /api/channels/{id}

```json
{ "title?": "string", "contextId?": "guid",
  "permissionSetId?": "guid", "allowedAgentIds?": ["guid"],
  "disableChatHeader?": true, "customChatHeader?": "string" }
```

**200** → `ChannelResponse`
**404** → `{ error: "Channel not found." }`

---

### DELETE /api/channels/{id}

**204** No Content
**404** Not Found

---

## Channel contexts

**Route prefix:** `/api/channel-contexts`
**Rate limit policy:** `global`

### POST /api/channel-contexts

```json
{ "agentId": "guid", "name?": "string",
  "permissionSetId?": "guid", "disableChatHeader?": true,
  "allowedAgentIds?": ["guid"] }
```

**200** → `ContextResponse`
**400** → `{ error: "Invalid context request." }`

---

### GET /api/channel-contexts

Optional query parameter: `?agentId={guid}`

**200** → `ContextResponse[]`

---

### GET /api/channel-contexts/{id}

**200** → `ContextResponse`
**404** → `{ error: "Context not found." }`

---

### PUT /api/channel-contexts/{id}

```json
{ "name?": "string", "permissionSetId?": "guid",
  "disableChatHeader?": true, "allowedAgentIds?": ["guid"] }
```

**200** → `ContextResponse`
**404** → `{ error: "Context not found." }`

---

### DELETE /api/channel-contexts/{id}

**204** No Content
**404** Not Found

---

## Threads

**Route prefix:** `/api/channels/{channelId}/threads`
**Rate limit policy:** `global`

### POST /api/channels/{channelId}/threads

```json
{ "name?": "string", "maxMessages?": 50, "maxCharacters?": 100000 }
```

**200** → `ThreadResponse`
**400** → `{ error: "Invalid thread request." }`

---

### GET /api/channels/{channelId}/threads

**200** → `ThreadResponse[]`

---

### GET /api/channels/{channelId}/threads/{threadId}

**200** → `ThreadResponse`
**404** → `{ error: "Thread not found." }`

---

### PUT /api/channels/{channelId}/threads/{threadId}

```json
{ "name?": "string", "maxMessages?": 50, "maxCharacters?": 100000 }
```

**200** → `ThreadResponse`
**404** → `{ error: "Thread not found." }`

---

### DELETE /api/channels/{channelId}/threads/{threadId}

**204** No Content
**404** Not Found

---

## Chat (per-channel)

**Route prefix:** `/api/channels/{channelId}/chat`
**Rate limit policy:** `chat` (20 req/min sliding window)

### POST /api/channels/{channelId}/chat

Send a chat message (one-shot, no history).

```json
{ "message": "string", "agentId?": "guid",
  "clientType?": "API", "editorContext?": { ... } }
```

**200** → `ChatResponse`
**400** → `{ error: "Invalid chat request." }`
**404** → `{ error: "Channel not found." }`

---

### GET /api/channels/{channelId}/chat/history

**200** → `ChatMessageResponse[]`
**404** → `{ error: "Channel not found." }`

---

## Thread chat

**Route prefix:** `/api/channels/{channelId}/chat/threads/{threadId}`
**Rate limit policy:** `chat`

### POST /api/channels/{channelId}/chat/threads/{threadId}

Send a chat message within a thread (includes history up to thread limits).

```json
{ "message": "string", "agentId?": "guid",
  "clientType?": "API", "editorContext?": { ... } }
```

**200** → `ChatResponse`
**400** → `{ error: "Invalid chat request." }`
**404** → `{ error: "Channel or thread not found." }`

---

### GET /api/channels/{channelId}/chat/threads/{threadId}

Thread message history.

**200** → `ChatMessageResponse[]`
**404** → `{ error: "Channel or thread not found." }`

---

## Chat streaming (SSE)

These are registered via minimal API (`MapChatStreamProxy`) rather than
MVC controllers, because they proxy raw SSE/HTTP streams.

### GET /api/channels/{channelId}/chat/stream

Proxy for channel-level SSE streaming. Query string forwarded.

SSE event types: `TextDelta`, `ToolCallStart`, `ToolCallResult`,
`ApprovalRequired`, `ApprovalResult`, `Error`, `Done`.

**Response:** `text/event-stream` with `no-cache`, `keep-alive`.

---

### GET /api/channels/{channelId}/chat/threads/{threadId}/stream

Proxy for thread-level SSE streaming.

---

### POST /api/channels/{channelId}/chat/stream/approve/{jobId}

Approve or deny a pending job during a streaming session.

```json
{ "approved": true }
```

---

### POST /api/channels/{channelId}/chat/threads/{threadId}/stream/approve/{jobId}

Approve or deny a pending job during a thread streaming session.

```json
{ "approved": true }
```

---

## Agent jobs

**Route prefix:** `/api/channels/{channelId}/jobs`
**Rate limit policy:** `global`

### POST /api/channels/{channelId}/jobs

Submit a new agent job.

```json
{
  "actionType": "ExecuteAsSafeShell",
  "resourceId?": "guid",
  "agentId?": "guid",
  "safeShellType?": "string",
  "scriptJson?": "string"
}
```

**200** → `AgentJobResponse`
**400** → `{ error: "Invalid job request." }`

---

### GET /api/channels/{channelId}/jobs

**200** → `AgentJobResponse[]`

---

### GET /api/channels/{channelId}/jobs/summaries

Lightweight list (no `resultData`/`errorLog`/`logs`).

**200** → `AgentJobSummaryResponse[]`

---

### GET /api/channels/{channelId}/jobs/{jobId}

**200** → `AgentJobResponse`
**404** → `{ error: "Job not found." }`

---

### POST /api/channels/{channelId}/jobs/{jobId}/approve

```json
{ "approverAgentId?": "guid" }
```

**200** → `AgentJobResponse`
**404** → `{ error: "Job not found." }`

---

### POST /api/channels/{channelId}/jobs/{jobId}/stop

Graceful stop for a long-running job; also accepts Paused jobs.

**200** → `AgentJobResponse`
**404** → `{ error: "Job not found." }`

---

### POST /api/channels/{channelId}/jobs/{jobId}/cancel

Force cancel (also accepts Paused jobs).

**200** → `AgentJobResponse`
**404** → `{ error: "Job not found." }`

---

### PUT /api/channels/{channelId}/jobs/{jobId}/pause

Pause an executing job (stops capture/inference).

**200** → `AgentJobResponse`
**404** → `{ error: "Job not found." }`

---

### PUT /api/channels/{channelId}/jobs/{jobId}/resume

Resume a paused job.

**200** → `AgentJobResponse`
**404** → `{ error: "Job not found." }`

---

## Models

**Route prefix:** `/api/models`
**Rate limit policy:** `global`

### POST /api/models

Forwards `CreateModelRequest` to the core API.

**200** → `ModelResponse`
**400** → `{ error: "Invalid model request." }`

---

### GET /api/models

Optional query parameter: `?providerId={guid}`

**200** → `ModelResponse[]`

---

### GET /api/models/{id}

**200** → `ModelResponse`
**404** → `{ error: "Model not found." }`

---

### PUT /api/models/{id}

Forwards `UpdateModelRequest` to the core API.

**200** → `ModelResponse`
**404** → `{ error: "Model not found." }`

---

### DELETE /api/models/{id}

**204** No Content
**404** Not Found

---

## Providers

**Route prefix:** `/api/providers`
**Rate limit policy:** `global`

### POST /api/providers

Forwards `CreateProviderRequest` to the core API.

**200** → `ProviderResponse`
**400** → `{ error: "Invalid provider request." }`

---

### GET /api/providers

**200** → `ProviderResponse[]`

---

### GET /api/providers/types

Forwards the core API provider-type metadata. This is the endpoint
gateway clients should use to populate provider pickers because the list
comes from enabled provider modules, including third-party modules.

**200** → `ProviderTypeResponse[]`

---

### GET /api/providers/{id}

**200** → `ProviderResponse`
**404** → `{ error: "Provider not found." }`

---

### PUT /api/providers/{id}

Forwards `UpdateProviderRequest` to the core API.

**200** → `ProviderResponse`
**404** → `{ error: "Provider not found." }`

---

### DELETE /api/providers/{id}

**204** No Content
**404** Not Found

---

### POST /api/providers/{id}/sync-models

Synchronizes provider models through the core API.

**200** → `ProviderResponse[]`
**404** → `{ error: "Provider not found." }`

---

### POST /api/providers/{id}/set-key

Forwards `SetApiKeyRequest` and returns no content on success.

**204** No Content
**404** → `{ error: "Provider not found." }`

---

### POST /api/providers/{id}/auth/device-code

Starts provider device-code authentication when the provider supports it.

**200** → `DeviceCodeResponse`
**404** → `{ error: "Provider not found." }`

---

### POST /api/providers/{id}/auth/device-code/poll

Polls an in-progress provider device-code authentication request.

**200** → provider-specific poll result
**408** → `{ status: "expired" }`
**404** → `{ error: "Provider not found." }`

---

## Cost tracking

**Endpoint group:** `Cost` (toggled separately from `Chat`/`Providers`)
**Rate limit policy:** `chat` (channel/thread cost), `global` (provider cost)

### GET /api/channels/{channelId}/chat/cost

Channel-level token cost breakdown.

**200** → `ChannelCostResponse`
**404** → `{ error: "Channel not found." }`

---

### GET /api/channels/{channelId}/chat/threads/{threadId}/cost

Thread-level token cost breakdown.

**200** → `ThreadCostResponse`
**404** → `{ error: "Channel or thread not found." }`

---

### GET /api/providers/{id}/cost

Per-provider cost with daily buckets.

Optional query parameters: `?days=30`, `?startDate=...`, `?endDate=...`

**200** → `ProviderCostResponse`
**404** → `{ error: "Provider not found." }`

---

### GET /api/providers/cost/total

Aggregate cost across all providers.

Optional query parameters are `?days=30` for a lookback window,
`?startDate=...` and `?endDate=...` for an ISO 8601 date range,
`?all=true` to include providers with zero cost, and `?simple=true` to return
`ProviderCostSimpleResponse` instead of the full aggregate shape.

**200** → `ProviderCostTotalResponse` (default) or `ProviderCostSimpleResponse` (`?simple=true`)

---

## Roles

**Route prefix:** `/api/roles`
**Rate limit policy:** `global`

### POST /api/roles

Forwards `CreateRoleRequest` to the core API.

**201** → `RoleResponse`
**400** → `{ error: "Invalid role request." }`

---

### GET /api/roles

**200** → `RoleResponse[]`

---

### GET /api/roles/{id}

**200** → `RoleResponse`
**404** → `{ error: "Role not found." }`

---

### GET /api/roles/{id}/permissions

**200** → `RolePermissionsResponse`
**404** → `{ error: "Role not found." }`

---

### PUT /api/roles/{id}/name

Renames a role through `RenameRoleRequest`.

**200** → `RoleResponse`
**404** → `{ error: "Role not found." }`
**409** → `{ error: "Role name conflict." }`

---

### PUT /api/roles/{id}/permissions

Replaces role permissions through `SetRolePermissionsRequest`.

**200** → `RolePermissionsResponse`
**401** → `{ error: "Not authenticated." }`
**403** → `{ error: "Insufficient permissions." }`
**404** → `{ error: "Role not found." }`
**409** → `{ error: "Permission conflict." }`

---

### DELETE /api/roles/{id}

**204** No Content
**404** Not Found

---

## Users

**Route prefix:** `/api/users`
**Rate limit policy:** `global`

### GET /api/users

Admin-only list of all users.

**200** → `UserEntry[]`
**403** → `{ error: "Admin access required." }`

---

### PUT /api/users/{id}/role

Assigns a role to a user through `SetUserRoleRequest`.

**200** → `UserEntry`
**400** → `{ error: "Invalid role assignment." }`
**403** → `{ error: "Admin access required." }`
**404** → `{ error: "User not found." }`

---

## Module-Owned Surfaces

The current `SharpClaw.Gateway` project exposes the controllers listed in this
document. If a deployment adds a gateway module, enable both the module-level
flag and the specific `{moduleId}/{groupId}` flag before expecting routes from
that module to map.

## Error handling

Every controller follows the same error pattern:

| HTTP status | Meaning |
|-------------|---------|
| `200` | Success with body |
| `204` | Success, no body (DELETE) |
| `400` | Bad request (invalid input) |
| `401` | Unauthorized (auth endpoints) |
| `403` | Forbidden (admin-only endpoints, IP ban) |
| `404` | Resource not found |
| `413` | Payload too large (> 64 KB) |
| `415` | Missing Content-Type on POST/PUT |
| `429` | Rate limited (includes `Retry-After` header) |
| `500` | Internal server error (unhandled exception) |
| `502` | Bad gateway — internal API unreachable |
| `503` | Service unavailable — endpoint group disabled or queue full |

All error bodies follow the envelope shape:

```json
{ "error": "description", "code": "ERROR_CODE", "requestId": "hex32" }
```

`ErrorEnvelopeFilter` catches unhandled controller exceptions and wraps
them in this envelope. Middleware errors (`GatewayErrors.WriteAsync`)
use the same shape.

### Error codes

| Code | Meaning |
|------|---------|
| `IP_BANNED` | IP address is temporarily banned |
| `RATE_LIMITED` | Rate limit exceeded |
| `PAYLOAD_TOO_LARGE` | Request body > 64 KB |
| `UNSUPPORTED_MEDIA_TYPE` | Missing Content-Type on POST/PUT |
| `ENDPOINT_DISABLED` | Specific endpoint group is toggled off |
| `GATEWAY_DISABLED` | Master kill-switch is off |
| `BAD_GATEWAY` | Core API unreachable |
| `QUEUE_FULL` | Request queue is at capacity |
| `INTERNAL_ERROR` | Unhandled server exception |

---

## Response headers

Every response includes telemetry headers set by the gateway middleware
pipeline. These are useful for client-side diagnostics and rate-limit
awareness.

| Header | Present on | Description |
|--------|-----------|------------|
| `X-Request-Id` | All responses | Correlation ID (32-char hex). If the request was queued, this is the queue item's ID; otherwise a fresh GUID. |
| `X-RateLimit-Limit` | All responses | The applicable rate limit (req/min) for the matched path. |
| `Cache-Control` | All responses | `private, max-age=5` for GET; `no-store` for mutations. |
| `X-Queue-Pending` | When queue is enabled | Number of items currently waiting in the queue. |
| `X-Queue-Avg-Ms` | When queue has data | Rolling average processing time (ms) over the last hour. |
| `X-Queue-Position` | Queued mutations only | This request's position in the queue when it was enqueued. |
| `X-Queue-Processing-Ms` | Queued mutations only | Actual processing time (ms) for this request. |
| `Retry-After` | 503 (queue full) | Estimated wait in seconds based on queue depth and average processing time (minimum 5). |

---

## Rate limiting details

Three named policies per IP:

| Policy | Limit | Window | Type | Applied to |
|--------|-------|--------|------|------------|
| `global` | 60 req/min | 1 min (6 segments) | Sliding window | Most endpoints |
| `auth` | 5 req/min | 1 min | Fixed window | `/api/auth/*` |
| `chat` | 20 req/min | 1 min (4 segments) | Sliding window | Chat send, chat stream, chat cost, thread chat |

When a request is rate-limited, the violation is recorded against the
IP's ban counter. Exceeding **10 violations in 5 minutes** triggers a
**1-hour IP ban**.

---

## Project structure

The current gateway project is intentionally small. `Program.cs` wires
configuration, endpoint gating, rate limiting, anti-spam checks, the request
queue, and the thin proxy controllers. `Controllers` contains the public
surfaces that still ship with the gateway: auth, agents, channels, channel
contexts, chat, streamed chat, threads, thread chat, jobs, models, providers,
roles, users, and the gateway status endpoints. `Configuration` contains only
the gateway endpoint, request queue, and env loader options, while
`Infrastructure` contains the internal API client, queued request dispatcher,
queue metrics, request queue service, and shared error-envelope helpers.

`Security` contains the middleware for endpoint gates, IP bans, body and content-type checks, and rate-limit policies. `Modules` contains the gateway module loader and endpoint-group catalog, plus the hosting and routing helpers used when an external gateway module contributes routes.

### OpenAPI / Swagger

In development mode (`ASPNETCORE_ENVIRONMENT=Development`), the gateway
exposes an OpenAPI document at `/openapi/v1.json` and a Swagger UI at
`/swagger`.

---

## Scope & non-goals

The gateway is a public proxy in front of the core API, not a separate
business-logic host. The current controllers proxy both reads and mutations for
the surfaces listed above, and the request queue can serialize mutation
traffic before forwarding it to the core API. Endpoint group toggles are the
main control for deciding which public surfaces are exposed in a deployment.

The gateway still does not own every SharpClaw capability. Env file management,
the editor bridge WebSocket, scheduler/task-definition controllers,
tool-awareness-set controllers, resource lookup controllers, and module-owned
surfaces only exist here when a concrete gateway controller or gateway module
maps them. If a path is only represented by an endpoint toggle and no
controller or module route exists, enabling the toggle will not create the
route by itself.
