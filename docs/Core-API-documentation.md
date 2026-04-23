# SharpClaw Core API Reference

> **Base URL:** `http://127.0.0.1:48923`
>
> **Authentication:** Every request must include an `X-Api-Key` header.
> The key is auto-generated per backend instance and written to that
> backend instance's runtime directory. Frontends, gateways, and editor
> integrations should resolve the runtime auth path through backend
> discovery metadata or an explicit runtime file path, not by assuming a
> single machine-global `%LOCALAPPDATA%/SharpClaw/.api-key` file.

All request/response bodies are JSON. Enum fields are serialized as strings.
Timestamps are ISO 8601 (`DateTimeOffset`).

---

## First-class support philosophy

SharpClaw aims to be **self-contained** — you should not need to cross-
reference two or three upstream provider docs just to get a feature
working. Provider parameters, cost tracking, model capabilities, and
wire-format mapping are all handled with typed, validated first-class
support.

If our docs are incomplete, a feature is not working correctly, or you
hit a gap in provider coverage, **open a GitHub issue before reaching
for a workaround:**

> 🐛 **https://github.com/mkn8rn/SharpClaw/issues**

Fallback mechanisms like the
[`providerParameters` escape-hatch](Provider-Parameters.md#providerparameters-escape-hatch)
exist so you can unblock yourself immediately, but they are intended as
**temporary** stopgaps. If you are relying on one regularly, that is a
sign the typed support needs to be expanded — and an issue is the
fastest way to make that happen.

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
- [Resources](#resources)
- [Roles](#roles)
- [Default resources](#default-resources)
- [Local models](#local-models)
- [Task definitions & instances](Tasks-documentation.md)
- [Token cost tracking](#token-cost-tracking)
- [Provider cost](#provider-cost)
- [Database administration](#database-administration)
- [Encryption & key management](#encryption--key-management)
- [Env file management](#env-file-management)
- [Custom chat header](#custom-chat-header)
- [Tool awareness sets](#tool-awareness-sets)
- [Permission Resolution](#permission-resolution)
- [Bundled modules](#bundled-modules)

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
Minimax, Custom, LlamaSharp, Whisper, Ollama
```

| Value | Int | Description |
|-------|-----|-------------|
| `LlamaSharp` | 13 | In-process LLM inference via LlamaSharp. No API key required. |
| `Whisper` | 17 | In-process transcription via Whisper.net. Not assignable to chat models. |
| `Ollama` | 18 | Ollama HTTP server. No API key required. Default endpoint `http://localhost:11434`. |
| `Minimax` | — | Minimax AI cloud provider. |
| `Custom` | — | Any OpenAI-compatible endpoint. |

All other values correspond to their named cloud providers.

### ActionKey

Jobs use a string-based `ActionKey` that identifies the action to execute.
The set of valid action keys is dynamic — query `GET /modules` for the
authoritative list.

Permission checks, job submission (`POST /channels/{id}/jobs`), and the CLI
`job submit` command all use `ActionKey` exclusively.

### PermissionClearance

| Value | Int | Description |
|-------|-----|-------------|
| `Unset` | 0 | Cascade — skip this layer and fall back to the next (channel → context → role). Denied if no layer provides a concrete clearance. |
| `ApprovedBySameLevelUser` | 1 | Requires approval from a same-level user |
| `ApprovedByWhitelistedUser` | 2 | Requires approval from a whitelisted user |
| `ApprovedByPermittedAgent` | 3 | Requires approval from an agent with the same permission |
| `ApprovedByWhitelistedAgent` | 4 | Requires approval from a whitelisted agent |
| `Independent` | 5 | Agent can act without any external approval |
| `Restricted` | 6 | Hard deny — action is blocked at this layer regardless of other layers. No approval path exists. |

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

### ChatClientType

```
CLI, API, Telegram, Discord, WhatsApp, VisualStudio, VisualStudioCode,
UnoWindows, UnoAndroid, UnoMacOS, UnoLinux, UnoBrowser, Other
```

Identifies the client interface that originated a chat message. Included
in the chat header so the agent knows the communication channel.

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
   `X-Api-Key` header. The key is auto-generated per backend instance and
   written to that instance's runtime `.api-key` file. This is for local
   machine trust, not user identity. Consumers should resolve the path
   from backend discovery metadata or an explicit runtime auth path.
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

### Development / testing auth flags

Both authentication layers can be individually disabled via `.env` for
local development and testing.  **Never disable in production.**

| `.env` key | Layer disabled | Default |
|---|---|---|
| `Auth:DisableApiKeyCheck` | API-key middleware (`X-Api-Key` header) — all requests pass without a key | `false` |
| `Auth:DisableAccessTokenCheck` | JWT session middleware — all requests pass without a Bearer token | `false` |
| `Agent:DisableCustomProviderParameters` | Custom `providerParameters` dictionary — when `true`, the escape-hatch dictionary is stripped before sending to the provider (typed fields still apply) | `false` |

Example `.env` snippet:

```json
{
  "Auth": {
    "DisableApiKeyCheck": true,
    "DisableAccessTokenCheck": true
  },
  "Agent": {
    "DisableCustomProviderParameters": false
  }
}
```

When `DisableApiKeyCheck` is `true`, the `ApiKeyMiddleware` short-circuits
immediately (equivalent to every request carrying a valid key).

When `DisableAccessTokenCheck` is `true`, the `JwtSessionMiddleware`
skips enforcement — no 401 is returned for missing/expired tokens on
non-exempt paths.  JWT parsing still runs, so if a valid Bearer token
is present, `SessionService.UserId` is populated normally.  Endpoints
that rely on `SessionService.UserId` (e.g. `GET /auth/me`) still work
when a token is provided; they just won't be *required*.

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

**CLI:** `me`

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

| Query param  | Type   | Required | Description                         |
|--------------|--------|----------|-------------------------------------|
| `providerId` | `guid` | No       | Filter models by provider.          |

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
  "maxCompletionTokens": "integer | null",
  "temperature": "number | null",
  "topP": "number | null",
  "topK": "integer | null",
  "frequencyPenalty": "number | null",
  "presencePenalty": "number | null",
  "stop": ["string"] | null,
  "seed": "integer | null",
  "responseFormat": { "type": "json_object" } | null,
  "reasoningEffort": "string | null",
  "providerParameters": { "key": "value" },
  "customChatHeader": "string | null",
  "toolAwarenessSetId": "guid | null",
  "disableToolSchemas": false
}
```

`disableToolSchemas` suppresses all tool-call schemas and the tool
instruction suffix from the model prompt. When `true`, the model sees
only the system prompt and conversation history — no tools are offered.
The channel's value overrides the agent's (`channel || agent`).

`maxCompletionTokens` caps the number
response. Sent as `max_tokens`, `max_completion_tokens`, or
`max_output_tokens` depending on the provider and API version. `null`
(default) means no limit — the provider default applies.

`customChatHeader` is an optional template string that overrides the
default chat header for this agent. See [Custom chat header](#custom-chat-header)
for the full tag reference and examples.

`toolAwarenessSetId` links a [tool awareness set](#tool-awareness-sets)
that controls which tool-call schemas are included in API requests.
Channel's set overrides the agent's; `null` means all tools enabled.

**First-class completion parameters** — `temperature`, `topP`, `topK`,
`frequencyPenalty`, `presencePenalty`, `stop`, `seed`, `responseFormat`,
and `reasoningEffort` are typed, validated against per-provider
constraints at create/update time, and mapped natively into each
provider's wire format. Invalid values produce an immediate **HTTP 400**
with structured error messages identifying the provider, the invalid
value, and the expected range.

`providerParameters` is an optional escape-hatch dictionary of arbitrary
key-value pairs merged into the API payload after typed fields. Keys
the client already sets (e.g. `model`, `messages`, `tools`) are never
overwritten.

> 📖 **For the complete provider support matrix, valid ranges, wire
> format mapping, Google Gemini translation rules, `responseFormat`
> values, and validation details, see
> [Provider-Parameters.md](Provider-Parameters.md).**

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
  "maxCompletionTokens": "integer | null",
  "temperature": "number | null",
  "topP": "number | null",
  "topK": "integer | null",
  "frequencyPenalty": "number | null",
  "presencePenalty": "number | null",
  "stop": ["string"] | null,
  "seed": "integer | null",
  "responseFormat": { "type": "json_object" } | null,
  "reasoningEffort": "string | null",
  "providerParameters": { "key": "value" },
  "customChatHeader": "string | null",
  "toolAwarenessSetId": "guid | null",
  "disableToolSchemas": "bool | null"
}
```

Pass `providerParameters` as `{}`
parameters.  Omit the field (or pass `null`) to leave them unchanged.

`customChatHeader`: pass a template string to set, `""` (empty string)
to clear, or `null` / omit to leave unchanged.

`toolAwarenessSetId`: pass a GUID to assign, `Guid.Empty`
(`00000000-...`) to clear, or `null` to leave unchanged.

For typed completion parameters: omit a field (or pass `null`) to leave
it unchanged. Pass `stop` as `[]` (empty array) to clear stop sequences.

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
  "maxCompletionTokens": "integer | null",
  "temperature": "number | null",
  "topP": "number | null",
  "topK": "integer | null",
  "frequencyPenalty": "number | null",
  "presencePenalty": "number | null",
  "stop": ["string"] | null,
  "seed": "integer | null",
  "responseFormat": { "type": "json_object" } | null,
  "reasoningEffort": "string | null",
  "providerParameters": { "key": "value" },
  "customChatHeader": "string | null",
  "toolAwarenessSetId": "guid | null",
  "disableToolSchemas": false,
  "cost": null
}
```

`cost` is an optional `AgentCostResponse` object. It is `null` on
standard CRUD responses; use the dedicated `GET /agents/{id}/cost`
endpoint to retrieve the full per-agent token breakdown.

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
  "maxCompletionTokens": "integer | null",
  "temperature": "number | null",
  "topP": "number | null",
  "topK": "integer | null",
  "frequencyPenalty": "number | null",
  "presencePenalty": "number | null",
  "stop": ["string"] | null,
  "seed": "integer | null",
  "responseFormat": { "type": "json_object" } | null,
  "reasoningEffort": "string | null",
  "providerParameters": { "key": "value" },
  "toolAwarenessSetId": "guid | null",
  "disableToolSchemas": false
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
`task`, `skill`, `transcriptionmodel`, `editor`, `document`, `nativeapp`.

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
  "allowedAgentIds": ["guid", ...] | null,
  "customChatHeader": "string | null",
  "toolAwarenessSetId": "guid | null",
  "disableToolSchemas": false
}
```

`disableToolSchemas` suppresses all tool-call schemas for this channel,
overriding the agent's setting. See [Tool awareness sets](#tool-awareness-sets).

`customChatHeader` is an optional template string that overrides the
agent's header
[Custom chat header](#custom-chat-header) for the full tag reference.

`toolAwarenessSetId` links a [tool awareness set](#tool-awareness-sets).
Overrides the agent's set when present.

`allowedAgentIds` specifies additional agents (beyond the default
`agentId`) allowed to operate on this channel.
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
  "allowedAgentIds": ["guid", ...] | null,
  "customChatHeader": "string | null",
  "toolAwarenessSetId": "guid | null",
  "disableToolSchemas": "bool | null"
}
```

`customChatHeader`: pass a template string to set, `""` (empty string)
to clear, or `null` / omit to leave unchanged.

`toolAwarenessSetId`: pass a GUID to assign, `Guid.Empty`
(`00000000-...`) to clear, or `null` to leave unchanged.

`disableToolSchemas`: pass `true`/`false` to set, or `null` to leave
unchanged.

When `allowedAgentIds` is provided
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
`task`, `skill`, `transcriptionmodel`, `editor`, `document`, `nativeapp`.

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
  "disableToolSchemas": false,
  "customChatHeader": "string | null",
  "toolAwarenessSetId": "guid | null",
  "createdAt": "datetime",
  "updatedAt": "datetime"
}
```

`agent` and each entry in `allowedAgents` are full
[`AgentSummary`](#agentsummary) objects.

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
  "threadCost": null,
  "agentCost": { /* AgentCostResponse — see Token cost tracking */ }
}
```

Every chat response piggybacks the current channel (and thread, when
applicable) and agent token usage so callers do not need a separate
round-trip to the `/cost` endpoints. See [Token cost tracking](#token-cost-tracking)
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
  "actionKey": "string",
  "resourceId": "guid | null",
  "agentId": "guid | null",
  "callerAgentId": "guid | null"
}
```

`actionKey` identifies the action to execute. The set of valid action
keys is dynamic — query `GET /modules` for the authoritative list.

`agentId` optionally overrides the channel's default agent. The agent
must be the channel default or in its `allowedAgentIds`.

`resourceId` is required for per-resource action types. When omitted,
defaults are resolved from the channel → context → agent role permission
sets. Global action types ignore it.

> **Module-specific fields:** The DTO may include additional fields
> consumed by the module that owns the action key (e.g. shell
> type/script parameters, transcription parameters). These fields are
> ignored by the core and passed through to the module. See individual
> module documentation for details.

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
    "actionKey": "string",
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

Gracefully stop a long-running job (completes it normally).
Also accepts `Paused` jobs.

**Response `200`:** `AgentJobResponse`
**Response `404`:** Not found.

---

### POST /channels/{channelId}/jobs/{jobId}/cancel

Cancel a job that has not yet completed. Also accepts `Paused` jobs.

**Response `200`:** `AgentJobResponse`
**Response `404`:** Not found.

---

### PUT /channels/{channelId}/jobs/{jobId}/pause

Pause a long-running job. The specific effect depends on the module
that owns the action (e.g. stopping capture loops, suspending
processing). The job can be resumed later.

Only jobs with status `Executing` can be paused.

**Response `200`:** `AgentJobResponse`
**Response `404`:** Not found.

---

### PUT /channels/{channelId}/jobs/{jobId}/resume

Resume a previously paused job. The module restarts its processing
loop using the original job parameters.

Only jobs with status `Paused` can be resumed.

**Response `200`:** `AgentJobResponse`
**Response `404`:** Not found.

---

### POST /channels/{channelId}/jobs/{jobId}/segments

Push a data segment into an executing job. Used by modules for
incremental output (e.g. transcription segments).

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

Retrieve segments added after the given timestamp.

**Response `200`:** `TranscriptionSegmentResponse[]`

---

### AgentJobResponse

```json
{
  "id": "guid",
  "channelId": "guid",
  "agentId": "guid",
  "actionKey": "string",
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
  },
  "jobCost": {
    "totalPromptTokens": 0,
    "totalCompletionTokens": 0,
    "totalTokens": 0
  }
}
```

The response also includes module-specific fields from the original
request (e.g. shell type, transcription parameters) when applicable.

`segments` is populated for action types that produce incremental
output (e.g. transcription); `null` otherwise.

`jobCost` contains the prompt and completion tokens attributed to this
specific job from the LLM round that triggered it. `null` for jobs
that were not submitted during a chat tool-call round.

`channelCost` is piggybacked on all detail / mutation responses
(Submit, GetById, Approve, Stop, Cancel, Pause, Resume) so callers
receive up-to-date token usage without a separate round-trip.
List and summary endpoints omit it.

---

## Resources

The API exposes multiple resource types under the `/resources` group.
All resource types follow the same CRUD pattern.

The set of available `/resources/*` endpoints is not fixed — it grows
or shrinks as modules are enabled or disabled. Each module registers
its own resource types at startup. All follow the same pattern:

```
POST   /resources/{type}
GET    /resources/{type}
GET    /resources/{type}/{id}
PUT    /resources/{type}/{id}
DELETE /resources/{type}/{id}
POST   /resources/{type}/sync        (some types)
```

Query `GET /modules` for each module's registered resource types and
their request/response shapes. See individual module documentation
for details.

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
| `documentSessionAccesses` | DocumentSessions |
| `nativeApplicationAccesses` | NativeApplications |

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
  "canCreateSubAgents": false,
  "canCreateContainers": false,
  "canRegisterInfoStores": false,
  "canAccessLocalhostInBrowser": false,
  "canAccessLocalhostCli": false,
  "canClickDesktop": false,
  "canTypeOnDesktop": false,
  "canReadCrossThreadHistory": false,
  "canEditAgentHeader": false,
  "canEditChannelHeader": false,
  "canCreateDocumentSessions": false,
  "createDocumentSessionsClearance": "Unset",
  "canEnumerateWindows": false,
  "enumerateWindowsClearance": "Unset",
  "canFocusWindow": false,
  "focusWindowClearance": "Unset",
  "canCloseWindow": false,
  "closeWindowClearance": "Unset",
  "canResizeWindow": false,
  "resizeWindowClearance": "Unset",
  "canSendHotkey": false,
  "sendHotkeyClearance": "Unset",
  "canReadClipboard": false,
  "readClipboardClearance": "Unset",
  "canWriteClipboard": false,
  "writeClipboardClearance": "Unset",
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
  "skillAccesses": null,
  "agentHeaderAccesses": null,
  "channelHeaderAccesses": null,
  "documentSessionAccesses": null,
  "nativeApplicationAccesses": null
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
  "canCreateSubAgents": false,
  "canCreateContainers": false,
  "canRegisterInfoStores": false,
  "canAccessLocalhostInBrowser": false,
  "canAccessLocalhostCli": false,
  "canClickDesktop": false,
  "canTypeOnDesktop": false,
  "canReadCrossThreadHistory": false,
  "canEditAgentHeader": false,
  "canEditChannelHeader": false,
  "canCreateDocumentSessions": false,
  "createDocumentSessionsClearance": "Unset",
  "canEnumerateWindows": false,
  "enumerateWindowsClearance": "Unset",
  "canFocusWindow": false,
  "focusWindowClearance": "Unset",
  "canCloseWindow": false,
  "closeWindowClearance": "Unset",
  "canResizeWindow": false,
  "resizeWindowClearance": "Unset",
  "canSendHotkey": false,
  "sendHotkeyClearance": "Unset",
  "canReadClipboard": false,
  "readClipboardClearance": "Unset",
  "canWriteClipboard": false,
  "writeClipboardClearance": "Unset",
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
  "skillAccesses": [],
  "agentHeaderAccesses": [],
  "channelHeaderAccesses": [],
  "documentSessionAccesses": [],
  "nativeApplicationAccesses": []
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
  "editorSessionResourceId": "guid | null",
  "documentSessionResourceId": "guid | null",
  "nativeApplicationResourceId": "guid | null"
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

## Task definitions & instances

SharpClaw tasks are user-defined C# scripts that run as managed background
processes. A **definition** is the registered source; an **instance** is a
single execution of it. Tasks can orchestrate agents, call HTTP endpoints,
stream output over SSE, and run as long-lived daemons.

The full reference — script language, attributes, step kinds, validation
diagnostic codes, execution lifecycle, permissions, agent tool exposure,
scheduling, and all endpoint shapes — is in the dedicated
**[Tasks documentation](Tasks-documentation.md)**.

---

## Token cost tracking

Token usage is tracked per-message and aggregated at the channel and
thread level. Cost data is **piggybacked** on the main chat, job,
and task responses so callers rarely need the dedicated cost endpoints.

### Piggybacked cost fields

| Response type | Field(s) | When populated |
|---------------|----------|----------------|
| `ChatResponse` | `channelCost`, `threadCost`, `agentCost` | Always (every chat turn) |
| `AgentJobResponse` | `channelCost`, `jobCost` | `channelCost` on detail / mutation endpoints (`GET`, `POST approve/stop/cancel`, `PUT pause/resume`); `jobCost` when the job was submitted during a chat tool-call round |
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

### TokenUsageResponse

Flat prompt / completion / total triple used for per-job cost:

```json
{
  "totalPromptTokens": 0,
  "totalCompletionTokens": 0,
  "totalTokens": 0
}
```

### AgentCostResponse

Aggregated token usage across all channels for a single agent:

```json
{
  "agentId": "guid",
  "agentName": "string",
  "totalPromptTokens": 0,
  "totalCompletionTokens": 0,
  "totalTokens": 0,
  "channelBreakdown": [
    {
      "channelId": "guid",
      "promptTokens": 0,
      "completionTokens": 0,
      "totalTokens": 0
    }
  ]
}
```

### Diagnostic endpoints

Dedicated endpoints for querying cost without a chat/job round-trip:

#### GET /channels/{id}/chat/cost

**Response `200`:** `ChannelCostResponse`

#### GET /channels/{id}/chat/threads/{threadId}/cost

**Response `200`:** `ThreadCostResponse`
**Response `404`:** Thread not found.

#### GET /agents/{id}/cost

**Response `200`:** `AgentCostResponse` — total token usage for the
agent across all channels, with per-channel breakdown.
**Response `404`:** Agent not found.

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

Aggregate cost across providers.

By default, only providers with an API key configured are included.
Pass `all=true` to include all providers (previous default behavior).
Pass `simple=true` to receive a simplified summary with just the total.

**Query parameters:**

| Param | Type | Default | Description |
|-------|------|---------|-------------|
| `days` | int | 30 | Number of days to look back |
| `startDate` | DateTimeOffset | — | Explicit period start (overrides `days`) |
| `endDate` | DateTimeOffset | — | Explicit period end |
| `all` | bool | false | Include all providers regardless of API key status |
| `simple` | bool | false | Return a simplified `ProviderCostSimpleResponse` |

**Response `200` (default):** `ProviderCostTotalResponse`
**Response `200` (simple):** `ProviderCostSimpleResponse`

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

### ProviderCostSimpleResponse

Returned when `simple=true` is passed.

```json
{
  "totalCost": 45.67,
  "currency": "USD",
  "periodStart": "datetime",
  "periodEnd": "datetime",
  "summary": "Cost is 45.67$ + GoogleGemini provider cost",
  "untrackedProviders": ["GoogleGemini"]
}
```

`untrackedProviders` lists providers that have an API key but do not
expose a billing API. The field is `null` when all providers are tracked.

---

## Database administration

SharpClaw supports multiple EF Core database providers, selected via the
`Database:Provider` key in the Core `.env` file. See the
[Database Configuration Guide](Database-Configuration.md) for full setup
instructions, connection strings, and migration workflows.

The admin endpoints below require the caller to be an **authenticated
user admin** (JWT + `IsUserAdmin = true`).

### GET /admin/db/status

Returns the current migration gate state and lists applied/pending
migrations.

**Response `200`:**

```json
{
  "state": "Idle",
  "applied": ["20250601120000_Initial", "20250615090000_AddTokens"],
  "pending": ["20250701100000_AddTasks"]
}
```

| Field | Type | Description |
|-------|------|-------------|
| `state` | string | `Idle`, `Draining`, or `Migrating` |
| `applied` | string[] | Migrations already applied to the database |
| `pending` | string[] | Migrations awaiting application |

**Response `403`:** Caller is not a user admin.

---

### POST /admin/db/migrate

Drains all in-flight requests (the migration gate closes), applies all
pending EF Core migrations, then reopens the gate. Only one migration
can run at a time.

**Response `200`:**

```json
{
  "applied": 1,
  "migrations": ["20250701100000_AddTasks"],
  "message": "Applied 1 migration(s)."
}
```

**Response `409`:** A migration is already in progress.

```json
{ "message": "A migration is already in progress." }
```

**Response `403`:** Caller is not a user admin.

> ⚠️ **During migration, all API requests are held at the migration gate
> until the migration completes.** Plan migrations during low-traffic
> periods.

> 💡 Migrations are never automatic. The app starts and serves requests
> even when migrations are pending — it logs a warning at startup. Use
> `GET /admin/db/status` to check, then `POST /admin/db/migrate` when
> ready.

---

## Encryption & key management

SharpClaw encrypts provider API keys at rest using **AES-256-GCM**.

### Key resolution

At startup the encryption key is resolved in order:

1. **Explicit** — `Encryption:Key` in the Core `.env` (Base64-encoded,
   exactly 256 bits / 32 bytes after decoding).
2. **Auto-generated** — if no key is configured, SharpClaw generates a
   cryptographically secure 256-bit key via
   `RandomNumberGenerator.GetBytes(32)`, persists it to
   `%LOCALAPPDATA%/SharpClaw/.encryption-key`, and reuses it on
   subsequent startups.

On Linux/macOS the generated key file is restricted to owner-only
permissions (`600`).

### Startup validation

If `Encryption:Key` is set in the Core `.env`, SharpClaw validates it
**before** the server accepts any requests:

- **Invalid Base64** — the backend crashes immediately with:
  `Encryption:Key is not valid Base64.`
- **Wrong key length** — the backend crashes immediately with:
  `Encryption:Key must be exactly 256 bits (32 bytes) after Base64 decoding. Got N bytes.`

Both errors advise removing the key from `.env` to fall back to
auto-generation.

> ⚠️ **Changing or losing the encryption key makes all previously
> encrypted provider API keys permanently unreadable.** Re-enter them
> after a key change.

> 💡 For most deployments, leave `Encryption:Key` unset and let
> SharpClaw auto-generate and persist the key. Only set it explicitly
> when you need a deterministic key across multiple instances or backup
> scenarios.

---

## Env file management

SharpClaw manages two `.env` configuration files:

- **Core** — server-side application configuration
  (`SharpClaw.Application.Infrastructure/Environment/.env`). Managed
  exclusively through the API endpoints below. Contains encryption keys,
  JWT secrets, database connection strings, local inference settings, and
  admin credentials.
- **Interface** — client-side configuration
  (`SharpClaw.Uno/Environment/.env`). Read and written directly by the
  Uno client (not exposed via the API).

Both files use JSON-with-comments format and are loaded via
`PhysicalFileProvider` into `IConfiguration`.

All Core `.env` endpoints require authentication (JWT) and enforce
server-side authorisation: the caller must be a user admin **or** the
`EnvEditor:AllowNonAdmin` setting must be `true` in the Core `.env`.

### GET /env/core/auth

Check whether the current user is authorised to read/write the Core
`.env` file. Useful as a lightweight pre-check before navigating to an
editor UI.

**Response `200`:**

```json
{ "authorised": true }
```

---

### GET /env/core

Read the raw content of the Core `.env` file.

**Response `200`:**

```json
{ "content": "{ ... raw JSON-with-comments content ... }" }
```

**Response `403`:** Caller is not authorised (not admin and
`EnvEditor:AllowNonAdmin` is not enabled).

**Response `404`:** Core `.env` file not found on disk.

---

### PUT /env/core

Overwrite the Core `.env` file with new content.

**Request:**

```json
{
  "content": "string"
}
```

**Response `200`:**

```json
{ "saved": true }
```

**Response `403`:** Caller is not authorised.

> ⚠️ **Changes to the Core `.env` require a backend restart to take
> effect.** The Uno client's env editor automatically restarts the
> backend after saving.

### Core `.env` keys

| Key | Description |
|-----|-------------|
| `Encryption:Key` | AES-256-GCM key for encrypting provider API keys at rest. Must be exactly 256 bits (32 bytes) Base64-encoded. If empty/unset, auto-generated in the current backend instance `secrets` directory. **Invalid values crash the backend at startup.** ⚠️ Changing this key makes previously encrypted API keys unreadable. |
| `Jwt:Secret` | HMAC signing key for JWT access tokens |
| `Jwt:AccessTokenLifetime` | Access token lifetime (e.g. `"00:30:00"`) |
| `Jwt:RefreshTokenLifetime` | Refresh token lifetime (e.g. `"30.00:00:00"`) |
| `ConnectionStrings:Postgres` | Optional Postgres connection string (default: EF InMemory + JSON sync) |
| `Api:ListenUrl` | HTTP listen URL (default: `http://127.0.0.1:48923`) |
| `Admin:Username` | Seeded admin username |
| `Admin:Password` | Seeded admin password |
| `Admin:ReconcilePermissions` | When `true`, reconcile the Admin role's permission set on every startup to back-fill newly added flags and wildcard grants (default: `false`) |
| `Browser:Executable` | Chromium executable path for `AccessLocalhostInBrowser` |
| `Browser:Arguments` | Extra browser launch arguments |
| `Local:GpuLayerCount` | Default GPU layers for local inference (default: `-1` = auto) |
| `Local:ContextSize` | Default context size for local models |
| `Local:KeepLoaded` | Keep models pinned after use |
| `Local:IdleCooldownMinutes` | Idle minutes before unloading unpinned models |
| `Logging:Serilog:Enabled` | Master switch for Serilog in the Core process. When `false`, Serilog sinks are disabled entirely. |
| `Logging:Serilog:ConsoleEnabled` | Enable or disable the Core console sink. |
| `Logging:Serilog:FileEnabled` | Enable or disable the Core per-instance session file sink under the backend instance `logs` directory. |
| `Logging:Serilog:RequestLoggingEnabled` | Enable or disable ASP.NET Core request logging through Serilog. |
| `Logging:Serilog:MinimumLevel` | Default Serilog level for Core logs. Safe default: `Information`. |
| `Logging:Serilog:MicrosoftMinimumLevel` | Override level for `Microsoft.*` categories. Safe default: `Warning`. |
| `Logging:Serilog:AspNetCoreMinimumLevel` | Override level for `Microsoft.AspNetCore.*` categories. Safe default: `Warning`. |
| `Logging:Serilog:EntityFrameworkCoreMinimumLevel` | Override level for `Microsoft.EntityFrameworkCore.*` categories. Safe default: `Warning`. |
| `Database:EnableDetailedErrors` | Enable EF Core detailed errors. Safe default: `true`. |
| `Database:EnableSensitiveDataLogging` | Include parameter values and entity data in EF Core logs. Safe default: `false`; enable only for local debugging. |
| `EnvEditor:AllowNonAdmin` | When `true`, non-admin users can edit the Core `.env` via the API |
| `Backend:Enabled` | When `false`, the Uno client skips launching the bundled backend |
| `Auth:DisableApiKeyCheck` | Disable API-key middleware (dev only) |
| `Auth:DisableAccessTokenCheck` | Disable JWT enforcement (dev only) |
| `Agent:DisableCustomProviderParameters` | Strip `providerParameters` escape-hatch before sending to provider |

### Interface `.env` keys

| Key | Description |
|-----|-------------|
| `Api:Url` | API base URL (default: `http://127.0.0.1:48923`) |
| `Backend:Enabled` | When `false`, the Uno client skips launching the bundled backend (default: `true`) |
| `Gateway:Enabled` | When `false`, the Uno client skips launching the bundled gateway (default: `true`) |
| `Gateway:Url` | Gateway bind URL used when the Uno client launches the bundled gateway |
| `Processes:Persistent` | Keep bundled backend and gateway alive when the Uno frontend exits |
| `Processes:AutoStart` | Register backend and gateway startup scripts at Windows login |
| `Logging:Serilog:Enabled` | Master switch for Serilog in the Uno frontend |
| `Logging:Serilog:ConsoleEnabled` | Enable or disable Uno console logging |
| `Logging:Serilog:FileEnabled` | Enable or disable Uno file logging to the Local AppData session folder |
| `Logging:Serilog:MinimumLevel` | Default Serilog level for Uno logs. Safe default: `Information`. |
| `Logging:Serilog:MicrosoftMinimumLevel` | Override level for `Microsoft.*` categories in Uno |
| `Logging:Serilog:UnoMinimumLevel` | Override level for `Uno.*` categories. Safe default: `Warning`. |

---

## Custom chat header

By default every user message sent to the model is prefixed with a
metadata header built by `ChatService.BuildChatHeaderAsync`. The
`customChatHeader` field on agents and channels lets you **replace** that
default with a free-form template string containing `{{tag}}`
placeholders that are expanded at send time.

### Override chain

| Priority | Source | How to set |
|----------|--------|------------|
| 1 (highest) | `channel.customChatHeader` | `PUT /channels/{id}` |
| 2 | `agent.customChatHeader` | `PUT /agents/{id}` |
| 3 (lowest) | Built-in default header | — |

If `disableChatHeader` is `true` on the channel (or inherited from its
context), **no header is sent** regardless of `customChatHeader`.

### Template syntax

Placeholders use double-brace syntax: `{{tagName}}`. Tags are
case-insensitive.

**Context tags** resolve to a single value:

| Tag | Output | Example |
|-----|--------|---------|
| `{{time}}` | Current UTC timestamp | `2025-07-14 09:30:00 UTC` |
| `{{user}}` | Logged-in username | `marko` |
| `{{via}}` | `ChatClientType` of the caller | `CLI` |
| `{{role}}` | User role name with granted permission names | `Admin (CreateSubAgents, SafeShell)` |
| `{{bio}}` | User bio text | `Backend developer, likes Rust` |
| `{{agent-name}}` | Agent display name | `CodeReview Agent` |
| `{{agent-role}}` | Agent role with clearance and resource-ID grants | `DevOps clearance=Independent (SafeShell[...], ManageAgent[...])` |
| `{{clearance}}` | Agent default clearance level | `Independent` |
| `{{grants}}` | User permission grant names (name-only) | `CreateSubAgents, SafeShell, ManageAgent` |
| `{{agent-grants}}` | Agent grants with enumerated resource IDs | `SafeShell[3fa85f64-...], EditTask[7c9e6679-...]` |
| `{{editor}}` | IDE context (type, file, selection) | `VisualStudio2026 18.4 file=Program.cs lang=csharp sel=10-15` |
| `{{accessible-threads}}` | Cross-channel threads the agent can read | `Debug Session [Ops Channel] (guid)` |

When a grant includes the wildcard resource
(`ffffffff-ffff-ffff-ffff-ffffffffffff`), all concrete resource IDs of
that type are resolved from the database and listed individually.

**Resource tags** enumerate entities from the database:

| Tag | Entities loaded |
|-----|-----------------|
| `{{Agents}}` | All agents |
| `{{Models}}` | All models (includes provider) |
| `{{Providers}}` | All providers |
| `{{Channels}}` | All channels |
| `{{Threads}}` | All threads |
| `{{Roles}}` | All roles |
| `{{Users}}` | All users |
| `{{Containers}}` | All containers |
| `{{Websites}}` | All websites |
| `{{SearchEngines}}` | All search engines |
| `{{AudioDevices}}` | All audio devices |
| `{{DisplayDevices}}` | All display devices |
| `{{EditorSessions}}` | All editor sessions |
| `{{Skills}}` | All skills |
| `{{SystemUsers}}` | All system users |
| `{{LocalInfoStores}}` | All local information stores |
| `{{ExternalInfoStores}}` | All external information stores |
| `{{ScheduledTasks}}` | All scheduled tasks |
| `{{Tasks}}` | All task definitions |

Without a per-item template, resource tags emit comma-separated GUIDs.
With a template, each entity is formatted using `{FieldName}` property
placeholders:

```
{{Agents:{Name} ({Id})}}
```

Fields decorated with `[HeaderSensitive]` (e.g. `PasswordHash`,
`EncryptedApiKey`) are replaced with `[redacted]`. Unknown field names
produce `[FieldName?]`.

### Permissions

Editing custom headers is controlled by two global permission flags and
two per-resource grant collections:

| Permission | Scope |
|------------|-------|
| `canEditAgentHeader` | Global flag — can edit any agent's header |
| `canEditChannelHeader` | Global flag — can edit any channel's header |
| `agentHeaderAccesses` | Per-agent grants (resource = agent ID) |
| `channelHeaderAccesses` | Per-channel grants (resource = channel ID) |

### Examples

#### Minimal context-only header

**Template:**

```
[{{time}} | {{user}} via {{via}}]
```

**Output:**

```
[2025-07-14 09:30:00 UTC | marko via CLI]
```

#### Agent self-awareness header

**Template:**

```
[time: {{time}} | user: {{user}} | agent: {{agent-name}} | role: {{agent-role}} | clearance: {{clearance}}]
```

**Output:**

```
[time: 2025-07-14 09:30:00 UTC | user: marko | agent: CodeReview Agent | role: DevOps clearance=Independent (SafeShell[3fa85f64-5717-4562-b3fc-2c963f66afa6], ManageAgent[7c9e6679-a0f9-11d2-9e96-0060976f8900]) | clearance: Independent]
```

#### Resource inventory header

**Template:**

```
[{{time}}] user={{user}} bio="{{bio}}"
Available agents: {{Agents:{Name} (model={ModelName}, provider={ProviderName})}}
Available models: {{Models:{Name} ({Id})}}
```

**Output:**

```
[2025-07-14 09:30:00 UTC] user=marko bio="Backend developer, likes Rust"
Available agents: CodeReview Agent (model=gpt-4o, provider=OpenAI), DevOps Agent (model=claude-sonnet-4-20250514, provider=Anthropic)
Available models: gpt-4o (3fa85f64-5717-4562-b3fc-2c963f66afa6), claude-sonnet-4-20250514 (7c9e6679-a0f9-11d2-9e96-0060976f8900)
```

#### Full header matching default format

The built-in default header can be reproduced exactly:

**Template:**

```
[time: {{time}} | user: {{user}} | via: {{via}} | role: {{role}} | bio: {{bio}} | agent-role: {{agent-role}}]
```

**Output:**

```
[time: 2025-07-14 09:30:00 UTC | user: marko | via: CLI | role: Admin (CreateSubAgents, SafeShell, ManageAgent) | bio: Backend developer, likes Rust | agent-role: DevOps clearance=Independent (SafeShell[3fa85f64-5717-4562-b3fc-2c963f66afa6], ManageAgent[7c9e6679-a0f9-11d2-9e96-0060976f8900])]
```

#### Editor-aware header (IDE extensions)

**Template:**

```
[{{time}} | {{user}} | {{editor}}]
```

**Output:**

```
[2025-07-14 09:30:00 UTC | marko | VisualStudio2026 18.4.2 workspace=E:\source\SharpClaw file=Program.cs lang=csharp sel=10-25 selection="public async Task RunAsync()"]
```

#### GUIDs-only resource list

**Template:**

```
Containers: {{Containers}}
```

**Output:**

```
Containers: 3fa85f64-5717-4562-b3fc-2c963f66afa6, 7c9e6679-a0f9-11d2-9e96-0060976f8900
```

#### Sensitive field protection

**Template:**

```
Users: {{Users:{Username} hash={PasswordHash}}}
```

**Output:**

```
Users: marko hash=[redacted], admin hash=[redacted]
```

#### Cross-thread awareness

**Template:**

```
[{{time}} | {{user}} | threads: {{accessible-threads}}]
```

**Output:**

```
[2025-07-14 09:30:00 UTC | marko | threads: Debug Session [Ops Channel] (d4e5f6a7-b8c9-0d1e-2f3a-4b5c6d7e8f90), Planning [Strategy Channel] (a1b2c3d4-e5f6-7890-abcd-ef1234567890)]
```

When the agent has no cross-thread access or no accessible threads
exist, the tag outputs `(none)`.

---

## Tool awareness sets

A tool awareness set is a **reusable named configuration** that controls
which tool-call schemas are sent in API requests. By disabling tools the
agent will never use, you can **significantly reduce prompt-token
overhead** — each tool schema adds hundreds of tokens to every request.

### Override chain

| Priority | Source | How to set |
|----------|--------|------------|
| 1 (highest) | `channel.disableToolSchemas` or `agent.disableToolSchemas` | `POST /channels`, `PUT /channels/{id}`, `POST /agents`, `PUT /agents/{id}`, CLI `--no-tools` |
| 2 | Channel's `toolAwarenessSetId` | `POST /channels`, `PUT /channels/{id}`, CLI `channel add --tools <setId>` |
| 3 | Agent's `toolAwarenessSetId` | `POST /agents`, `PUT /agents/{id}`, CLI `agent add --tools <setId>` |
| 4 (default) | `null` — all tools enabled | Omit the field or set `Guid.Empty` to clear |

When `disableToolSchemas` is `true` on either the channel or the agent
(`channel || agent`), **no tool schemas or tool instruction suffix are
sent** — the model sees only the system prompt and conversation history.
This takes precedence over any tool awareness set.

### Filtering logic

Tools whose key is **`true`** or **absent** from the set's `tools`
dictionary are included. Only tools explicitly set to `false` are
excluded. This means a new tool awareness set with an empty dictionary
(`{}`) enables all tools — you opt tools **out**, not in.

### REST endpoints

Route group: `/tool-awareness-sets`

#### POST /tool-awareness-sets

Create a new set.

**Request:**

```json
{
  "name": "string",
  "tools": { "tool_name": true | false, ... } | null
}
```

`tools` defaults to `{}` (empty — all tools enabled).

**Response `200`:** `ToolAwarenessSetResponse`

---

#### GET /tool-awareness-sets

List all sets.

**Response `200`:** `ToolAwarenessSetResponse[]`

---

#### GET /tool-awareness-sets/{id}

**Response `200`:** `ToolAwarenessSetResponse`
**Response `404`:** Not found.

---

#### PUT /tool-awareness-sets/{id}

**Request:**

```json
{
  "name": "string | null",
  "tools": { "tool_name": true | false, ... } | null
}
```

Omit a field (or pass `null`) to leave it unchanged. Pass `tools` as
`{}` (empty object) to reset to all-enabled.

**Response `200`:** `ToolAwarenessSetResponse`
**Response `404`:** Not found.

---

#### DELETE /tool-awareness-sets/{id}

Deletes the set. Any agents or channels referencing it will have their
`toolAwarenessSetId` set to `null` (cascade `SetNull`).

**Response `204`:** Deleted.
**Response `404`:** Not found.

---

### ToolAwarenessSetResponse

```json
{
  "id": "guid",
  "name": "string",
  "tools": { "tool_name": true | false, ... },
  "createdAt": "datetime",
  "updatedAt": "datetime"
}
```

### CLI

```
tools add <name> [json]                   Create a tool awareness set
tools list                                List all tool awareness sets
tools get <id>                            Show a set
tools update <id> [--name <n>] [json]     Update a set
tools delete <id>                         Delete a set
```

Assign to agents and channels with `--tools <setId>`.
Disable all tools with `--no-tools`:

```
agent add MyAgent #42 --tools #5
agent add MyAgent #42 --no-tools
channel add --agent #3 --tools #5 "My Channel"
channel add --agent #3 --no-tools "My Channel"
```

---

## Permission Resolution

When an agent action is submitted as a job, the permission system
evaluates it through two stages:

### Stage 1 — Clearance cascade & agent capability check

The system resolves the **effective clearance** by cascading across three
layers in order: **channel PS → context PS → agent role PS**.

At each layer:
- `Restricted` (6) → **hard deny** immediately. The error message identifies the blocking layer. No further layers are consulted.
- Any of 1–5 → **use this clearance**. First concrete value wins.
- `Unset` (0) or absent → **skip**, move to the next layer.

If all layers are `Unset` or absent → **denied** ("unset across all layers").

Once the effective clearance is resolved:
- `Independent` (5) → **approved immediately**, no further checks.
- Any other clearance level (1–4) → proceeds to Stage 2.

### Stage 2 — Channel / context pre-authorisation

The channel/context pre-authorisation logic is used to determine whether a
pending job can execute immediately without per-job approval.

- Conversation/channel pre-auth counts as `ApprovedByWhitelistedUser`-level authority.
- This satisfies agent clearances ≤ 2 (`ApprovedBySameLevelUser`, `ApprovedByWhitelistedUser`).
- Clearances ≥ 3 (`ApprovedByPermittedAgent`, `ApprovedByWhitelistedAgent`) always require explicit per-job approval.

The resolver checks in order: channel's own permission set → parent context → fallback (AwaitingApproval).

### Cross-thread history access

Reading another channel's thread history uses a **double-gate** model:

1. The agent's role permission set must have `CanReadCrossThreadHistory = true`.
2. The target channel's effective permission set must also have
   `CanReadCrossThreadHistory = true` (opt-in).

Channels that have not opted in are effectively private, even if the
agent holds the permission. `Independent` clearance on the agent's role
overrides the channel opt-in requirement.

The agent must also be the channel's primary agent or listed in its
`AllowedAgents` (channel-level first, context-level fallback).

Accessible threads are surfaced in the chat header
(`accessible-threads:` section) and via inline tools provided by the
[Context Tools](modules/Module-ContextTools.md) module.

---

## Bundled modules

SharpClaw ships with a set of default modules that register their own
action keys, resource types, and REST endpoints at startup. Each module
has its own documentation:

| Module | Documentation |
|--------|---------------|
| Agent Orchestration | [Module-AgentOrchestration.md](modules/Module-AgentOrchestration.md) |
| Bot Integration | [Module-BotIntegration.md](modules/Module-BotIntegration.md) |
| Computer Use | [Module-ComputerUse.md](modules/Module-ComputerUse.md) |
| Context Tools | [Module-ContextTools.md](modules/Module-ContextTools.md) |
| Dangerous Shell | [Module-DangerousShell.md](modules/Module-DangerousShell.md) |
| Database Access | [Module-DatabaseAccess.md](modules/Module-DatabaseAccess.md) |
| Editor Common | [Module-EditorCommon.md](modules/Module-EditorCommon.md) |
| Mk8 Shell | [Module-Mk8Shell.md](modules/Module-Mk8Shell.md) |
| Module Dev | [Module-ModuleDev.md](modules/Module-ModuleDev.md) |
| Office Apps | [Module-OfficeApps.md](modules/Module-OfficeApps.md) |
| Transcription | [Module-Transcription.md](modules/Module-Transcription.md) |
| VS 2026 Editor | [Module-VS2026Editor.md](modules/Module-VS2026Editor.md) |
| VS Code Editor | [Module-VSCodeEditor.md](modules/Module-VSCodeEditor.md) |
| Web Access | [Module-WebAccess.md](modules/Module-WebAccess.md) |

For enabling/disabling modules, see the
[Module Enablement Guide](modules/Module-Enablement-Guide.md).
