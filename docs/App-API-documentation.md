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

- [Enums](#enums)
- [Auth](#auth)
- [Providers](#providers)
- [Models](#models)
- [Agents](#agents)
- [Channel contexts](#channel-contexts)
- [Channels](#channels)
- [Threads](#threads)
- [Chat (per-channel)](#chat-per-channel)
- [Chat streaming (SSE)](#chat-streaming-sse)
- [Agent Jobs](#agent-jobs)
- [Transcription streaming](#transcription-streaming)
- [Resources](#resources)
- [Permission Resolution](#permission-resolution)

---

## Enums

### ProviderType

```
OpenAI, Anthropic, OpenRouter, GoogleVertexAI, GoogleGemini,
ZAI, VercelAIGateway, XAI, Groq, Cerebras, Mistral, GitHubCopilot, Custom
```

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

### ModelCapability (flags)

```
None, Chat, Transcription, ImageGeneration, Embedding, TextToSpeech
```

Values can be combined (comma-separated). Default is `Chat`.

---

## Auth

### POST /auth/register

Register a new user.

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

**Request:**

```json
{
  "username": "string",
  "password": "string",
  "rememberMe": false
}
```

**Response `200`:**

```json
{
  "accessToken": "string",
  "accessTokenExpiresAt": "datetime",
  "refreshToken": "string | null",
  "refreshTokenExpiresAt": "datetime | null"
}
```

**Response `401`:** Invalid credentials.

---

### POST /auth/refresh

**Request:**

```json
{
  "refreshToken": "string"
}
```

**Response `200`:** Same shape as login response.

**Response `401`:** Invalid or expired refresh token.

---

### POST /auth/invalidate-access-tokens

**Request:**

```json
{
  "userIds": ["guid"]
}
```

**Response `204`:** No content.

---

### POST /auth/invalidate-refresh-tokens

**Request:**

```json
{
  "userIds": ["guid"]
}
```

**Response `204`:** No content.

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
  "systemPrompt": "string | null"
}
```

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
  "systemPrompt": "string | null"
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
  "roleId": "guid",
  "callerUserId": "guid"
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
  "roleName": "string | null"
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

### ContextResponse

```json
{
  "id": "guid",
  "name": "string",
  "agentId": "guid",
  "agentName": "string",
  "permissionSetId": "guid | null",
  "createdAt": "datetime",
  "updatedAt": "datetime"
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

### ChannelResponse

```json
{
  "id": "guid",
  "title": "string",
  "agentId": "guid",
  "agentName": "string",
  "contextId": "guid | null",
  "contextName": "string | null",
  "permissionSetId": "guid | null",
  "effectivePermissionSetId": "guid | null",
  "allowedAgentIds": ["guid"],
  "createdAt": "datetime",
  "updatedAt": "datetime"
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
{ "message": "string", "agentId": "guid | null" }
```

`agentId` optionally overrides the channel's default agent. The agent
must be the channel default or in its `allowedAgentIds`.

Response `200` (example):

```json
{
  "userMessage": { "role": "user", "content": "string", "timestamp": "datetime" },
  "assistantMessage": { "role": "assistant", "content": "string", "timestamp": "datetime" },
  "jobResults": [ /* Agent job results, if any */ ]
}
```

When the assistant submits agent jobs during the turn the same
permission-resolution rules apply (see [Permission Resolution](#permission-resolution)).

### GET /channels/{id}/chat

Retrieve chat history for a channel (most recent messages, chronological order).

Response `200`: an array of message objects.

---

## Chat streaming (SSE)

The API exposes an SSE-based streaming chat endpoint which pauses for
inline approvals. Use the following endpoints under the channel route
group:

POST /channels/{id}/chat/stream

- Streams `ChatStreamEvent` items as server-sent events (`text/event-stream`).
- When a job requires approval the stream emits an `approval_required`
  event and waits for the client to POST to the companion approve
  endpoint.

POST /channels/{id}/chat/stream/approve/{jobId}

- Companion endpoint used by the client to resolve a pending approval
  emitted by a running SSE stream. Body:

```json
{ "approved": true }
```

Response `200` on success, or `404` when no pending approval exists.

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
  "transcriptionModelId": "guid | null",
  "language": "string | null"
}
```

`agentId` optionally overrides the channel's default agent. The agent
must be the channel default or in its `allowedAgentIds`.

`resourceId` is required for per-resource action types. When omitted,
defaults are resolved from the channel → context → agent role permission
sets. Global action types ignore it.

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
  where auto-detection is unreliable.

> **CLI equivalent:**
> `job submit <channelId> TranscribeFromAudioDevice <audioDeviceId> --model <id> --lang en`

**Response `200`:** `AgentJobResponse`

---

### GET /channels/{channelId}/jobs

List all jobs for a channel.

**Response `200`:** `AgentJobResponse[]`

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

**Response `200`:** `AgentJobResponse`
**Response `404`:** Not found.

---

### POST /channels/{channelId}/jobs/{jobId}/cancel

Cancel a job that has not yet completed.

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
  "transcriptionModelId": "guid | null",
  "language": "string | null",
  "segments": [
    {
      "id": "guid",
      "text": "string",
      "startTime": 0.0,
      "endTime": 1.5,
      "confidence": 0.95,
      "timestamp": "datetime"
    }
  ]
}
```

`segments` is only populated for transcription action types; `null`
otherwise.

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
Examples implemented in the handlers include containers and audio
devices.

### Containers

POST /resources/containers
GET  /resources/containers
GET  /resources/containers/{id}
PUT  /resources/containers/{id}
DELETE /resources/containers/{id}
POST /resources/containers/sync

Typical request/response bodies are `CreateContainerRequest`, `ContainerResponse`,
etc.

### Audio devices

POST /resources/audiodevices
GET  /resources/audiodevices
GET  /resources/audiodevices/{id}
PUT  /resources/audiodevices/{id}
DELETE /resources/audiodevices/{id}
POST /resources/audiodevices/sync

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
