<![CDATA[# Getting Started

This guide describes the default SharpClaw installation.

## Configure The Runtime

On first launch, the bundled Runtime starts without a provider or credentials and the client opens Settings. Choose a provider supplied by an enabled module, enter its model and any required endpoint/key, then save. SharpClaw writes the protected backend configuration and restarts its owned Runtime; it does not silently select a provider. An external Runtime must be configured on its own host.

## Select A Model

Use the exact model identifier exposed by the selected provider. A local keyless provider still needs its real inference server and model. Open Settings to check Runtime readiness or change the provider later; credentials are never displayed there.

## Send A Message

Open Chat, enter a message, and send it. The Runtime returns the model response.

## Use Jobs

A tool or an API request can submit a Job when work continues after the current request. The kernel stores Job state through one lifecycle.

## Use The Gateway

The Gateway is optional. Start it only when another process needs the public API boundary. Local Runtime and client use remain available when the Gateway is disabled.

## Next Step

Use Troubleshooting when a Runtime, provider, Job, or Gateway operation fails.
]]>
