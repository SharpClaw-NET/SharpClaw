# SharpClaw

SharpClaw is a hypermoddable and hypertunable LLM kernel for .NET. It provides model streaming, tools, Jobs, Events, module lifecycle, authenticated transport, logging, readiness checks, and durable storage. The default JSONColdStore database creates itself and needs no external database service. PostgreSQL, SQL Server, and SQLite are also supported when an installation needs a relational database. SharpClaw supplies a coherent operational foundation while neutral module contracts let you build the product behavior that you need.

## Kernel By Default

A default installation lets you select a provider and model, then send one independent message to that model. The kernel does not create hidden agents, permissions, channels, threads, memory, or conversation history. It owns provider and model selection, streaming, tool execution, canonical Jobs and Events, module composition, and the universal action graph. Optional behavior exists only when its owning module is enabled.

## Modules And Capabilities

Modules extend one compiled graph through typed contracts, declared capabilities, and authenticated host transport. A module can add a narrow capability without replacing the kernel or creating a parallel execution path. The base application remains useful on its own, while an installation can add complete feature domains as required.

| Module type | Capability when enabled |
| --- | --- |
| Provider | Adds provider transport, credentials, model discovery, and model invocation. |
| Tool | Adds model-visible operations that complete directly or submit canonical Jobs. |
| Context | Adds threads, channels, conversation history, and context assembly. |
| Two Tier Permission | Adds role clearance, channel and context preauthorization, grants, denials, and approvals. |
| Agents | Adds agents, skills, memory, and typed agent workflows that use canonical Jobs. |
| Application | Adds declared CLI commands and authenticated HTTP or WebSocket endpoints. |
| Integration | Connects external editors, services, observability systems, or other product surfaces. |

## Bring Your Own Features

SharpClaw modules use public neutral contracts instead of host-specific branches. A module declares what it supplies and what it requires. The host validates that graph before startup, gives the module only its approved capabilities, and keeps all work on shared kernel paths.

| Extension surface | What you can supply |
| --- | --- |
| Services and contracts | Typed services that other declared modules can use. |
| Providers and tools | Model backends, model-visible tools, and typed handlers. |
| Actions, hooks, and events | Interception, policy, tuning, observation, and event handling. |
| Jobs | Typed durable work through the kernel scheduler, recovery, and result lifecycle. |
| Storage | Host-managed module documents, indexes, transactions, and module-owned EF Core contexts. |
| Chat context | Bounded prompt context from an enabled context contributor. |
| Application surfaces | CLI commands and authenticated HTTP or WebSocket endpoints. |

## Storage

SharpClaw uses one configured persistence path for kernel and module storage. `JsonFile` is the default and uses JSONColdStore. A new local installation can create durable storage without a database server or setup step. `Postgres`, `SqlServer`, and `SQLite` use the same Entity Framework Core boundary for deployments that need those providers. The Runtime validates storage before it publishes readiness and never falls back after a configured provider fails.

## Getting Started

The [SharpClaw releases](https://github.com/SharpClaw-NET/SharpClaw/releases) page provides packaged builds. A source build uses the .NET SDK version in `global.json`. Configure one enabled provider and model in the Runtime environment, then use **Chat** for model requests. **Settings** manages the Runtime endpoint and optional Gateway process. The Agent Orchestration modules add history, context, permissions, agents, skills, and memory.

```powershell
dotnet restore SharpClaw.slnx
dotnet build SharpClaw.slnx -c Release --no-restore
```

## Documentation

The [kernel architecture specification](docs/SharpClaw-Kernel-Architecture-Specification.md) defines product ownership and module boundaries. [Database configuration](docs/Database-Configuration.md) describes each supported storage provider. The [Core API](docs/Core-API-documentation.md), [Core CLI](docs/Core-CLI-documentation.md), [Gateway](docs/Gateway-documentation.md), [logging](docs/Logging.md), and [provider parameters](docs/Provider-Parameters.md) documents describe the main operating surfaces.

## License And Security

SharpClaw uses the [GNU Affero General Public License version 3 or later](LICENSE.md), with the exceptions stated in the license file. Report a security issue through [GitHub Security Advisories](https://github.com/SharpClaw-NET/SharpClaw/security/advisories/new), not through a public issue.
