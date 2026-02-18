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
- [Contexts](#contexts)
- [Conversations](#conversations)
- [Chat](#chat)
- [Agent Jobs](#agent-jobs)
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
| Global flags | `ExecuteAsAdmin`, `CreateSubAgent`, `CreateContainer`, `RegisterInfoStore`, `EditAnyTask` |
| Per-resource | `ExecuteAsSystemUser`, `AccessLocalInfoStore`, `AccessExternalInfoStore`, `AccessWebsite`, `QuerySearchEngine`, `AccessContainer`, `ManageAgent`, `EditTask`, `AccessSkill` |

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

Syncs model list from the provider's API.

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

### POST /models

**Request:**

```json
{
  "name": "string",
  "providerId": "guid"
}
```

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
  "name": "string | null"
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
  "providerName": "string"
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

### AgentResponse

```json
{
  "id": "guid",
  "name": "string",
  "systemPrompt": "string | null",
  "modelId": "guid",
  "modelName": "string",
  "providerName": "string"
}
```

---

## Contexts

A context is a named group of conversations and tasks that share a common
set of pre-authorised permission grants. See [Permission Resolution](#permission-resolution).

### POST /contexts

**Request:**

```json
{
  "agentId": "guid",
  "name": "string | null",
  "permissionGrants": [
    { "actionType": "ExecuteAsAdmin", "grantedClearance": "ApprovedBySameLevelUser" }
  ]
}
```

**Response `200`:** `ContextResponse`

---

### GET /contexts?agentId={guid}

List contexts. Optional `agentId` filter.

**Response `200`:** `ContextResponse[]`

---

### GET /contexts/{id}

**Response `200`:** `ContextResponse`
**Response `404`:** Not found.

---

### PUT /contexts/{id}

**Request:**

```json
{
  "name": "string | null"
}
```

**Response `200`:** `ContextResponse`
**Response `404`:** Not found.

---

### DELETE /contexts/{id}

Deletes the context. Conversations and tasks inside it are detached
(set to standalone), not deleted.

**Response `204`:** Deleted.
**Response `404`:** Not found.

---

### POST /contexts/{id}/grant

Add or update a permission grant on the context.

**Request:**

```json
{
  "actionType": "ExecuteAsAdmin",
  "grantedClearance": "ApprovedBySameLevelUser"
}
```

**Response `200`:** `ContextResponse` (updated, including all grants).
**Response `404`:** Not found.

---

### ContextResponse

```json
{
  "id": "guid",
  "name": "string",
  "agentId": "guid",
  "agentName": "string",
  "createdAt": "datetime",
  "updatedAt": "datetime",
  "permissionGrants": [
    {
      "id": "guid",
      "actionType": "ExecuteAsAdmin",
      "grantedClearance": "ApprovedBySameLevelUser"
    }
  ]
}
```

---

## Conversations

A conversation belongs to an agent and optionally to a context. Each
conversation has its own model (changeable mid-conversation), title,
chat history, and permission grant overrides.

### POST /conversations

**Request:**

```json
{
  "agentId": "guid",
  "title": "string | null",
  "modelId": "guid | null",
  "contextId": "guid | null",
  "permissionGrants": [
    { "actionType": "AccessWebsite", "grantedClearance": "Independent" }
  ]
}
```

- `modelId` defaults to the agent's current model if omitted.
- `contextId` is optional; when set the context's grants become defaults.

**Response `200`:** `ConversationResponse`

---

### GET /conversations?agentId={guid}

List conversations. Optional `agentId` filter.

**Response `200`:** `ConversationResponse[]`

---

### GET /conversations/{id}

**Response `200`:** `ConversationResponse`
**Response `404`:** Not found.

---

### PUT /conversations/{id}

**Request:**

```json
{
  "title": "string | null",
  "modelId": "guid | null",
  "contextId": "guid | null"
}
```

- Set `contextId` to a valid GUID to attach to a context.
- Set `contextId` to `"00000000-0000-0000-0000-000000000000"` to detach.

**Response `200`:** `ConversationResponse`
**Response `404`:** Not found.

---

### DELETE /conversations/{id}

**Response `204`:** Deleted.
**Response `404`:** Not found.

---

### POST /conversations/{id}/grant

Add or update a per-conversation permission override.

**Request:**

```json
{
  "actionType": "ExecuteAsAdmin",
  "grantedClearance": "Independent"
}
```

**Response `200`:** `ConversationResponse` (updated).
**Response `404`:** Not found.

---

### ConversationResponse

```json
{
  "id": "guid",
  "title": "string",
  "agentId": "guid",
  "agentName": "string",
  "modelId": "guid",
  "modelName": "string",
  "contextId": "guid | null",
  "contextName": "string | null",
  "createdAt": "datetime",
  "updatedAt": "datetime",
  "permissionGrants": [
    {
      "id": "guid",
      "actionType": "AccessWebsite",
      "grantedClearance": "Independent"
    }
  ],
  "effectivePermissions": [
    {
      "actionType": "ExecuteAsAdmin",
      "grantedClearance": "ApprovedBySameLevelUser",
      "source": "context"
    },
    {
      "actionType": "AccessWebsite",
      "grantedClearance": "Independent",
      "source": "conversation"
    }
  ]
}
```

---

## Chat

All chat operations are scoped to a conversation.

### POST /conversations/{conversationId}/chat

Send a message and receive the assistant's reply.

**Request:**

```json
{
  "message": "string"
}
```

**Response `200`:**

```json
{
  "userMessage": {
    "role": "user",
    "content": "string",
    "timestamp": "datetime"
  },
  "assistantMessage": {
    "role": "assistant",
    "content": "string",
    "timestamp": "datetime"
  }
}
```

The conversation's model and the agent's system prompt are used for
completion. Message history (up to 50 most recent messages in the
conversation) is included automatically.

---

### GET /conversations/{conversationId}/chat

Retrieve chat history for a conversation (most recent 50 messages,
chronological order).

**Response `200`:**

```json
[
  {
    "role": "user",
    "content": "string",
    "timestamp": "datetime"
  },
  {
    "role": "assistant",
    "content": "string",
    "timestamp": "datetime"
  }
]
```

---

## Agent Jobs

Jobs represent permission-gated agent actions. When a job is submitted,
the permission system evaluates it immediately:

- **Approved** → executes inline, returns `Completed` or `Failed`.
- **Pending** → returns `AwaitingApproval` (needs manual approve).
- **Denied** → returns `Denied`.

### POST /agents/{agentId}/jobs

Submit a new job.

**Request:**

```json
{
  "actionType": "ExecuteAsAdmin",
  "resourceId": "guid | null",
  "callerUserId": "guid | null",
  "callerAgentId": "guid | null"
}
```

`resourceId` is required for per-resource action types. Global action
types ignore it.

**Response `200`:** `AgentJobResponse`

---

### GET /agents/{agentId}/jobs

List all jobs for an agent.

**Response `200`:** `AgentJobResponse[]`

---

### GET /agents/{agentId}/jobs/{jobId}

**Response `200`:** `AgentJobResponse`
**Response `404`:** Not found.

---

### POST /agents/{agentId}/jobs/{jobId}/approve

Approve a job that is `AwaitingApproval`.

**Request:**

```json
{
  "approverUserId": "guid | null",
  "approverAgentId": "guid | null"
}
```

The approver's identity is re-evaluated against the clearance
requirement.

**Response `200`:** `AgentJobResponse`
**Response `404`:** Not found.

---

### POST /agents/{agentId}/jobs/{jobId}/cancel

Cancel a job.

**Response `200`:** `AgentJobResponse`
**Response `404`:** Not found.

---

### AgentJobResponse

```json
{
  "id": "guid",
  "agentId": "guid",
  "actionType": "ExecuteAsAdmin",
  "resourceId": "guid | null",
  "status": "Completed",
  "effectiveClearance": "ApprovedBySameLevelUser",
  "resultData": "string | null",
  "errorLog": "string | null",
  "logs": [
    {
      "message": "string",
      "level": "Information",
      "timestamp": "datetime"
    }
  ],
  "createdAt": "datetime",
  "startedAt": "datetime | null",
  "completedAt": "datetime | null"
}
```

---

## Permission Resolution

When an agent action is checked, the system resolves the effective
clearance through a three-level chain:

```
┌──────────────────────────────────────────────────┐
│ 1. Per-conversation / per-task grant override    │ ← highest priority
│ 2. Context-level default grant                   │
│ 3. No auto-approval (manual per-job approval)    │ ← lowest priority
└──────────────────────────────────────────────────┘
```

- If the conversation has a grant for the action type → that clearance
  is used (source: `"conversation"`).
- Else if the conversation belongs to a context that has a grant → the
  context's clearance is used (source: `"context"`).
- Else → no pre-approval; the action goes to `AwaitingApproval` status.

The `effectivePermissions` array in `ConversationResponse` shows the
merged result with source attribution.

**Example:** A context grants `ExecuteAsAdmin` at
`ApprovedBySameLevelUser`. A conversation in that context overrides
`AccessWebsite` to `Independent`. The effective permissions are:

| Action | Clearance | Source |
|--------|-----------|--------|
| `ExecuteAsAdmin` | `ApprovedBySameLevelUser` | `context` |
| `AccessWebsite` | `Independent` | `conversation` |

Standalone conversations (no context) rely solely on their own grants.
