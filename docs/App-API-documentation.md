# SharpClaw Application API Reference

> **Base URL:** `http://127.0.0.1:48923`
>
> **Authentication:** Every request must include an `X-Api-Key` header.
> The key is auto-generated per session and written to
> `%LOCALAPPDATA%/SharpClaw/.api-key`.

All request/response bodies are JSON. Enum fields are serialized as strings.
Timestamps are ISO 8601 (`DateTimeOffset`).

---

## Table of Contents

- [Health checks](#health-checks)
- [Enums](#enums)
- [Auth](#auth)
- [Users](#users)
- [Providers](#providers)
- [Models](#models)
- [Agents](#agents)
- [Channel contexts](#channel-contexts)
- [Channels](#channels)
- [Threads](#threads)
- [Chat (per-channel)](#chat-per-channel)
- [Chat streaming (SSE)](#chat-streaming-sse)
- [Agent Jobs](#agent-jobs)
- [Inline tools](#inline-tools)
- [Transcription streaming](#transcription-streaming)
- [Resources](#resources)
- [Roles](#roles)
- [Default resources](#default-resources)
- [Local models](#local-models)
- [Editor bridge](#editor-bridge)
- [Task definitions & instances](#task-definitions--instances)
- [Token cost tracking](#token-cost-tracking)
- [Provider cost](#provider-cost)
- [Permission Resolution](#permission-resolution)

---

## Health checks

### GET /echo

Liveness check — **no auth required** (no `X-Api-Key` header needed).

**Response `200`:**

```json
{ "status": "ok" }
```

---

### GET /ping

Authentication check — requires a valid `X-Api-Key` header.

**Response `200`:**

```json
{ "status": "authenticated" }
```

---

## Enums

### ProviderType

```
OpenAI, Anthropic, OpenRouter, GoogleVertexAI, GoogleGemini,
ZAI, VercelAIGateway, XAI, Groq, Cerebras, Mistral, GitHubCopilot,
Custom, Local
```

`Local` is used for in-process LLamaSharp / Whisper.net models.

### AgentActionType

| Category | Values |
|----------|--------|
| Global flags | `CreateSubAgent`, `CreateContainer`, `RegisterInfoStore`, `AccessLocalhostInBrowser`, `AccessLocalhostCli`, `ClickDesktop`, `TypeOnDesktop` |
| Per-resource | `UnsafeExecuteAsDangerousShell`, `ExecuteAsSafeShell`, `AccessLocalInfoStore`, `AccessExternalInfoStore`, `AccessWebsite`, `QuerySearchEngine`, `AccessContainer`, `ManageAgent`, `EditTask`, `AccessSkill`, `CaptureDisplay` |
| Transcription | `TranscribeFromAudioDevice`, `TranscribeFromAudioStream`, `TranscribeFromAudioFile` |
| Editor | `EditorReadFile`, `EditorGetOpenFiles`, `EditorGetSelection`, `EditorGetDiagnostics`, `EditorApplyEdit`, `EditorCreateFile`, `EditorDeleteFile`, `EditorShowDiff`, `EditorRunBuild`, `EditorRunTerminal` |

### PermissionClearance

| Value | Int | Description |
|-------|-----|-------------|
| `Unset` | 0 | Falls back to the group-level default |
| `ApprovedBySameLevelUser` | 1 | Requires approval from a same-level user |
| `ApprovedByWhitelistedUser` | 2 | Requires approval from a whitelisted user |
| `ApprovedByPermittedAgent` | 3 | Requires approval from an agent with the same permission |
| `ApprovedByWhitelistedAgent` | 4 | Requires approval from a whitelisted agent |
| `Independent` | 5 | Agent can act without any external approval |

### AgentJobStatus

| Value | Int | Description |
|-------|-----|-------------|
| `Queued` | 0 | Created, permission check in progress |
| `Executing` | 1 | Permission granted, action running |
| `AwaitingApproval` | 2 | Requires approval before execution |
| `Completed` | 3 | Finished successfully |
| `Failed` | 4 | Action threw an error |
| `Denied` | 5 | Agent lacks the required permission |
| `Cancelled` | 6 | Cancelled by a user or agent |
| `Paused` | 7 | Temporarily paused; can be resumed |

### ModelCapability (flags)

```
None = 0, Chat = 1, Transcription = 2, ImageGeneration = 4,
Embedding = 8, TextToSpeech = 16, Vision = 32
```

Values can be combined (comma-separated). Default is `Chat`.
`Vision` enables image/screenshot input for models that support it
(e.g. gpt-4o, claude-3+, gemini-1.5+).

### DangerousShellType

```
Bash, PowerShell, CommandPrompt, Git
```

These spawn a real shell interpreter with unrestricted execution.

### SafeShellType

```
Mk8Shell
```

Sandboxed DSL — never invokes a real shell interpreter.

### ChatClientType

```
CLI, API, Telegram, Discord, WhatsApp, VisualStudio, VisualStudioCode,
UnoWindows, UnoAndroid, UnoMacOS, UnoLinux, UnoBrowser, Other
```

Identifies the client interface that originated a chat message. Included
in the chat header so the agent knows the communication channel.

### EditorType

```
VisualStudio2026, VisualStudioCode, Other
```

### LocalModelStatus

```
Pending, Downloading, Ready, Failed
```

### ChatStreamEventType

| Value | Description |
|-------|-------------|
| `TextDelta` | Partial text token from the model |
| `ToolCallStart` | A tool call was detected and a job submitted |
| `ToolCallResult` | A tool call job completed (or failed/was denied) |
| `ApprovalRequired` | A job requires user approval before execution |
| `ApprovalResult` | Approval decision has been applied |
| `Error` | An error occurred during the stream |
| `Done` | Stream complete; contains the final persisted response |

### TranscriptionMode

| Value | Int | Description |
|-------|-----|-------------|
| `SlidingWindow` | 0 | Two-pass sliding window. Segments are emitted provisionally as soon as they pass quality filters, then finalized (or retracted) once the commit delay confirms them. Consumers see text within one inference tick (~3 s) and receive an update when the segment is confirmed. **Default.** |
| `Simple` | 1 | Sequential non-overlapping chunks. Each chunk transcribed independently, segments emitted immediately. Lower latency, fewer API calls, no cross-window dedup. |
| `StrictSlidingWindow` | 2 | Single-pass sliding window. Segments only emitted after the full commit delay, deduplication, and hallucination filtering. Higher accuracy but ~5–8 s perceived latency. |

### TaskInstanceStatus

| Value | Int | Description |
|-------|-----|-------------|
| `Queued` | 0 | Instance created, awaiting execution start |
| `Running` | 1 | Task entry point is actively running |
| `Paused` | 2 | Execution temporarily suspended; can be resumed |
| `Completed` | 3 | Entry point ran to completion successfully |
| `Failed` | 4 | Entry point threw an unhandled exception |
| `Cancelled` | 5 | Instance was cancelled by a user or agent |

### TaskOutputEventType

| Value | Description |
|-------|-------------|
| `Output` | Task-emitted output (from `Emit(...)`) |
| `Log` | Log message appended during execution |
| `StatusChange` | Task status changed (started, completed, failed, etc.) |
| `Done` | Terminal event — no more events will follow |

---


## Auth

SharpClaw uses a two-layer authentication scheme:

1. **API Key** — Every request (except `GET /echo`) must include an
   `X-Api-Key` header. The key is auto-generated per session and written
   to `%LOCALAPPDATA%/SharpClaw/.api-key`. This is for local machine
   trust, not user identity.
2. **JWT Bearer Token** — After the API key check, a standard
   `Authorization: Bearer <token>` header identifies the user. Endpoints
   that are not exempt (see below) return `401` if the token is missing,
   expired, or invalid.

**Exempt paths** (no JWT required): `/echo`, `/ping`, `/auth/login`,
`/auth/register`, `/auth/refresh`, `/editor/ws*`.

### Token lifetimes (defaults)

| Token | Default Lifetime | Configurable via |
|-------|-----------------|------------------|
| Access token (JWT) | 30 minutes | `Jwt:AccessTokenLifetime` |
| Refresh token | 30 days | `Jwt:RefreshTokenLifetime` |

Access tokens are short-lived and stateless (validated by signature +
expiry). Refresh tokens are stored server-side and support revocation
and rotation.

### Complete token lifecycle

The diagram below shows the full access / refresh token lifecycle that
third-party clients should implement.

```
 ┌──────────────┐                              ┌──────────────┐
 │  3rd-party   │                              │  SharpClaw   │
 │   client     │                              │    API       │
 └──────┬───────┘                              └──────┬───────┘
        │                                             │
        │  1. POST /auth/register                     │
        │   { username, password }                    │
        ├────────────────────────────────────────────►│
        │◄─────────── 200 { id, username } ───────────┤
        │                                             │
        │  2. POST /auth/login                        │
        │   { username, password, rememberMe: true }  │
        ├────────────────────────────────────────────►│
        │◄── 200 { accessToken,                  ─────┤
        │         accessTokenExpiresAt,                │
        │         refreshToken,                        │
        │         refreshTokenExpiresAt }              │
        │                                             │
        │  3. Use access token on all requests        │
        │   Authorization: Bearer <accessToken>       │
        │   X-Api-Key: <key>                          │
        ├────────────────────────────────────────────►│
        │◄─────────── 200 (normal response) ──────────┤
        │                                             │
        │  ··· (time passes, token nears expiry) ···  │
        │                                             │
        │  4. Access token expired → 401              │
        │   GET /some-endpoint                        │
        ├────────────────────────────────────────────►│
        │◄── 401 { error: "access_token_expired" } ──┤
        │    WWW-Authenticate: Bearer                 │
        │      error="invalid_token"                  │
        │                                             │
        │  5. POST /auth/refresh                      │
        │   { refreshToken: "<saved-token>" }         │
        ├────────────────────────────────────────────►│
        │◄── 200 { accessToken (new),            ─────┤
        │         accessTokenExpiresAt,                │
        │         refreshToken (rotated),              │
        │         refreshTokenExpiresAt }              │
        │                                             │
        │  6. Retry original request with new token   │
        ├────────────────────────────────────────────►│
        │◄─────────── 200 (normal response) ──────────┤
        │                                             │
```

**Key implementation notes for third-party clients:**

- Always store both the access token **and** the refresh token.
- Check `accessTokenExpiresAt` proactively to refresh before expiry, or
  react to the `401` / `access_token_expired` error code.
- After a successful refresh, the **old refresh token is revoked** and a
  new one is returned (rotation). Always replace your stored refresh
  token with the new one.
- If the refresh token itself is expired or revoked, `/auth/refresh`
  returns `401` — the user must log in again.
- `rememberMe: false` on login omits the refresh token entirely. The
  access token is the only credential and cannot be renewed after expiry.

### Detecting access token expiry (machine-readable)

When an access token expires (naturally or via server-side invalidation),
the API returns a specific response that third-party clients can detect
programmatically:

**Status:** `401 Unauthorized`

**Headers:**

```
WWW-Authenticate: Bearer error="invalid_token", error_description="The access token has expired"
```

**Body (`application/json`):**

```json
{
  "error": "access_token_expired",
  "message": "The access token has expired. Use your refresh token to obtain a new one via POST /auth/refresh."
}
```

**How to distinguish 401 causes:**

| Cause | Body | Content-Type |
|-------|------|-------------|
| Expired access token | `{ "error": "access_token_expired", ... }` | `application/json` |
| Missing/invalid API key | `"Invalid or missing API key."` | `text/plain` |
| Missing/malformed JWT | `"Authentication required."` | `text/plain` |

The `AccessTokenExpiredException` class
(`SharpClaw.Contracts.Exceptions.AccessTokenExpiredException`) exposes a
constant `ErrorCode = "access_token_expired"` that .NET clients can
reference directly instead of hard-coding the string.

### Server-side token invalidation

Administrators can force-invalidate tokens without waiting for natural
expiry:

- **`POST /auth/invalidate-access-tokens`** — Sets an invalidation
  timestamp on the user record. Any access token issued *before* this
  timestamp is rejected (returns the `access_token_expired` error).
  Refresh tokens remain valid — users can still refresh.
- **`POST /auth/invalidate-refresh-tokens`** — Revokes all refresh
  tokens for the specified users. Existing access tokens remain valid
  until their natural expiry.

To fully lock out a user immediately, call both endpoints.

### Example: third-party client refresh flow (pseudocode)

```python
response = http.get("/agents", headers={
    "Authorization": f"Bearer {access_token}",
    "X-Api-Key": api_key,
})

if response.status == 401:
    body = response.json()
    if body.get("error") == "access_token_expired" and refresh_token:
        refresh_resp = http.post("/auth/refresh", json={
            "refreshToken": refresh_token
        }, headers={"X-Api-Key": api_key})

        if refresh_resp.status == 200:
            tokens = refresh_resp.json()
            access_token  = tokens["accessToken"]
            refresh_token = tokens["refreshToken"]   # rotated!
            # Retry the original request
            response = http.get("/agents", headers={
                "Authorization": f"Bearer {access_token}",
                "X-Api-Key": api_key,
            })
        else:
            # Refresh token expired/revoked → force re-login
            redirect_to_login()
    else:
        # Not an expiry issue → missing or invalid token
        redirect_to_login()
```

### Example: C# / .NET client with automatic refresh

```csharp
public async Task<HttpResponseMessage> SendWithAutoRefreshAsync(
    HttpClient http, HttpRequestMessage request,
    TokenStore tokens)
{
    request.Headers.Authorization =
        new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
    request.Headers.Add("X-Api-Key", tokens.ApiKey);

    var response = await http.SendAsync(request);

    if (response.StatusCode == HttpStatusCode.Unauthorized)
    {
        var body = await response.Content.ReadAsStringAsync();
        if (body.Contains(AccessTokenExpiredException.ErrorCode)
            && tokens.RefreshToken is not null)
        {
            var refreshResp = await http.PostAsJsonAsync("/auth/refresh",
                new { refreshToken = tokens.RefreshToken });

            if (refreshResp.IsSuccessStatusCode)
            {
                var login = await refreshResp.Content
                    .ReadFromJsonAsync<LoginResponse>();
                tokens.AccessToken  = login!.AccessToken;
                tokens.RefreshToken = login.RefreshToken;

                // Clone and retry
                var retry = await CloneRequestAsync(request);
                retry.Headers.Authorization =
                    new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
                response = await http.SendAsync(retry);
            }
        }
    }

    return response;
}
```

---

### POST /auth/register

Register a new user. **No JWT required** (but API key is required).

**Request:**

```json
{
  "username": "string",
  "password": "string"
}
```

**Response `200`:**

```json
{
  "id": "guid",
  "username": "string"
}
```

---

### POST /auth/login

Authenticate and receive tokens. **No JWT required.**

**Request:**

```json
{
  "username": "string",
  "password": "string",
  "rememberMe": false
}
```

| Field | Description |
|-------|-------------|
| `rememberMe` | When `true`, the response includes a refresh token. When `false` (default), only the short-lived access token is returned. |

**Response `200`:**

```json
{
  "accessToken": "eyJhbG...",
  "accessTokenExpiresAt": "2025-01-15T12:30:00+00:00",
  "refreshToken": "base64-string | null",
  "refreshTokenExpiresAt": "2025-02-14T12:00:00+00:00 | null"
}
```

**Response `401`:** Invalid credentials.

---

### POST /auth/refresh

Exchange a valid refresh token for a new access + refresh token pair.
**No JWT required.** The old refresh token is **revoked** upon
successful use (one-time use / rotation).

**Request:**

```json
{
  "refreshToken": "string"
}
```

**Response `200`:** Same shape as login response (both tokens rotated).

```json
{
  "accessToken": "eyJhbG... (new)",
  "accessTokenExpiresAt": "2025-01-15T13:00:00+00:00",
  "refreshToken": "base64-string (new — store this!)",
  "refreshTokenExpiresAt": "2025-02-14T12:30:00+00:00"
}
```

**Response `401`:** Refresh token is invalid, expired, or already
revoked. The user must log in again.

---

### POST /auth/invalidate-access-tokens

Force-expire all access tokens issued before *now* for the given users.
Existing JWTs become invalid even if they haven't reached their natural
expiry. Refresh tokens are **not** affected — users can still refresh.

**Request:**

```json
{
  "userIds": ["guid"]
}
```

**Response `204`:** No content.

---

### POST /auth/invalidate-refresh-tokens

Revoke all refresh tokens for the given users. Existing access tokens
remain valid until their natural expiry.

**Request:**

```json
{
  "userIds": ["guid"]
}
```

**Response `204`:** No content.

---

### GET /auth/me

Get the authenticated user's profile.

**Response `200`:**

```json
{
  "id": "guid",
  "username": "string",
  "bio": "string | null",
  "roleId": "guid | null",
  "roleName": "string | null",
  "isUserAdmin": false
}
```

**Response `401`:** Not authenticated.

---

### PUT /auth/me/role

Self-assign a role. The caller must have permission to assign the
requested role.

**Request:**

```json
{
  "roleId": "guid"
}
```

**Response `200`:** Updated user profile.
**Response `403`:** Caller lacks permission.

---

## Users

Admin-only endpoints for managing registered users.

### GET /users

List all registered users. Requires the caller to be a user admin.

**Response `200`:** `UserEntry[]`

```json
[
  {
    "id": "guid",
    "username": "string",
    "bio": "string | null",
    "roleId": "guid | null",
    "roleName": "string | null",
    "isUserAdmin": false
  }
]
```

**Response `403`:** Caller is not a user admin.

---

### PUT /users/{id}/role

Assign a role to a user. Pass `Guid.Empty` as `roleId` to remove the
role. Requires the caller to be a user admin.

**Request:**

```json
{
  "roleId": "guid"
}
```

**Response `200`:** Updated `UserEntry`.
**Response `403`:** Caller is not a user admin.
**Response `404`:** User not found.

---

## Providers

### POST /providers

Create a new provider.

**Request:**

```json
{
  "name": "string",
  "providerType": "OpenAI",
  "apiEndpoint": "string | null",
  "apiKey": "string | null"
}
```

`apiEndpoint` is required for `Custom` providers, ignored otherwise.

**Response `200`:** `ProviderResponse`

---

### GET /providers

List all providers.

**Response `200`:** `ProviderResponse[]`

---

### GET /providers/{id}

**Response `200`:** `ProviderResponse`
**Response `404`:** Not found.

---

### PUT /providers/{id}

**Request:**

```json
{
  "name": "string | null",
  "apiEndpoint": "string | null"
}
```

**Response `200`:** `ProviderResponse`
**Response `404`:** Not found.

---

### DELETE /providers/{id}

**Response `204`:** Deleted.
**Response `404`:** Not found.

---

### POST /providers/{id}/set-key

**Request:**

```json
{
  "apiKey": "string"
}
```

**Response `204`:** Key stored (encrypted at rest).

---

### POST /providers/{id}/sync-models

Fetches the model catalogue from the provider's API and creates local
`Model` records for each one.  **This is the recommended way to add
models** — it guarantees the model names match the exact identifiers
the provider expects (e.g. `gpt-4o`, `claude-sonnet-4-20250514`).

After syncing, capabilities are automatically refreshed by name
heuristics.

**CLI:**

```
provider sync-models <providerId>
```

**Response `200`:** Synced model list.

---

### POST /providers/{id}/auth/device-code

Start an OAuth device code flow for providers that require it (e.g. GitHub Copilot).

**Response `200`:**

```json
{
  "userCode": "string",
  "verificationUri": "string",
  "expiresInSeconds": 0
}
```

---

### ProviderResponse

```json
{
  "id": "guid",
  "name": "string",
  "providerType": "OpenAI",
  "apiEndpoint": "string | null",
  "hasApiKey": true
}
```

---

## Models

> **Recommended:** Use `POST /providers/{id}/sync-models` (CLI: `provider
> sync-models <id>`) to auto-import models from the provider.  Manual
> creation is only needed for models the provider's list endpoint does
> not return.

### POST /models

**Request:**

```json
{
  "name": "string",
  "providerId": "guid",
  "capabilities": "Chat"
}
```

> ⚠️ **`name` must be the exact model identifier recognised by the
> provider's API** (e.g. `gpt-4o`, `claude-sonnet-4-20250514`,
> `gemini-2.5-flash`).  This value is sent directly in API requests;
> an incorrect name will cause a **404** error when chatting.
> Run `provider sync-models <id>` to see valid identifiers.

`capabilities` is a flags enum (comma-separated): `Chat`, `Transcription`,
`ImageGeneration`, `Embedding`, `TextToSpeech`. Defaults to `Chat`.

**Response `200`:** `ModelResponse`

---

### GET /models

**Response `200`:** `ModelResponse[]`

---

### GET /models/{id}

**Response `200`:** `ModelResponse`
**Response `404`:** Not found.

---

### PUT /models/{id}

**Request:**

```json
{
  "name": "string | null",
  "capabilities": "Chat,Transcription | null"
}
```

**Response `200`:** `ModelResponse`
**Response `404`:** Not found.

---

### DELETE /models/{id}

**Response `204`:** Deleted.
**Response `404`:** Not found.

---

### ModelResponse

```json
{
  "id": "guid",
  "name": "string",
  "providerId": "guid",
  "providerName": "string",
  "capabilities": "Chat"
}
```

---

## Agents

### POST /agents

**Request:**

```json
{
  "name": "string",
  "modelId": "guid",
  "systemPrompt": "string | null",
  "maxCompletionTokens": "integer | null"
}
```

`maxCompletionTokens` caps the number of tokens the model may generate per
response. Sent as `max_tokens`, `max_completion_tokens`, or
`max_output_tokens` depending on the provider and API version. `null`
(default) means no limit — the provider default applies.

**Response `200`:** `AgentResponse`

---

### GET /agents

**Response `200`:** `AgentResponse[]`

---

### GET /agents/{id}

**Response `200`:** `AgentResponse`
**Response `404`:** Not found.

---

### PUT /agents/{id}

**Request:**

```json
{
  "name": "string | null",
  "modelId": "guid | null",
  "systemPrompt": "string | null",
  "maxCompletionTokens": "integer | null"
}
```

**Response `200`:** `AgentResponse`
**Response `404`:** Not found.

---

### DELETE /agents/{id}

**Response `204`:** Deleted.
**Response `404`:** Not found.

---

### PUT /agents/{id}/role

Assign a role to an agent.

**Request:**

```json
{
  "roleId": "guid"
}
```

**Response `200`:** `AgentResponse`
**Response `403`:** Caller lacks permission.
**Response `404`:** Not found.

---

### AgentResponse

```json
{
  "id": "guid",
  "name": "string",
  "systemPrompt": "string | null",
  "modelId": "guid",
  "modelName": "string",
  "providerName": "string",
  "roleId": "guid | null",
  "roleName": "string | null",
  "maxCompletionTokens": "integer | null"
}
```

### AgentSummary

Lightweight agent object embedded in channel and context responses.
Omits `systemPrompt` to keep payloads compact.

```json
{
  "id": "guid",
  "name": "string",
  "modelId": "guid",
  "modelName": "string",
  "providerName": "string",
  "roleId": "guid | null",
  "roleName": "string | null",
  "maxCompletionTokens": "integer | null"
}
```

---

## Channel contexts

The API exposes a first-class resource called `channel-contexts` (route
group: `/channel-contexts`). These are the same "context" objects used
by the permission system to provide group-level permission grants for
channels and conversations.

### POST /channel-contexts

Create a new channel context.

Request body (example):

```json
{
  "agentId": "guid",
  "name": "string | null",
  "permissionSetId": "guid | null"
}
```

`permissionSetId` links the context to an existing permission set. When
channels inside this context do not have their own permission set, the
context's permission set is used as the default.

Response `200`: `ContextResponse`

### GET /channel-contexts?agentId={guid}

List channel contexts. Optional `agentId` filter.

Response `200`: `ContextResponse[]`

### GET /channel-contexts/{id}

Response `200`: `ContextResponse` or `404` when not found.

### PUT /channel-contexts/{id}

Update a context (e.g. rename or change permission set).

Request body:

```json
{
  "name": "string | null",
  "permissionSetId": "guid | null"
}
```

Response `200`: `ContextResponse` or `404` when not found.

### DELETE /channel-contexts/{id}

Deletes the context. Channels inside it are detached instead of deleted.

Response `204` or `404` when not found.

### Allowed agents (context)

#### GET /channel-contexts/{id}/agents

List the context's allowed agents.

**Response `200`:** `ContextAllowedAgentsResponse`

#### POST /channel-contexts/{id}/agents

Add an agent to the context's allowed set.

**Request:**

```json
{ "agentId": "guid" }
```

**Response `200`:** `ContextAllowedAgentsResponse`

#### DELETE /channel-contexts/{id}/agents/{agentId}

Remove an agent from the context's allowed set.

**Response `200`:** `ContextAllowedAgentsResponse`
**Response `404`:** Context not found.

### Default resources (context, per-key)

#### PUT /channel-contexts/{id}/defaults/{key}

Set a single default resource by key.

**Request:**

```json
{ "resourceId": "guid" }
```

**Response `200`:** `DefaultResourcesResponse`
**Response `400`:** Invalid key.

#### DELETE /channel-contexts/{id}/defaults/{key}

Clear a single default resource by key.

**Response `200`:** `DefaultResourcesResponse`
**Response `400`:** Invalid key.

Valid keys: `dangshell`, `safeshell`, `container`, `website`, `search`,
`localinfo`, `externalinfo`, `audiodevice`, `displaydevice`, `agent`,
`task`, `skill`, `transcriptionmodel`, `editor`.

### ContextResponse

```json
{
  "id": "guid",
  "name": "string",
  "agent": { /* AgentSummary */ },
  "permissionSetId": "guid | null",
  "disableChatHeader": false,
  "allowedAgents": [ { /* AgentSummary */ } ],
  "createdAt": "datetime",
  "updatedAt": "datetime"
}
```

`agent` and each entry in `allowedAgents` are full
[`AgentSummary`](#agentsummary) objects (id, name, modelId, modelName,
providerName, roleId, roleName, maxCompletionTokens).

### ContextAllowedAgentsResponse

```json
{
  "contextId": "guid",
  "defaultAgent": { /* AgentSummary */ },
  "allowedAgents": [ { /* AgentSummary */ } ]
}
```

---

## Channels

Channels are the primary REST surface for sending messages and
containing chat history. The route group is `/channels`.

### POST /channels

Create a new channel.

Request (example):

```json
{
  "agentId": "guid",
  "title": "string | null",
  "contextId": "guid | null",
  "permissionSetId": "guid | null",
  "allowedAgentIds": ["guid", ...] | null
}
```

`allowedAgentIds` specifies additional agents (beyond the default
`agentId`) allowed to operate on this channel. The model used for
completions is always the resolved agent's model.

Response `200`: `ChannelResponse`

### GET /channels?agentId={guid}

List channels. Optional `agentId` filter.

Response `200`: `ChannelResponse[]`

### GET /channels/{id}

Response `200`: `ChannelResponse` or `404` when not found.

### PUT /channels/{id}

Update channel properties.

Request (example):

```json
{
  "title": "string | null",
  "contextId": "guid | null",
  "permissionSetId": "guid | null",
  "allowedAgentIds": ["guid", ...] | null
}
```

When `allowedAgentIds` is provided it replaces the current set.
`permissionSetId` set to `Guid.Empty` removes the override; `null` leaves
it unchanged.

Response `200`: `ChannelResponse` or `404` when not found.

### DELETE /channels/{id}

Response `204` or `404` when not found.

### Default agent

#### PUT /channels/{id}/agent

Set the default agent for a channel.

**Request:**

```json
{ "agentId": "guid" }
```

**Response `200`:** `ChannelResponse`
**Response `404`:** Channel not found.

### Allowed agents (channel)

#### GET /channels/{id}/agents

List the channel's allowed agents (effective: channel's own, falling
back to context's).

**Response `200`:** `ChannelAllowedAgentsResponse`

#### POST /channels/{id}/agents

Add an agent to the channel's allowed set.

**Request:**

```json
{ "agentId": "guid" }
```

**Response `200`:** `ChannelAllowedAgentsResponse`

#### DELETE /channels/{id}/agents/{agentId}

Remove an agent from the channel's allowed set.

**Response `200`:** `ChannelAllowedAgentsResponse`
**Response `404`:** Channel not found.

### Default resources (channel, per-key)

#### PUT /channels/{id}/defaults/{key}

Set a single default resource by key.

**Request:**

```json
{ "resourceId": "guid" }
```

**Response `200`:** `DefaultResourcesResponse`
**Response `400`:** Invalid key.

#### DELETE /channels/{id}/defaults/{key}

Clear a single default resource by key.

**Response `200`:** `DefaultResourcesResponse`
**Response `400`:** Invalid key.

Valid keys: `dangshell`, `safeshell`, `container`, `website`, `search`,
`localinfo`, `externalinfo`, `audiodevice`, `displaydevice`, `agent`,
`task`, `skill`, `transcriptionmodel`, `editor`.

### ChannelResponse

```json
{
  "id": "guid",
  "title": "string",
  "agent": { /* AgentSummary | null */ },
  "contextId": "guid | null",
  "contextName": "string | null",
  "permissionSetId": "guid | null",
  "effectivePermissionSetId": "guid | null",
  "allowedAgents": [ { /* AgentSummary */ } ],
  "disableChatHeader": false,
  "createdAt": "datetime",
  "updatedAt": "datetime"
}
```

`agent` and each entry in `allowedAgents` are full
[`AgentSummary`](#agentsummary) objects (id, name, modelId, modelName,
providerName, roleId, roleName, maxCompletionTokens).

### ChannelAllowedAgentsResponse

```json
{
  "channelId": "guid",
  "defaultAgent": { /* AgentSummary | null */ },
  "allowedAgents": [ { /* AgentSummary */ } ]
}
```

---

## Threads

Threads are lightweight conversation threads within a channel. Messages
sent with a thread ID include the thread's history; messages without a
thread are one-shot (no history sent to the model).

Each thread has optional per-thread history limits:

- **`maxMessages`** — caps the number of recent messages sent as context.
  Default: `50`. `null` means use system default.
- **`maxCharacters`** — caps the total character count of the history
  payload. Default: `100000`. `null` means use system default.

When both are set, messages must fit within **both** limits — the oldest
messages are trimmed first. Setting either to `0` in an update resets it
to `null` (system default).

Endpoints live under `/channels/{channelId}/threads`.

### POST /channels/{channelId}/threads

Create a new thread.

**Request:**

```json
{
  "name": "string | null",
  "maxMessages": "int | null",
  "maxCharacters": "int | null"
}
```

All fields are optional. Omitted limits use system defaults (50 messages,
100 000 characters).

**Response `200`:** `ThreadResponse`

---

### GET /channels/{channelId}/threads

List all threads in a channel.

**Response `200`:** `ThreadResponse[]`

---

### GET /channels/{channelId}/threads/{threadId}

**Response `200`:** `ThreadResponse`
**Response `404`:** Not found.

---

### PUT /channels/{channelId}/threads/{threadId}

**Request:**

```json
{
  "name": "string | null",
  "maxMessages": "int | null",
  "maxCharacters": "int | null"
}
```

All fields are optional; only provided fields are updated. Set
`maxMessages` or `maxCharacters` to `0` to reset to `null` (system
default).

**Response `200`:** `ThreadResponse`
**Response `404`:** Not found.

---

### DELETE /channels/{channelId}/threads/{threadId}

Deletes the thread and all its messages.

**Response `204`:** Deleted.
**Response `404`:** Not found.

---

### ThreadResponse

```json
{
  "id": "guid",
  "name": "string | null",
  "channelId": "guid",
  "maxMessages": "int | null",
  "maxCharacters": "int | null",
  "createdAt": "datetime",
  "updatedAt": "datetime"
}
```

`maxMessages` and `maxCharacters` are `null` when using the system
defaults (50 and 100 000 respectively).

---

### Thread chat

Chat endpoints that operate within a thread context:

#### POST /channels/{id}/chat/threads/{threadId}

Send a message in a thread. History is included automatically,
respecting the thread's `maxMessages` and `maxCharacters` limits
(defaults: 50 messages / 100 000 characters). Body is the same as
`POST /channels/{id}/chat`.

**Response `200`:** `ChatResponse`

---

#### GET /channels/{id}/chat/threads/{threadId}/history

Retrieve message history for a specific thread.

**Response `200`:** Array of message objects.

---

#### GET /channels/{id}/chat/threads/{threadId}/stream

SSE-based streaming chat within a thread. Same event format as
`POST /channels/{id}/chat/stream` but includes thread history context.

**Response:** `text/event-stream`

---

## Chat (per-channel)

All chat operations are scoped to a channel id. Endpoints live under
`/channels/{id}/chat`.

### POST /channels/{id}/chat

Send a message and receive the assistant's reply. Without a thread,
no history is sent to the model (one-shot). Body:

```json
{
  "message": "string",
  "agentId": "guid | null",
  "clientType": "CLI | API | Telegram | Discord | WhatsApp | VisualStudio | VisualStudioCode | UnoWindows | UnoAndroid | UnoMacOS | UnoLinux | UnoBrowser | Other",
  "editorContext": {
    "editorType": "VisualStudio2026 | VisualStudioCode | Other",
    "editorVersion": "string | null",
    "workspacePath": "string | null",
    "activeFilePath": "string | null",
    "activeFileLanguage": "string | null",
    "selectionStartLine": "int | null",
    "selectionEndLine": "int | null",
    "selectedText": "string | null"
  }
}
```

- `agentId` optionally overrides the channel's default agent. The agent
  must be the channel default or in its `allowedAgentIds`.
- `clientType` identifies the calling interface. Defaults to `API`.
  Included in the chat header so the agent knows the communication
  channel.
- `editorContext` is optional. When provided by an IDE extension, it is
  included in the chat header so the agent is aware of the user's
  current editor state (open file, selection, workspace).

Response `200`:

```json
{
  "userMessage": { "role": "user", "content": "string", "timestamp": "datetime" },
  "assistantMessage": { "role": "assistant", "content": "string", "timestamp": "datetime" },
  "jobResults": [ /* AgentJobResponse[], if any */ ],
  "channelCost": { /* ChannelCostResponse — see Token cost tracking */ },
  "threadCost": null
}
```

Every chat response piggybacks the current channel (and thread, when
applicable) token usage so callers do not need a separate round-trip to
the `/cost` endpoints. See [Token cost tracking](#token-cost-tracking)
for the shape of these objects.

When the assistant submits agent jobs during the turn the same
permission-resolution rules apply (see [Permission Resolution](#permission-resolution)).

### GET /channels/{id}/chat

Retrieve chat history for a channel (most recent messages, chronological order).

Response `200`: an array of message objects.

---

## Chat streaming (SSE)

The API exposes SSE-based streaming chat endpoints which pause for
inline approvals.

### POST /channels/{id}/chat/stream

Streams `ChatStreamEvent` items as server-sent events (`text/event-stream`).
Request body is the same as `POST /channels/{id}/chat`.

Each event has an `event:` line matching the `ChatStreamEventType` and a
`data:` line with the JSON payload:

```
event: TextDelta
data: {"type":"TextDelta","delta":"Hello"}

event: ToolCallStart
data: {"type":"ToolCallStart","job":{...AgentJobResponse...}}

event: ToolCallResult
data: {"type":"ToolCallResult","result":{...AgentJobResponse...}}

event: ApprovalRequired
data: {"type":"ApprovalRequired","pendingJob":{...AgentJobResponse...}}

event: ApprovalResult
data: {"type":"ApprovalResult","approvalOutcome":{...AgentJobResponse...}}

event: Error
data: {"type":"Error","error":"message"}

event: Done
data: {"type":"Done","finalResponse":{...ChatResponse...}}
```

When a job requires approval the stream emits an `ApprovalRequired`
event and waits for the client to POST to the companion approve
endpoint.

### POST /channels/{id}/chat/stream/approve/{jobId}

Companion endpoint used by the client to resolve a pending approval
emitted by a running SSE stream. Body:

```json
{ "approved": true }
```

Response `200` on success, or `404` when no pending approval exists.

---

### Thread streaming

Thread-scoped streaming mirrors the channel-level endpoints but
includes conversation history:

#### POST /channels/{id}/chat/threads/{threadId}/stream

Same SSE event format as `POST /channels/{id}/chat/stream` but scoped
to a thread. Request body is the same as `POST /channels/{id}/chat`.
History is included automatically, respecting the thread's
`maxMessages` and `maxCharacters` limits.

#### POST /channels/{id}/chat/threads/{threadId}/stream/approve/{jobId}

Same approval companion endpoint, scoped to a thread stream.

---

## Agent Jobs

Jobs represent permission-gated agent actions. When a job is submitted,
the permission system evaluates it immediately:

- **Approved** → executes inline, returns `Completed` or `Failed`.
- **Pending** → checks conversation/context for a user-granted
  permission. If found → executes. Otherwise → `AwaitingApproval`.
- **Denied** → returns `Denied`.

### POST /channels/{channelId}/jobs

Submit a new job on a channel. The agent is inferred from the channel
unless overridden via `agentId`.

**Request:**

```json
{
  "actionType": "ExecuteAsSafeShell",
  "resourceId": "guid | null",
  "agentId": "guid | null",
  "callerAgentId": "guid | null",
  "dangerousShellType": "Bash | PowerShell | CommandPrompt | Git | null",
  "safeShellType": "Mk8Shell | null",
  "scriptJson": "string | null",
  "workingDirectory": "string | null",
  "transcriptionModelId": "guid | null",
  "language": "string | null",
  "transcriptionMode": "SlidingWindow | Simple | StrictSlidingWindow | null",
  "windowSeconds": "int | null",
  "stepSeconds": "int | null"
}
```

`agentId` optionally overrides the channel's default agent. The agent
must be the channel default or in its `allowedAgentIds`.

`resourceId` is required for per-resource action types. When omitted,
defaults are resolved from the channel → context → agent role permission
sets. Global action types ignore it.

#### Shell fields

For shell jobs:

- **`dangerousShellType`** — required for `UnsafeExecuteAsDangerousShell`.
  Values: `Bash`, `PowerShell`, `CommandPrompt`, `Git`.
- **`safeShellType`** — required for `ExecuteAsSafeShell`. Value: `Mk8Shell`.
- **`scriptJson`** — serialised `Mk8ShellScript` JSON for safe shell;
  raw command text for dangerous shell.
- **`workingDirectory`** — absolute path where the dangerous shell
  process should be spawned. Overrides the system user's default
  working directory. Not sandboxed — dangerous by design.

#### Transcription fields

For transcription jobs (`TranscribeFromAudioDevice`,
`TranscribeFromAudioStream`, `TranscribeFromAudioFile`):

- **`transcriptionModelId`** — overrides the agent's model with a
  specific transcription model. When omitted the default transcription
  model is resolved from the channel → context default resource set.
- **`language`** — BCP-47 language hint (e.g. `"en"`, `"de"`, `"ja"`)
  forwarded to the STT provider. When omitted, the model auto-detects
  the spoken language. **Supplying the correct language code improves
  accuracy and reduces latency** — especially for short audio chunks
  where auto-detection is unreliable.  When set, the orchestrator
  enforces the language via prompt seeding: the Whisper prompt is
  initialised with a natural phrase from an embedded
  `transcription-language-seeds.json` resource covering all 99
  Whisper-supported languages. If the API's response-level language
  tag doesn't match, the chunk is retried up to 4 times with
  escalating reinforcement (single seed → triple seed → instruction
  preamble + seed → max saturation block). If all retries still
  return the wrong language the result is accepted anyway — no audio
  is ever silently dropped.
- **`transcriptionMode`** — pipeline mode. `SlidingWindow` (default):
  two-pass — segments are emitted provisionally within one inference
  tick, then finalized or retracted once the commit delay confirms
  them.  `Simple`: sequential non-overlapping chunks, segments emitted
  immediately.  `StrictSlidingWindow`: single-pass — segments only
  emitted after the full commit delay + dedup pipeline confirms them
  (~5–8 s latency).
- **`windowSeconds`** — seconds of audio sent to Whisper per inference
  tick. Clamped to [5, 15]. Default 10. Larger windows give more
  context but cost more per API call.
- **`stepSeconds`** — seconds between inference ticks (SlidingWindow
  mode only). Clamped to [1, window]. Default 2. Ignored in Simple
  mode where step equals window.

> **CLI equivalent:**
> `job submit <channelId> TranscribeFromAudioDevice <audioDeviceId> --model <id> --lang en --mode simple --window 12 --step 4`
>
> Mode shortcuts: `sliding` (default, two-pass), `simple`, `strict` (single-pass).

Audio is automatically normalised to mono 16 kHz 16-bit PCM before
being sent to the transcription model (Whisper-optimal format).

#### Dedup pipeline constants

| Constant | Value | Description |
|----------|-------|-------------|
| `WindowSeconds` | 10 | Default audio window per inference tick |
| `InferenceIntervalSeconds` | 2 | Default step between ticks |
| `BufferCapacitySeconds` | 15 | Ring buffer size / max window clamp |
| `CommitDelaySeconds` | 2.0 | Delay before provisional → finalized |
| `MaxPromptChars` | 500 | Rolling prompt buffer size for Whisper |
| `SampleRate` | 16 000 | Audio sample rate (mono PCM) |

**Response `200`:** `AgentJobResponse`

---

### GET /channels/{channelId}/jobs

List all jobs for a channel.

**Response `200`:** `AgentJobResponse[]`

---

### GET /channels/{channelId}/jobs/summaries

List lightweight summaries for all jobs in a channel. Returns only the
fields needed for list views / dropdowns — no `resultData`, `errorLog`,
`logs`, or `segments`. Use this endpoint when you only need to enumerate
jobs without their heavy payloads.

**Response `200`:** `AgentJobSummaryResponse[]`

```json
[
  {
    "id": "guid",
    "channelId": "guid",
    "agentId": "guid",
    "actionType": "CaptureDisplay",
    "resourceId": "guid | null",
    "status": "Completed",
    "createdAt": "datetime",
    "startedAt": "datetime | null",
    "completedAt": "datetime | null"
  }
]
```

---

### GET /channels/{channelId}/jobs/{jobId}

**Response `200`:** `AgentJobResponse`
**Response `404`:** Not found.

---

### POST /channels/{channelId}/jobs/{jobId}/approve

Approve a job that is `AwaitingApproval`.

**Request:**

```json
{
  "approverAgentId": "guid | null"
}
```

The approver's identity is re-evaluated against the clearance
requirement.

**Response `200`:** `AgentJobResponse`

---

### POST /channels/{channelId}/jobs/{jobId}/stop

Stop a long-running transcription job (completes it normally).
Also accepts `Paused` transcription jobs.

**Response `200`:** `AgentJobResponse`
**Response `404`:** Not found.

---

### POST /channels/{channelId}/jobs/{jobId}/cancel

Cancel a job that has not yet completed. Also accepts `Paused` jobs.

**Response `200`:** `AgentJobResponse`
**Response `404`:** Not found.

---

### PUT /channels/{channelId}/jobs/{jobId}/pause

Pause a long-running job. For transcription jobs this stops the audio
capture and inference loop so no further API calls (and therefore no
token costs) are incurred while paused. The job can be resumed later.

Only jobs with status `Executing` can be paused.

**Response `200`:** `AgentJobResponse`
**Response `404`:** Not found.

---

### PUT /channels/{channelId}/jobs/{jobId}/resume

Resume a previously paused job. For transcription jobs this restarts
the audio capture and inference loop using the original job parameters.

Only jobs with status `Paused` can be resumed.

**Response `200`:** `AgentJobResponse`
**Response `404`:** Not found.

---

### POST /channels/{channelId}/jobs/{jobId}/segments

Push a transcription segment into an executing transcription job.

**Request:**

```json
{
  "text": "string",
  "startTime": 0.0,
  "endTime": 1.5,
  "confidence": 0.95
}
```

**Response `200`:** `TranscriptionSegmentResponse`
**Response `404`:** Job not found or not executing.

---

### GET /channels/{channelId}/jobs/{jobId}/segments?since={datetime}

Retrieve transcription segments added after the given timestamp.

**Response `200`:** `TranscriptionSegmentResponse[]`

---

### AgentJobResponse

```json
{
  "id": "guid",
  "channelId": "guid",
  "agentId": "guid",
  "actionType": "ExecuteAsSafeShell",
  "resourceId": "guid | null",
  "status": "Completed",
  "effectiveClearance": "ApprovedBySameLevelUser",
  "resultData": "string | null",
  "errorLog": "string | null",
  "logs": [
    {
      "message": "string",
      "level": "Info",
      "timestamp": "datetime"
    }
  ],
  "createdAt": "datetime",
  "startedAt": "datetime | null",
  "completedAt": "datetime | null",
  "dangerousShellType": "Bash | null",
  "safeShellType": "Mk8Shell | null",
  "scriptJson": "string | null",
  "workingDirectory": "string | null",
  "transcriptionModelId": "guid | null",
  "language": "string | null",
  "transcriptionMode": "SlidingWindow | Simple | StrictSlidingWindow | null",
  "windowSeconds": "int | null",
  "stepSeconds": "int | null",
  "segments": [
    {
      "id": "guid",
      "text": "string",
      "startTime": 0.0,
      "endTime": 1.5,
      "confidence": 0.95,
      "timestamp": "datetime",
      "isProvisional": false
    }
  ],
  "channelCost": {
    "channelId": "guid",
    "totalPromptTokens": 0,
    "totalCompletionTokens": 0,
    "totalTokens": 0,
    "agentBreakdown": []
  }
}
```

`segments` is only populated for transcription action types; `null`
otherwise.

`channelCost` is piggybacked on all detail / mutation responses
(Submit, GetById, Approve, Stop, Cancel, Pause, Resume) so callers
receive up-to-date token usage without a separate round-trip.
List and summary endpoints omit it.

#### Two-pass segment lifecycle (SlidingWindow mode)

In the default `SlidingWindow` mode, segments go through a two-pass
lifecycle:

1. **Provisional** — emitted as soon as the segment passes the dedup
   pipeline (within one inference tick, ~2 s). `isProvisional: true`.
   The text may shift slightly in later inference passes.

2. **Finalized** — once the commit delay (2 s) confirms the segment, a
   second event is pushed with the same `id`, updated `text` /
   `confidence`, and `isProvisional: false`. Consumers should replace
   the provisional version in-place.

3. **Retracted** — if a provisional segment is not confirmed within
   twice the commit delay (likely a hallucination), it is deleted and a
   tombstone event is pushed with the same `id`, empty `text`, and
   `isProvisional: false`. Consumers should remove it.

In `StrictSlidingWindow` mode all segments are final on first emission
(`isProvisional` is always `false`). In `Simple` mode the same applies.

#### Deduplication pipeline (non-timestamped API responses)

When the STT API returns the full window as a single text blob without
per-word timestamps (e.g. `gpt-4o-transcribe`), the orchestrator uses a
multi-layer dedup pipeline to extract genuinely new content:

1. **Text diff (`RemoveOverlap`)** — compares the current API response
   against `previousWindowText` (the last response that produced new
   content):
   - *Containment check* — if the current text is a word-level subset
     of the previous text, it is already-seen content and is skipped.
     Uses strict matching for short sequences (< 10 words) and 10%
     fuzzy tolerance (floor) for longer ones to catch minor Whisper
     hallucinations like "a" → "the".
   - *Suffix-prefix overlap* — finds the longest suffix of the previous
     text matching a prefix of the current text, and returns only the
     tail that is genuinely new.

2. **Context tracking** — `previousWindowText` is updated carefully:
   - When new text **is** found: always updated to the current response.
   - When new text is **not** found: only upgraded to a **longer**
     response (never downgraded to a shorter subset). This prevents
     context loss that would let already-emitted content slip through
     as "new" in later ticks.

3. **Sentence splitting** — when multiple sentences accumulate as "new"
   in a single tick, they are split at sentence boundaries
   (`[.!?]` + space + uppercase letter). Each sentence becomes its own
   segment with timestamps distributed proportionally by character
   count across the available time span. This prevents multi-sentence
   bursts from being emitted as a single segment.

4. **Fragment merge** — very short residuals (≤ 2 words starting with a
   lowercase letter) from API truncation at window boundaries are merged
   into the most recent provisional segment rather than emitted
   standalone.

5. **Emitted-text guard** — a `HashSet` tracks all emitted segment texts
   (normalized: trimmed, trailing period stripped, case-insensitive).
   Any segment whose text was already emitted is skipped at emission
   time. This catches Whisper hallucination replay where the API
   regurgitates the entire transcript history as if it were new speech.

6. **Timestamp guard** — segments whose `absEnd ≤ lastSeenEnd` are
   skipped to prevent backward-looking duplicates.

---

## Transcription streaming

Two streaming transports are provided for live transcription segments.

WebSocket:

    /jobs/{jobId}/ws

- Connect with WebSocket; the server will send JSON text frames with
  transcription segment objects.

SSE:

    /jobs/{jobId}/stream

- Server-sent events with transcription segments in `data` frames.

Both endpoints return `404` if the job is not found or has no active
subscription.

---

## Resources

The API exposes multiple resource types under the `/resources` group.
All resource types follow the same CRUD pattern.

### Containers

```
POST   /resources/containers
GET    /resources/containers
GET    /resources/containers/{id}
PUT    /resources/containers/{id}
DELETE /resources/containers/{id}
POST   /resources/containers/sync
```

`sync` scans the local filesystem for registered mk8.shell workspaces.

Typical request/response bodies are `CreateContainerRequest`, `ContainerResponse`,
etc.

### Audio devices

```
POST   /resources/audiodevices
GET    /resources/audiodevices
GET    /resources/audiodevices/{id}
PUT    /resources/audiodevices/{id}
DELETE /resources/audiodevices/{id}
POST   /resources/audiodevices/sync
```

`sync` discovers WASAPI capture devices on the local machine
(Windows-only).

### Display devices

```
POST   /resources/displaydevices
GET    /resources/displaydevices
GET    /resources/displaydevices/{id}
PUT    /resources/displaydevices/{id}
DELETE /resources/displaydevices/{id}
POST   /resources/displaydevices/sync
```

`sync` enumerates connected displays.

**CreateDisplayDeviceRequest:**

```json
{
  "name": "string",
  "deviceIdentifier": "string | null",
  "displayIndex": 0,
  "description": "string | null"
}
```

**DisplayDeviceResponse:**

```json
{
  "id": "guid",
  "name": "string",
  "deviceIdentifier": "string | null",
  "displayIndex": 0,
  "description": "string | null",
  "skillId": "guid | null",
  "createdAt": "datetime"
}
```

### Editor sessions

Editor sessions are resource entities that represent registered IDE
connections. They are used for permission grants on editor actions.

```
POST   /resources/editorsessions
GET    /resources/editorsessions
GET    /resources/editorsessions/{id}
PUT    /resources/editorsessions/{id}
DELETE /resources/editorsessions/{id}
```

**CreateEditorSessionRequest:**

```json
{
  "name": "string",
  "editorType": "VisualStudio2026 | VisualStudioCode | Other",
  "editorVersion": "string | null",
  "workspacePath": "string | null",
  "description": "string | null"
}
```

**UpdateEditorSessionRequest:**

```json
{
  "name": "string | null",
  "description": "string | null"
}
```

**EditorSessionResponse:**

```json
{
  "id": "guid",
  "name": "string",
  "editorType": "VisualStudio2026",
  "editorVersion": "string | null",
  "workspacePath": "string | null",
  "description": "string | null",
  "isConnected": true,
  "createdAt": "datetime"
}
```

### Resource lookup

#### GET /resources/lookup/{type}

Returns lightweight `[{id, name}]` items for the resource type that
backs a given permission access category. The `type` path parameter
matches the JSON property names used in the permissions API.

**Valid `type` values:**

| Type | Resource |
|------|----------|
| `dangerousShellAccesses` | SystemUsers |
| `safeShellAccesses` | Containers |
| `containerAccesses` | Containers |
| `websiteAccesses` | Websites |
| `searchEngineAccesses` | SearchEngines |
| `localInfoStoreAccesses` | LocalInformationStores |
| `externalInfoStoreAccesses` | ExternalInformationStores |
| `audioDeviceAccesses` | AudioDevices |
| `displayDeviceAccesses` | DisplayDevices |
| `editorSessionAccesses` | EditorSessions |
| `agentAccesses` | Agents |
| `taskAccesses` | ScheduledTasks |
| `skillAccesses` | Skills |

**Response `200`:**

```json
[
  { "id": "guid", "name": "string" }
]
```

**Response `400`:** Unknown resource type.

---

## Roles

Roles define permission sets that can be assigned to agents.

### GET /roles

List all roles.

**Response `200`:** `RoleResponse[]`

---

### GET /roles/{id}

**Response `200`:** `RoleResponse`
**Response `404`:** Not found.

---

### GET /roles/{id}/permissions

**Response `200`:** `RolePermissionsResponse`
**Response `404`:** Not found.

---

### PUT /roles/{id}/permissions

Replace the entire permission set of a role. The calling user must
hold every permission they are granting — you cannot give what you
don't have.

**Request:**

```json
{
  "defaultClearance": "Independent",
  "canCreateSubAgents": false,
  "canCreateContainers": false,
  "canRegisterInfoStores": false,
  "canAccessLocalhostInBrowser": false,
  "canAccessLocalhostCli": false,
  "canClickDesktop": false,
  "canTypeOnDesktop": false,
  "dangerousShellAccesses": [{ "resourceId": "guid", "clearance": "Independent" }],
  "safeShellAccesses": [{ "resourceId": "guid", "clearance": "Independent" }],
  "containerAccesses": null,
  "websiteAccesses": null,
  "searchEngineAccesses": null,
  "localInfoStoreAccesses": null,
  "externalInfoStoreAccesses": null,
  "audioDeviceAccesses": null,
  "agentAccesses": null,
  "taskAccesses": null,
  "skillAccesses": null
}
```

Each `*Accesses` array contains `ResourceGrant` objects:

```json
{ "resourceId": "guid", "clearance": "Independent" }
```

Use `ffffffff-ffff-ffff-ffff-ffffffffffff` as the `resourceId` for a
wildcard grant that covers all resources of that type.

**Response `200`:** `RolePermissionsResponse`
**Response `403`:** Caller lacks the permissions they are trying to grant.
**Response `404`:** Not found.

### RoleResponse

```json
{
  "id": "guid",
  "name": "string",
  "permissionSetId": "guid | null"
}
```

### RolePermissionsResponse

```json
{
  "roleId": "guid",
  "roleName": "string",
  "defaultClearance": "Independent",
  "canCreateSubAgents": false,
  "canCreateContainers": false,
  "canRegisterInfoStores": false,
  "canAccessLocalhostInBrowser": false,
  "canAccessLocalhostCli": false,
  "canClickDesktop": false,
  "canTypeOnDesktop": false,
  "dangerousShellAccesses": [{ "resourceId": "guid", "clearance": "Independent" }],
  "safeShellAccesses": [],
  "containerAccesses": [],
  "websiteAccesses": [],
  "searchEngineAccesses": [],
  "localInfoStoreAccesses": [],
  "externalInfoStoreAccesses": [],
  "audioDeviceAccesses": [],
  "agentAccesses": [],
  "taskAccesses": [],
  "skillAccesses": []
}
```

---

## Default resources

Default resources determine which resource is used when a job is
submitted without an explicit `resourceId`. Defaults can be set at both
channel and context level. Resolution order: channel → context.

### GET /channels/{id}/defaults

**Response `200`:** `DefaultResourcesResponse`
**Response `404`:** Channel not found or no defaults set.

### PUT /channels/{id}/defaults

**Request:** `SetDefaultResourcesRequest`
**Response `200`:** `DefaultResourcesResponse`

### GET /channel-contexts/{id}/defaults

**Response `200`:** `DefaultResourcesResponse`
**Response `404`:** Context not found or no defaults set.

### PUT /channel-contexts/{id}/defaults

**Request:** `SetDefaultResourcesRequest`
**Response `200`:** `DefaultResourcesResponse`

### SetDefaultResourcesRequest

```json
{
  "dangerousShellResourceId": "guid | null",
  "safeShellResourceId": "guid | null",
  "containerResourceId": "guid | null",
  "websiteResourceId": "guid | null",
  "searchEngineResourceId": "guid | null",
  "localInfoStoreResourceId": "guid | null",
  "externalInfoStoreResourceId": "guid | null",
  "audioDeviceResourceId": "guid | null",
  "displayDeviceResourceId": "guid | null",
  "agentResourceId": "guid | null",
  "taskResourceId": "guid | null",
  "skillResourceId": "guid | null",
  "transcriptionModelId": "guid | null",
  "editorSessionResourceId": "guid | null"
}
```

### DefaultResourcesResponse

Same fields as the request, plus an `id` field for the
`DefaultResourceSetDB` entity.

---

## Local models

In-process inference via LLamaSharp (LLM) and Whisper.net (STT).
Endpoints live under `/models/local`.

### POST /models/local/download

Download a GGUF model file from a URL (e.g. Hugging Face) and register
it as a local model.

**Request:**

```json
{
  "url": "string",
  "name": "string | null",
  "quantization": "string | null",
  "gpuLayers": "int | null"
}
```

**Response `200`:** `LocalModelFileResponse`

---

### GET /models/local/download/list?url={url}

List available GGUF files at a Hugging Face model URL.

**Response `200`:** `ResolvedModelFileResponse[]`

```json
[
  {
    "downloadUrl": "string",
    "filename": "string",
    "quantization": "string | null"
  }
]
```

---

### GET /models/local

List all downloaded local models.

**Response `200`:** `LocalModelFileResponse[]`

---

### POST /models/local/{modelId}/load

Pin a model in memory. Optionally override GPU layers and context size.

**Request:**

```json
{
  "gpuLayers": "int | null",
  "contextSize": "uint | null"
}
```

**Response `200`:** `{ "modelId": "guid", "pinned": true }`

---

### POST /models/local/{modelId}/unload

Unpin a model (it will be evicted when idle).

**Response `200`**

---

### DELETE /models/local/{modelId}

Delete the local model file and its DB record.

**Response `204`:** Deleted.
**Response `404`:** Not found.

---

### LocalModelFileResponse

```json
{
  "id": "guid",
  "modelId": "guid",
  "modelName": "string",
  "sourceUrl": "string",
  "filePath": "string",
  "fileSizeBytes": 0,
  "quantization": "string | null",
  "status": "Ready",
  "downloadProgress": 1.0,
  "isLoaded": true
}
```

`status` is a `LocalModelStatus`: `Pending`, `Downloading`, `Ready`, `Failed`.

---

## Editor bridge

The editor bridge provides a WebSocket connection for IDE extensions
(VS 2026, VS Code) and a REST endpoint for querying active sessions.

### WS /editor/ws

WebSocket upgrade endpoint. IDE extensions connect here and send a
registration message, then enter a request/response loop managed by
`EditorBridgeService`.

**Registration message (extension → server):**

```json
{
  "editorType": "VisualStudio2026 | VisualStudioCode | Other",
  "editorVersion": "string | null",
  "workspacePath": "string | null"
}
```

**Request (server → extension):**

```json
{
  "requestId": "guid",
  "action": "string",
  "params": { ... }
}
```

**Response (extension → server):**

```json
{
  "requestId": "guid",
  "success": true,
  "data": "string | null",
  "error": "string | null"
}
```

30-second timeout per request.

---

### GET /editor/sessions

List all currently connected editor sessions.

**Response `200`:**

```json
[
  {
    "sessionId": "guid",
    "editorType": "VisualStudio2026",
    "editorVersion": "string | null",
    "workspacePath": "string | null",
    "isConnected": true,
    "connectedAt": "datetime"
  }
]
```

---

## Task definitions & instances

Tasks are user-defined C# scripts that run as background processes.
A **definition** is the registered source code; an **instance** is a
single execution of that definition.

### POST /tasks

Register a new task definition from raw `.cs` source.

**Request:**

```json
{
  "sourceText": "string"
}
```

**Response `200`:** `TaskDefinitionResponse`

---

### GET /tasks

List all task definitions.

**Response `200`:** `TaskDefinitionResponse[]`

---

### GET /tasks/{taskId}

**Response `200`:** `TaskDefinitionResponse`
**Response `404`:** Not found.

---

### PUT /tasks/{taskId}

Update an existing definition's source or active flag.

**Request:**

```json
{
  "sourceText": "string | null",
  "isActive": "bool | null"
}
```

**Response `200`:** `TaskDefinitionResponse`
**Response `404`:** Not found.

---

### DELETE /tasks/{taskId}

**Response `204`:** Deleted.
**Response `404`:** Not found.

---

### TaskDefinitionResponse

```json
{
  "id": "guid",
  "name": "string",
  "description": "string | null",
  "outputTypeName": "string | null",
  "isActive": true,
  "parameters": [
    {
      "name": "string",
      "typeName": "string",
      "description": "string | null",
      "defaultValue": "string | null",
      "isRequired": true
    }
  ],
  "createdAt": "datetime",
  "updatedAt": "datetime",
  "customId": "string | null"
}
```

---

### POST /tasks/{taskId}/instances

Start a new instance of a task definition.

**Request:**

```json
{
  "channelId": "guid | null",
  "parameterValues": { "key": "value" }
}
```

**Response `200`:** `TaskInstanceResponse`

---

### GET /tasks/{taskId}/instances

List all instances for a definition.

**Response `200`:** `TaskInstanceResponse[]`

---

### GET /tasks/{taskId}/instances/{instanceId}

Get a single instance by ID. The response includes `channelCost`
when the instance is bound to a channel (token usage for the
channel is computed on the fly).

**Response `200`:** `TaskInstanceResponse`
**Response `404`:** Not found.

---

### POST /tasks/{taskId}/instances/{instanceId}/cancel

Cancel a running instance.

**Response `204`:** Cancelled.
**Response `404`:** Not found.

---

### POST /tasks/{taskId}/instances/{instanceId}/stop

Gracefully stop an instance via the orchestrator.

**Response `204`**

---

### POST /tasks/{taskId}/instances/{instanceId}/start

Start execution of a queued instance via the orchestrator.

**Response `204`**

---

### GET /tasks/{taskId}/instances/{instanceId}/outputs?since={datetime}

Retrieve task output entries. Optionally filter by timestamp.

**Response `200`:** `TaskOutputEntryResponse[]`

```json
[
  {
    "id": "guid",
    "sequence": 1,
    "data": "string | null",
    "timestamp": "datetime"
  }
]
```

---

### GET /tasks/{taskId}/instances/{instanceId}/stream

Streams `TaskOutputEvent` items as server-sent events
(`text/event-stream`).

Each SSE frame:

```
data:{"type":"Output","sequence":1,"timestamp":"...","data":"..."}
```

Event types: `Output`, `Log`, `StatusChange`, `Done`.

---

### TaskInstanceResponse

```json
{
  "id": "guid",
  "taskDefinitionId": "guid",
  "taskName": "string",
  "status": "Queued | Running | Paused | Completed | Failed | Cancelled",
  "outputSnapshotJson": "string | null",
  "errorMessage": "string | null",
  "logs": [
    {
      "message": "string",
      "level": "string",
      "timestamp": "datetime"
    }
  ],
  "createdAt": "datetime",
  "startedAt": "datetime | null",
  "completedAt": "datetime | null",
  "channelId": "guid | null",
  "channelCost": { /* ChannelCostResponse, see Token cost tracking */ }
}
```

`channelCost` is populated on the `GET .../instances/{instanceId}`
endpoint when the instance is bound to a channel. It shows the
aggregated token usage for that channel.

---

## Token cost tracking

Token usage is tracked per-message and aggregated at the channel and
thread level. Cost data is **piggybacked** on the main chat, job,
and task responses so callers rarely need the dedicated cost endpoints.

### Piggybacked cost fields

| Response type | Field(s) | When populated |
|---------------|----------|----------------|
| `ChatResponse` | `channelCost`, `threadCost` | Always (every chat turn) |
| `AgentJobResponse` | `channelCost` | On detail / mutation endpoints (`GET`, `POST approve/stop/cancel`, `PUT pause/resume`) |
| `TaskInstanceResponse` | `channelCost` | On `GET .../instances/{instanceId}` when bound to a channel |
| SSE `Done` event | Inside the `ChatResponse` payload | Always |

### ChannelCostResponse

```json
{
  "channelId": "guid",
  "totalPromptTokens": 0,
  "totalCompletionTokens": 0,
  "totalTokens": 0,
  "agentBreakdown": [
    {
      "agentId": "guid",
      "agentName": "string",
      "promptTokens": 0,
      "completionTokens": 0,
      "totalTokens": 0
    }
  ]
}
```

### ThreadCostResponse

Same shape as `ChannelCostResponse` with an additional `threadId` field:

```json
{
  "threadId": "guid",
  "channelId": "guid",
  "totalPromptTokens": 0,
  "totalCompletionTokens": 0,
  "totalTokens": 0,
  "agentBreakdown": [ /* AgentTokenBreakdown[] */ ]
}
```

### Diagnostic endpoints

Dedicated endpoints for querying cost without a chat/job round-trip:

#### GET /channels/{id}/chat/cost

**Response `200`:** `ChannelCostResponse`

#### GET /channels/{id}/chat/threads/{threadId}/cost

**Response `200`:** `ThreadCostResponse`
**Response `404`:** Thread not found.

---

## Provider cost

Query real provider-side cost data (where supported by the provider's
usage API).

### GET /providers/{id}/cost

Get cost data for a single provider.

**Query parameters:**

| Param | Type | Default | Description |
|-------|------|---------|-------------|
| `days` | int | 30 | Number of days to look back |
| `startDate` | DateTimeOffset | — | Explicit period start (overrides `days`) |
| `endDate` | DateTimeOffset | — | Explicit period end |

**Response `200`:** `ProviderCostResponse`
**Response `404`:** Provider not found.

---

### GET /providers/cost/total

Aggregate cost across all providers.

Same query parameters as above.

**Response `200`:** `ProviderCostTotalResponse`

---

### ProviderCostResponse

```json
{
  "providerId": "guid",
  "providerName": "string",
  "providerType": "OpenAI",
  "isLocal": false,
  "costApiSupported": true,
  "totalCost": 12.34,
  "currency": "USD",
  "periodStart": "datetime",
  "periodEnd": "datetime",
  "dailyBreakdown": [
    {
      "start": "datetime",
      "end": "datetime",
      "amount": 1.23,
      "currency": "USD"
    }
  ],
  "note": "string | null"
}
```

### ProviderCostTotalResponse

```json
{
  "totalCost": 45.67,
  "currency": "USD",
  "periodStart": "datetime",
  "periodEnd": "datetime",
  "providers": [ /* ProviderCostResponse[] */ ]
}
```

---

## Permission Resolution

When an agent action is submitted as a job, the permission system
evaluates it through two stages:

### Stage 1 — Agent capability check

The agent's **role permission set** is checked to determine whether the
agent has the grant at all and what clearance level it requires.

- `Independent` (5) → **approved immediately**, no further checks.
- Any other clearance level → proceeds to Stage 2.
- No matching grant → **denied**.

### Stage 2 — Channel / context pre-authorisation

The channel/context pre-authorisation logic is used to determine whether a
pending job can execute immediately without per-job approval.

- Conversation/channel pre-auth counts as `ApprovedByWhitelistedUser`-level authority.
- This satisfies agent clearances ≤ 2 (`ApprovedBySameLevelUser`, `ApprovedByWhitelistedUser`).
- Clearances ≥ 3 (`ApprovedByPermittedAgent`, `ApprovedByWhitelistedAgent`) always require explicit per-job approval.

The resolver checks in order: channel's own permission set → parent context → fallback (AwaitingApproval).