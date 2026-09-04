# SharpClaw Core API Reference

## Scope

The Runtime Host exposes the base SharpClaw kernel API. The default installation provides one-shot model chat without conversation history.

Jobs and Events are integral kernel functions. They do not require an Agents, Context, Permission, or other feature module.

Optional modules add their own behavior through the compiled module graph. This document does not define module-owned APIs.

The base host does not expose static agent, channel, context, thread, role, user, or permission routes. Those domains belong to optional modules.

## Base URL

The default local address is `http://127.0.0.1:48923`. Set `ASPNETCORE_URLS` to select another address before startup.

Use the address in the Runtime discovery entry when another process starts the host. Do not assume one machine-wide Runtime address.

## Request Authentication

The Runtime Host uses the `X-Api-Key` header as its local process trust boundary. The key is created for the Runtime instance and is not a user identity.

The `/echo` endpoint permits anonymous liveness checks. Every other base endpoint requires a valid key unless controlled configuration disables the key check.

An invalid or missing key returns HTTP `423` with a stable JSON error. The response does not grant access to the protected pipeline.

Set `Auth__DisableApiKeyCheck=true` only for controlled local tests. Do not use this setting for a deployed Runtime.

User sessions and administrator rules are not base-host behavior. An optional module must provide those rules through the current module contracts.

## Health And Readiness

`GET /echo` returns HTTP `200` with `{ "status": "ok" }`. This route checks process liveness and does not require the API key.

`GET /health` returns HTTP `200` with `{ "status": "healthy" }` after the request reaches the Runtime Host.

`GET /healthz` returns the same health response. It is an additional health probe name.

`GET /readyz` returns HTTP `200` with `{ "status": "ready" }` after database readiness and Runtime startup complete.

`GET /readyz` returns HTTP `503` before the Runtime becomes ready. A failed database readiness check prevents discovery publication.

`GET /ping` returns HTTP `200` with `{ "status": "authenticated" }` after API-key middleware accepts the request.

## Direct Chat

`POST /chat` runs one direct model turn through the compiled kernel graph.

Use this request shape:

```json
{
  "message": "Write one short status message.",
  "conversationId": null
}
```

`message` is required and must contain non-whitespace text. The endpoint returns HTTP `400` when validation fails.

The Runtime resolves the provider and model through enabled provider modules. A missing or duplicate configured provider fails the graph before readiness.

The response is the serialized `ChatTurnResult` returned by the kernel. Provider modules can supply provider-specific metadata through the current provider contract.

The default installation uses stateless conversation behavior. It does not load or commit conversation history.

The Context module owns history and context assembly when that module is enabled. The base host does not create a hidden history store.

## Streaming Chat

`POST /chat/stream` runs the same direct model turn as `/chat` and writes `ChatStreamChunk` values as server-sent events.

Use the same JSON request shape as `/chat`. Each event uses the `data:` field and contains one serialized chunk.

The final chunk marks completion. Request cancellation stops the kernel stream and response writes through the same cancellation boundary.

The endpoint does not create a second chat pipeline. Buffered and streaming turns use the same compiled graph, action dispatcher, provider transport, and tool path.

## Runtime Configuration Inspection

`GET /env/core` returns the non-null configuration entries that the Runtime has loaded. API-key middleware protects this endpoint.

Treat this endpoint as a local administration surface. Configuration sources can contain operational or secret values.

The endpoint runs the `security.secret.read` action before it returns data. A denied security decision returns HTTP `403`.

## Canonical Jobs

The `/jobs` route group is the base Jobs API. Jobs use the canonical Core contract and one Runtime storage gateway.

`POST /jobs` submits a job. The request must contain an `actionKey` and an `input` payload that matches a registered action codec.

Use this request shape for the common fields:

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

The action owner defines the valid payload. The base host does not infer a module type from an untyped payload.

`GET /jobs` lists Jobs visible to the current action context. `GET /jobs/{jobId}` reads one Job by identifier.

`POST /jobs/{jobId}/dispatch` dispatches a queued Job. `POST /jobs/{jobId}/cancel` requests cancellation.

`POST /jobs/{jobId}/pause` pauses a Job. `POST /jobs/{jobId}/stop` stops a Job.

`POST /jobs/{jobId}/resume` resumes a paused Job. `POST /jobs/{jobId}/recover` applies canonical recovery.

`POST /jobs/{jobId}/resolve-hold` resolves a hold. `POST /jobs/{jobId}/retry` retries a failed Job through the canonical lifecycle.

`DELETE /jobs/{jobId}` deletes a Job when the canonical lifecycle permits deletion. It returns HTTP `204` after deletion and HTTP `404` when no Job exists.

`GET /jobs/{jobId}/progress` reads progress. `GET /jobs/{jobId}/attempts` reads attempts.

`GET /jobs/{jobId}/artifact` reads the Job artifact when one exists. Results, failures, recovery, and cancellation remain part of the canonical Job document.

Every Jobs operation uses the universal action dispatcher. Jobs do not require an Agent, Thread, Channel, Context, or Permission module.

## Modules And Providers

The Runtime loads module manifests from the `modules` directory beside the host executable. The graph validates each enabled manifest before it publishes readiness.

Provider modules register provider plugins through `IKernelBuilder`. The host does not contain provider-specific execution or a provider fallback.

A module can run in-process or through the published sidecar host contract. The host owns the singleton action dispatcher and module storage gateway.

An enabled module that fails validation blocks activation. The host does not silently remove the module or replace its behavior with local code.

Module-owned endpoints are dynamic. Their route and payload contracts belong to the owning module package.

## Environment Configuration

Use canonical dotenv keys in the Runtime `Environment/.env` file. Use `__` between configuration sections and keys.

Use these keys for the base host:

```dotenv
ASPNETCORE_URLS=http://127.0.0.1:48923
Auth__DisableApiKeyCheck=false
Encryption__EncryptDatabase=true
Encryption__EncryptProviderKeys=true
Database__Provider=JsonFile
Modules__sharpclaw_providers_openai_compat=true
```

`Encryption__EncryptDatabase` controls database record encryption. It does not control protection of the active environment document.

The active environment document uses the Supprocom.Secrets installation-key boundary. `SHARPCLAW_ENCRYPTION_KEY` is an explicit installation-key source when configured and valid.

`Database__Provider` selects the configured provider. The Runtime validates database readiness before it publishes discovery.

`Modules__<module-id>` enables or disables a module by its manifest identity. Invalid or incomplete enabled modules fail closed.

## Errors And Cancellation

The host maps public typed errors through one response boundary. General internal exception details are not returned to API callers.

Request cancellation returns HTTP `499` before response headers are sent. A failure after response start is rethrown so the server does not report a false successful response.

The API key, action, provider, Jobs, storage, and response boundaries all use the request cancellation token. A caller must not receive a late completion after cancellation.

## Ownership Boundary

SharpClaw.Core owns the neutral kernel, Jobs, Events, action contracts, and event contracts. SharpClaw.Contracts owns the neutral interfaces.

The Runtime Host owns composition, API mapping, provider binding, module loading, readiness, and the module storage gateway.

Agent Orchestration owns Agents, Skills, Memory, Context, Threads, Channels, history, and Two Tier Permission behavior when its published modules are enabled.

The base Runtime does not declare feature-specific database entities for those domains. It does not keep inactive feature routes, UI, or compatibility stores.
