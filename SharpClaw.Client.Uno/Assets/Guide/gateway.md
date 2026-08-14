<![CDATA[# Gateway

The Gateway is an optional public API process. It exposes a separate listener and forwards approved requests to the local Runtime.

## Local Runtime And Gateway

Local client use connects to the loopback Runtime. It does not require the Gateway. A remote client uses the Gateway listener and its declared authentication boundary.

The Gateway does not create a second Jobs store or execution path. The authoritative Runtime remains the application owner for kernel state.

## Process Lifecycle

The Uno client can start the Gateway when its environment enables Gateway launch. It can also connect to an externally started Gateway.

When Gateway launch is disabled, the client does not start a Gateway process. Local Runtime use remains available.

When the client stops a Gateway process that it owns, the process stops. An external Gateway remains under its own process owner.

## Gateway Environment

Edit the client environment through the canonical dotenv editor. Use `__` for nested keys in the file, such as `Gateway__Enabled=true`.

The Gateway and Runtime use their own assembly-local `Environment/.env` files. Do not replace an application dotenv file with JSON or JSONC text.

## Troubleshooting

If the Gateway does not respond, check its process output, loopback listener, and protected environment values. Check that the Runtime loopback address is reachable.

If the Gateway starts but cannot forward a request, check its API key and target configuration. A failed remote target must fail closed. The Gateway must not fall back to local execution.
]]>
