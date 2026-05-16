# SharpClaw Core API Reference

> **Base URL:** `http://127.0.0.1:48923`
>
> **Authentication:** Requests pass the per-instance `X-Api-Key` gate first.
> Non-exempt user endpoints also require `Authorization: Bearer <token>`.
> Gateway service calls may present `X-Gateway-Token` instead of a user JWT.
> Runtime auth files are generated per backend instance, so frontends,
> gateways, and editor integrations should resolve them through backend
> discovery metadata or explicit file paths rather than assuming a single
> machine-global location.

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
- [Modules](#modules)
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
OpenAI, DeepSeek, Anthropic, OpenRouter, GoogleVertexAI, GoogleGemini,
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

SharpClaw uses two authentication layers for normal callers. The first
layer is the per-instance API key in the `X-Api-Key` header. Core creates
that key at startup, writes it to the backend instance runtime `.api-key`
file, and deletes it on clean shutdown when it still owns the file. This
is a local-process trust check, not a user identity check, so clients
should resolve the key from backend discovery metadata or from an explicit
runtime file path rather than assuming a machine-global location.

The second layer is the user JWT in `Authorization: Bearer <token>`.
After the API key middleware accepts a request, `JwtSessionMiddleware`
validates the token and populates `SessionService.UserId`. Endpoints that
are not anonymous and are not exempt return `401` for a missing or invalid
token, and return `419` with `access_token_expired` when the token was
validly signed but has expired or was invalidated server-side. The exempt
paths are `/echo`, `/ping`, `/auth/login`, `/auth/register`, and
`/auth/refresh`; anonymous endpoints declared with ASP.NET Core metadata
also bypass JWT enforcement.

The gateway has a service credential in addition to caller JWTs. A gateway
request still carries the internal `X-Api-Key`, but it may also send
`X-Gateway-Token` from the backend instance `.gateway-token` file. That
token proves the request came from the trusted gateway process and lets
gateway-owned background work reach Core endpoints that do not have a
user context.

### Token lifetimes

Access tokens default to thirty minutes and refresh tokens default to
thirty days. Both are configured through the Core `.env` `Jwt` section
with TimeSpan text, for example `AccessTokenLifetime` set to `"00:30:00"`
and `RefreshTokenLifetime` set to `"30.00:00:00"`. `Jwt:Issuer` and
`Jwt:Audience` default to `SharpClaw`; `Jwt:Secret` signs tokens and may
be left unset so the backend generates and persists a per-instance secret.
Access tokens are stateless after issuance, while refresh tokens are
stored server-side and rotate on refresh.

### Development and testing auth flags

The Core `.env` can disable either authentication layer for controlled
local testing. `Auth:DisableApiKeyCheck` makes the API-key middleware pass
every request as though a valid `X-Api-Key` header was present.
`Auth:DisableAccessTokenCheck` stops JWT enforcement for protected
endpoints, although a supplied valid Bearer token is still parsed and used
to populate the session. These switches should remain `false` outside
local testing. `Agent:DisableCustomProviderParameters` is not an auth
gate, but it is a related hardening switch: when `true`, the free-form
`providerParameters` escape hatch is stripped before sending a request to
an LLM provider, while typed agent and model fields still apply.

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
specific job. Core records chat-provider usage for jobs that execute chat
work, and modules can add their own usage through `IAgentJobCostTracker`
when they call transcription, OCR, private LLM, or other model APIs from
inside a job. `jobCost` is `null` only when no usage has been recorded for
that job.

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
| `AgentJobResponse` | `channelCost`, `jobCost` | `channelCost` on detail / mutation endpoints (`GET`, `POST approve/stop/cancel`, `PUT pause/resume`); `jobCost` when core or a module has recorded token usage for that job |
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

At startup, Core first tries `Encryption:Key` from the Core `.env`. The
value must decode to exactly thirty-two bytes because provider-key
encryption and encrypted JSON persistence use AES-256-GCM. If no key is
configured there, SharpClaw creates a cryptographically secure key with
`RandomNumberGenerator.GetBytes(32)`, persists it through
`PersistentKeyStore`, and reuses it on later startups. On Linux and macOS,
generated key files are written with owner-only permissions.

### Startup validation

If `Encryption:Key` is set in the Core `.env`, SharpClaw validates it
before the server accepts requests. Invalid Base64 crashes startup with
`Encryption:Key is not valid Base64.` A decoded value with the wrong byte
length crashes startup with `Encryption:Key must be exactly 256 bits (32
bytes) after Base64 decoding. Got N bytes.` Both errors advise removing
`Encryption:Key` from `.env` to fall back to auto-generation.

> ⚠️ **Changing or losing the encryption key makes all previously
> encrypted provider API keys permanently unreadable.** Re-enter them
> after a key change.

> 💡 For most deployments, leave `Encryption:Key` unset and let
> SharpClaw auto-generate and persist the key. Only set it explicitly
> when you need a deterministic key across multiple instances or backup
> scenarios.

---

## Env file management

SharpClaw uses JSON-with-comments environment files that are loaded into
`IConfiguration`. Core reads `SharpClaw.Application.Infrastructure/Environment/.env`
and, in development, `.dev.env`. The Uno interface reads
`SharpClaw.Uno/Environment/.env` and `.dev.env` directly from the client
process. The public gateway reads `SharpClaw.Gateway/Environment/.env`
and `.dev.env` through `GatewayEnvironment.AddGatewayEnvironment()`.

The Core env file is special because it can contain encryption keys, JWT
secrets, connection strings, and initial admin credentials. It is managed
through the `/env/core/*` API endpoints below and is automatically
encrypted at rest after startup when an encryption key is available. The
Interface env file is regular client-side configuration for the current
desktop user. The Gateway env file controls public endpoint exposure,
internal API discovery, request queue behavior, and gateway logging.

All Core `.env` endpoints require a valid user JWT. The caller must be an
admin user unless `EnvEditor:AllowNonAdmin` is set to `true` in the Core
`.env`. Changes to the Core `.env` require a backend restart before they
affect the running service; the Uno env editor performs that restart after
saving.

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

The Core template is `SharpClaw.Application.Infrastructure/Environment/.env.template`.
It includes the `Encryption` section for the at-rest encryption key and
encryption toggles, the `Jwt` section for token signing, issuer, audience,
and lifetimes, and the `Auth` section for local-only bypass switches.
`Agent:DisableCustomProviderParameters` is also in the template because it
is the hardening switch for free-form provider parameters.

Database configuration lives in the `Database` section. `Database:Provider`
accepts `JsonFile`, `Postgres`, `SqlServer`, or `SQLite`, and relational
providers read their matching `ConnectionStrings` entry. The same section
also holds the JSON persistence durability options such as checksums, event
log retention, snapshots, async flush, detailed errors, and sensitive-data
logging. Migrations remain manual and are not generated or run by editing
the env file.

`Admin` controls first-run admin seeding and permission reconciliation.
`Local` controls LLamaSharp defaults such as GPU layers, context size,
keep-loaded behavior, idle unload timing, maximum cached sessions, model
directory, and optional Hugging Face token. `EnvEditor` controls whether
non-admin users may edit the Core env through the API. `UniqueNames`
contains per-entity unique-name enforcement flags. `ExternalModules` and
`Modules` control module discovery, module host guardrails, and bundled
module enablement.

### Interface `.env` keys

The Interface template is `SharpClaw.Uno/Environment/.env.template`.
`Api:Url` selects the Core API URL and is also passed to a bundled backend
as `ASPNETCORE_URLS` when the client launches one. `Backend:Enabled`
controls bundled backend launch. `Gateway:Enabled` is false in the
template so the public gateway is opt-in, and `Gateway:Url` is used only
when that bundled gateway is launched. `Processes:Persistent` keeps child
processes alive after the frontend exits, while `Processes:AutoStart`
registers Windows startup scripts. The `Logging:Serilog` section controls
frontend logging.

### Gateway `.env` keys

The Gateway template is `SharpClaw.Gateway/Environment/.env.template`.
`InternalApi` contains the selected Core API URL, timeout, optional API-key
override, optional API-key file path, optional gateway service token, and
optional gateway-token file path. Empty auth values are ignored so normal
instance discovery can resolve the runtime `.api-key` and `.gateway-token`
files. `Gateway:RequestQueue` controls mutation queueing, retry, timeout,
and max queue size. `Gateway:Endpoints` is secure by default: the master
switch is on, but each built-in public endpoint group is false until an
operator opts in. `Gateway:Modules` controls gateway-side module endpoint
loading and hot reload.

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
(`accessible-threads:` section). Older builds also exposed a separate Context
Tools module, but that module is not part of the current bundled module set.

---

## Modules

Modules are addressed by **module ID strings**, not GUIDs. Example:
`sharpclaw_agent_orchestration`.

### GET /modules

List all known bundled modules and their current state.

### GET /modules/{moduleId}

Return enriched detail for one module.

### POST /modules/{moduleId}/enable

Enable a bundled module at runtime.

- **Response `200`**: enable result
- **Response `404`**: unknown module ID
- **Response `409`**: dependency or state conflict prevented enablement

### POST /modules/{moduleId}/disable

Disable a bundled module at runtime.

- **Response `200`**: disable result
- **Response `404`**: unknown module ID
- **Response `409`**: another enabled module depends on one of its exported contracts

### POST /modules/scan

Scan the external modules directory and load newly discovered modules.

### POST /modules/{moduleId}/reload

Reload a previously loaded external module.

### POST /modules/{moduleId}/unload

Unload an external module.

### GET /modules/{moduleId}/logs

Read buffered logs for a module.

Query parameters:

| Query param | Type | Description |
|---|---|---|
| `since` | string | ISO 8601 cursor timestamp |
| `level` | string | Minimum log level |
| `take` | int | Page size, clamped to `1..500` |

### DELETE /modules/{moduleId}/logs

Clear the in-memory log buffer for a module.

### GET /modules/{moduleId}/diagnostics

Return warning/error-focused diagnostics for one module.

### GET /modules/{moduleId}/metrics
### GET /modules/metrics
### POST /modules/{moduleId}/metrics/reset
### POST /modules/metrics/reset

Read or reset aggregated module execution metrics.

### GET /modules/{moduleId}/health
### GET /modules/health

Read the last known health snapshot for one module or for all modules.

For tutorial-style module workflows, see
[guides/Module-User-Guide.md](guides/Module-User-Guide.md) and
[guides/Module-Agent-Skill.md](guides/Module-Agent-Skill.md).

---

## Bundled modules

The current `DefaultModules` tree ships agent orchestration, editor common,
metrics, module development, provider modules for Anthropic, Google,
LlamaSharp, Ollama, and OpenAI-compatible APIs, plus the VS 2026 and VS Code
editor integrations. Older docs for removed or externalized modules may remain
in the repository as historical references, but they are not part of the
current bundled module set unless a deployment supplies them as external
modules.

For the current enablement keys and base/development defaults, see the
[Module Enablement Guide](modules/Module-Enablement-Guide.md). For task and
module authoring walkthroughs, also see the `docs/guides/` folder.
