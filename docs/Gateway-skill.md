# Gateway Integration Skill

## Scope

Use this skill when you configure, test, or operate the optional SharpClaw Gateway. The Gateway is a separate public process. It forwards requests to the local Runtime Host.

The skill does not create application state. The Runtime owns provider and model selection, canonical Jobs, module storage, and kernel execution. The Gateway owns transport controls, forwarding, process state, and module endpoint-group hosting.

## Route Contract

The current base route contract is:

```text
GET  /api/health
GET  /api/gateway/status
ANY  /api/chat
```

The Gateway forwards `/api/chat` to `/chat`. It keeps the request method, query string, body, response status, content type, and response body. The Gateway does not cache a model response and does not run a local request after a forwarding failure.

Gateway module extensions can register endpoint groups below `/api/modules/`. The catalog requires a known module and group identity. The endpoint gate requires the group to be enabled. Unknown and disabled groups fail before module execution.

## Configuration Contract

Use the assembly-local `Environment/.env` file. Use double underscores for nested dotenv keys.

```text
InternalApi__BaseUrl="http://127.0.0.1:48923"
InternalApi__TimeoutSeconds="300"
InternalApi__ApiKey=""
Gateway__Endpoints__Enabled="true"
Gateway__RequestQueue__Enabled="true"
Gateway__RequestQueue__MaxConcurrency="1"
Gateway__RequestQueue__TimeoutSeconds="30"
Gateway__RequestQueue__MaxRetries="2"
Gateway__RequestQueue__RetryDelayMs="500"
Gateway__RequestQueue__MaxQueueSize="500"
Gateway__Modules__HotReloadEnabled="false"
Gateway__Modules__DrainTimeoutSeconds="30"
```

Use `Gateway:Endpoints:Enabled` only as the normalized IConfiguration key. Use `Gateway__Endpoints__Enabled=true` in the dotenv file. Do not use JSON or JSONC application configuration files.

## Security Boundary

The Gateway service credential is used only for the local Gateway-to-Runtime request. The Gateway does not place that credential in a forwarded request to a remote proxy. The request body cannot choose a credential mode or a forwarding target.

The transport pipeline applies the master gate, module group gate, IP ban check, body validation, rate limiting, and action handling before forwarding. A disabled or invalid target fails closed. A forwarding failure cannot start local execution.

## Validation Flow

First verify `/api/health`. Next verify `/api/gateway/status`. Then send a bounded request to `/api/chat` with a configured provider and model. Finally verify the response status, content type, body, and request cancellation behavior.

For a module endpoint, verify discovery, group identity, enabled state, authorization, response status, and shutdown. Verify that an unknown group returns 404 and a disabled group returns 503. Verify that module failure does not create a Runtime or storage fallback.

## Diagnostics

Check Gateway process output for listener, queue, rate-limit, and forwarding errors. Check Runtime process output for provider, model, module, and kernel errors. Keep request bodies, authorization values, API keys, cookies, and query values out of logs.
