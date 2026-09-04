# SharpClaw Gateway

## Purpose

The Gateway is an optional public process. It forwards approved requests to the local Runtime Host. Local Runtime use does not require the Gateway.

The Gateway does not own application persistence. It does not create a second kernel execution path. The Runtime remains the owner of provider selection, model selection, canonical Jobs, and module storage.

## Active Base Routes

The Gateway maps these base routes:

```text
GET  /api/health
GET  /api/gateway/status
ANY  /api/chat
```

`GET /api/health` returns the Gateway health response. `GET /api/gateway/status` returns the configured Runtime target and ready status. `/api/chat` forwards the request to the Runtime `/chat` route and copies the response status, content type, and body.

Gateway modules can add endpoint groups under `/api/modules/{SourceId}/{groupId}`. The module loader discovers only valid Gateway module extensions. The endpoint catalog rejects unknown groups and disabled groups before the request reaches module code. A module endpoint does not create a second Runtime or storage path.

## Configuration

Gateway configuration uses the assembly-local `Environment/.env` file. Use canonical dotenv keys with double underscores.

```text
InternalApi__BaseUrl="http://127.0.0.1:48923"
InternalApi__TimeoutSeconds="300"
InternalApi__ApiKey=""
InternalApi__ApiKeyFilePath=""
Gateway__Endpoints__Enabled="true"
Gateway__RequestQueue__Enabled="true"
Gateway__RequestQueue__MaxConcurrency="1"
Gateway__RequestQueue__TimeoutSeconds="30"
Gateway__Modules__HotReloadEnabled="false"
Gateway__Modules__DrainTimeoutSeconds="30"
```

The `Gateway__Endpoints__Enabled` switch controls the complete Gateway listener. Module group switches remain in the Gateway module configuration. A disabled Gateway returns a stable unavailable response before forwarding.

Do not store application environment settings as JSON or JSONC text. Do not use colon notation in a dotenv assignment. Use `Gateway__Endpoints__Enabled=true` in the file and `Gateway:Endpoints:Enabled` only when describing an IConfiguration key.

## Request Flow

The process assigns request metadata before the middleware pipeline runs. Health probes can short-circuit. The master endpoint gate then rejects a disabled Gateway and checks module endpoint groups. IP bans, body validation, rate limits, and Gateway action handling run before forwarding.

The internal client sends the request to the configured Runtime target. It adds the Gateway service credential only on the Gateway-to-Runtime hop. The Gateway does not expose that credential to the caller or a module endpoint. A failed remote target returns an explicit failure. The Gateway never executes the request locally after a forwarding failure.

## Process Operation

Start the Runtime Host before the Gateway when local process management does not start both processes. The Gateway readiness check uses the Runtime health route. A failed readiness check does not produce a local fallback.

The Gateway can use its request queue for bounded forwarding. Queue settings control concurrency, timeout, retry count, retry delay, and queue size. Queue state is process state. It is not application Job state and it is not durable application storage.

## Troubleshooting

If `/api/health` fails, inspect the Gateway process and listener. If `/api/chat` fails, check `InternalApi__BaseUrl`, the Runtime listener, and the protected API key. If a module endpoint returns unavailable, check the module configuration and endpoint group identity.

A configured remote target must fail closed when it is invalid or unreachable. Do not remove the remote configuration to force local Runtime startup. Use the Runtime process for application diagnostics and the Gateway process for forwarding diagnostics.
