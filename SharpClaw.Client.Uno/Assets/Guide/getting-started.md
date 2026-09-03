<![CDATA[# Getting Started

This guide describes the default stateless SharpClaw installation.

## Select A Provider

Open the provider settings and select an enabled provider. Enter the provider secret through the environment editor or the provider settings that the installed provider module supplies.

## Select A Model

Open the model settings and select a model exposed by the selected provider. The Runtime uses that provider and model for the next message.

## Send A Message

Open the chat view, enter one message, and send it. The Runtime returns one reply. The default installation does not load prior messages or save conversation history.

## Use Jobs

A tool or a direct request can submit a Job when work continues after the current request. The kernel stores Job state and exposes progress, completion, failure, cancellation, and recovery through one path.

## Use The Gateway

The Gateway is optional. Start it only when another process needs the public API boundary. Local Runtime and client use remain available when the Gateway is disabled.

## Next Step

Use Troubleshooting when a Runtime, provider, Job, or Gateway operation fails.
]]>
