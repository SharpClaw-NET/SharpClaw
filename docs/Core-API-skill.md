# SharpClaw Core API Skill

## Purpose

Use this guide when you call the local SharpClaw Runtime Host. The base host provides stateless chat, streaming, health, and canonical Jobs.

Optional modules provide feature domains through the compiled module graph. Do not assume that a module route exists in every installation.

## Connect To Runtime

Read the Runtime discovery entry before you call the host. Use its address and the instance API-key file for the same Runtime instance.

The default local address is `http://127.0.0.1:48923`. The `ASPNETCORE_URLS` environment setting can change this address.

Send the API key in the `X-Api-Key` header. The key proves local process access. It does not identify a user.

Use `Auth__DisableApiKeyCheck=true` only in controlled local tests. A missing or invalid key returns HTTP `423`.

## Check Runtime State

Use these routes:

```text
GET /echo       liveness without the API key
GET /health     process health
GET /healthz    alternate process health route
GET /readyz     database and Runtime readiness
GET /ping       authenticated API-key check
```

`/readyz` returns HTTP `503` until database readiness and Runtime startup complete. Do not send chat or Jobs requests before readiness.

## Send Direct Chat

Call `POST /chat` with one message:

```json
{
  "message": "Return one short answer.",
  "conversationId": null
}
```

The `message` value must contain non-whitespace text. The default installation does not load or save conversation history.

The Runtime selects the provider and model through enabled provider modules. A missing or duplicate provider prevents readiness.

The response is a serialized `ChatTurnResult`. Keep provider metadata as returned by the current provider contract.

## Read Streaming Chat

Call `POST /chat/stream` with the same request body as `/chat`. Read each server-sent event from the `data:` field.

The final `ChatStreamChunk` marks completion. Pass request cancellation through the HTTP client so the Runtime can stop the kernel stream.

Buffered and streaming chat use one compiled graph, one action path, one provider transport, and one tool path.

## Inspect Runtime Configuration

Call `GET /env/core` only through a protected local administration flow. The route returns non-null configuration entries loaded by the Runtime.

Configuration sources can contain operational or secret values. Do not expose this response to an untrusted caller.

The route checks the `security.secret.read` action before it returns data. A denied decision returns HTTP `403`.

## Use Canonical Jobs

Submit a Job with `POST /jobs`:

```json
{
  "actionKey": "provider.action",
  "input": {
    "kind": "payload"
  },
  "conversationId": null,
  "holds": null
}
```

The action owner defines the valid input payload. Use an action key and payload codec from the enabled module graph.

Use these canonical Jobs routes:

```text
GET    /jobs
GET    /jobs/{jobId}
POST   /jobs/{jobId}/dispatch
POST   /jobs/{jobId}/cancel
POST   /jobs/{jobId}/pause
POST   /jobs/{jobId}/stop
POST   /jobs/{jobId}/resume
POST   /jobs/{jobId}/recover
POST   /jobs/{jobId}/resolve-hold
POST   /jobs/{jobId}/retry
DELETE /jobs/{jobId}
GET    /jobs/{jobId}/progress
GET    /jobs/{jobId}/attempts
GET    /jobs/{jobId}/artifact
```

Jobs do not require an Agent, Context, Thread, Channel, or Permission module. Every Jobs operation uses the universal action dispatcher and one module storage gateway.

## Understand Module Routes

The Runtime loads enabled manifests from the `modules` directory beside the host executable. The graph validates manifests before it publishes readiness.

Provider modules register provider plugins. Tool modules register model-visible tools. Module-owned route groups are dynamic and belong to their owning package.

An enabled module that fails validation blocks activation. The host does not remove the module silently or execute a local substitute.

## Configure The Base Host

Use canonical dotenv syntax in the Runtime `Environment/.env` file. Use `__` between configuration sections and keys.

```dotenv
ASPNETCORE_URLS=http://127.0.0.1:48923
Auth__DisableApiKeyCheck=false
Encryption__EncryptDatabase=true
Encryption__EncryptProviderKeys=true
Database__Provider=JsonFile
Modules__sharpclaw_providers_openai_compat=true
```

`Encryption__EncryptDatabase` controls database record encryption. It does not control active environment document protection.

The active environment document uses the Supprocom.Secrets installation-key boundary. `SHARPCLAW_ENCRYPTION_KEY` is an installation-key source when it is configured and valid.

`Database__Provider` selects the database provider. The Runtime validates the selected database before it publishes discovery.

`Modules__<module-id>` selects a manifest identity. Invalid or incomplete enabled modules fail closed.

## Handle Errors

The Runtime returns stable public error responses. It does not return general exception messages to API callers.

Request cancellation returns HTTP `499` before response headers start. A failure after response start is rethrown to prevent a false successful response.

The base host does not define user, role, agent, channel, thread, context, permission, history, skill, or memory routes. Use the owning module contract when that behavior is enabled.
