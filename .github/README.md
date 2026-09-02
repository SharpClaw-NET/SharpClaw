# SharpClaw

SharpClaw is a hypermoddable and hypertunable LLM kernel for .NET. Its small kernel uses neutral contracts and one compiled module graph.

## Default Runtime

A user selects a provider and model. Each request sends one message and receives one model response.

The default path is stateless. It stores no conversation history, thread, channel, context, agent, permission, skill, or memory data.

The kernel owns provider and model selection, streaming, tools, canonical Jobs, canonical Events, module loading, and the universal action and event path.

Jobs support durable work that does not finish in the current request. Events give the kernel and modules one declared event substrate.

## Modules

Provider modules supply model transports and provider-specific behavior. Tool modules supply model-visible tools and their handlers.

The optional Context module supplies threads, channels, context assembly, and conversation history. Without this module, requests remain independent.

The optional Two Tier Permission module supplies role, clearance, preauthorization, grant, denial, and approval behavior. The kernel remains permission-neutral.

The optional Agents module supplies agents, skills, and memory. It uses canonical Jobs instead of a second scheduler.

Editor, Metrics, and Module Development packages supply other optional capabilities. A disabled module contributes no behavior or storage.

Every out-of-process module uses authenticated transport for host actions and storage. The host does not compile against optional module implementations.

## Runtime Components

`SharpClaw.Runtime.Host` provides the local runtime API and module host. It owns the neutral composition, persistence gateway, and action dispatcher boundaries.

`SharpClaw.Gateway` provides an optional public proxy. It loads enabled modules only after neutral metadata validation.

`SharpClaw.Client.Uno` provides the desktop client. The client uses the runtime API and does not own optional module domains.

The distribution selects module package payloads through a generic manifest and payload root. Runtime Host and Gateway have no optional module package references.

## Get SharpClaw

Use the [SharpClaw releases](https://github.com/SharpClaw-NET/SharpClaw/releases) for packaged Runtime, Server, and Application builds.

Developers can build the Runtime Host from source:

```bash
dotnet build SharpClaw.Runtime/Host/SharpClaw.Runtime.Host.csproj
dotnet run --project SharpClaw.Runtime/Host/SharpClaw.Runtime.Host.csproj
```

Module authors can start with the [Module Creation Guide](../docs/guides/Module-Creation-Guide.md) and the [Module Enablement Guide](../docs/modules/Module-Enablement-Guide.md).

## Documentation

The [Kernel Architecture Specification](../docs/SharpClaw-Kernel-Architecture-Specification.md) defines the product boundary.

Use the [Core API Reference](../docs/Core-API-documentation.md), [Gateway API Reference](../docs/Gateway-documentation.md), and [Core CLI Reference](../docs/Core-CLI-documentation.md) for integration details.

Provider settings are in [Provider Parameters](../docs/Provider-Parameters.md). Runtime log locations are in [Logging](../docs/Logging.md).

## License And Security

SharpClaw uses the [GNU Affero General Public License v3.0](../LICENSE.md). Package metadata identifies any component with a different license.

Report security issues through [GitHub Private Vulnerability Reporting](https://github.com/SharpClaw-NET/SharpClaw/security/advisories/new).
