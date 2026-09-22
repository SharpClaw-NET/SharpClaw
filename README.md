# SharpClaw

SharpClaw is a modular LLM runtime for .NET. Its deliberately small kernel handles model streaming, tools, canonical Jobs and Events, authenticated transport, module composition, logging, readiness, and durable storage. Product-specific behavior lives in optional modules, so an installation can start simple and grow without forking the kernel.

## Architecture At A Glance

SharpClaw compiles the host and every enabled module into one typed graph before it reports readiness. Clients call the Runtime directly or through the optional authenticated Gateway. The Runtime composes SharpClaw.Core with provider integrations, tools, Jobs, Events, and one configured persistence provider, while modules contribute only the services and capabilities they declare.

```mermaid
flowchart LR
    Client --> Runtime
    Gateway -. optional authenticated proxy .-> Runtime
    Modules[Enabled modules] -->|typed registrations| Runtime
    Runtime --> Core[SharpClaw.Core]
    Core --> Providers
    Core --> Tools
    Core --> Jobs
    Core --> Events
    Runtime --> Storage
```

## Bring Your Own Features

A SharpClaw module uses public neutral contracts to declare what it supplies and requires. Before readiness, the host validates those relationships and grants only approved capabilities, so independently replaceable features still run through the same kernel paths.

| Area | Bring your own | What it enables |
| --- | --- | --- |
| **Kernel extensions** | Model providers and tools | Model backends, provider selection, model-visible tools, and typed handlers. |
|  | Actions, hooks, and events | Typed operations, interception, policy, tuning, observation, and event handling. |
|  | Durable work | Actions and tools that use canonical Jobs, progress, recovery, cancellation, and results. |
| **Authorization and permissions** | Authorization policy | One active `IAuthorizationPolicy` provider that makes allow-or-deny decisions. |
|  | Authorization consumers | Protected features that call the active policy through `RequireAuthorization`. |
|  | Authorization restrictions | Composable rules that may preserve or deny a decision, but can never grant authority. |
|  | Permission systems | Users, sessions, roles, clearances, resource grants, approvals, tenant rules, and their APIs or CLI commands. |
| **Feature systems** | Conversations and context | Threads, channels, history, conversation identity, chat profiles, and bounded prompt context. |
|  | Agents and knowledge | Agents, skills, memory, delegation, planning, and multi-step workflows. |
|  | Integrations and operations | Editor bridges, external services, metrics, logging integrations, and observability surfaces. |
| **Application and data** | Shared services | Typed exported and required contracts between declared modules. |
|  | Application surfaces | CLI commands and authenticated HTTP or WebSocket endpoints. |
|  | Storage | Host-managed documents, indexes, claims, and transactions, or module-owned EF Core contexts. |

## Bring Your Own Keys

Provider credentials and AI integrations belong to modules, not the kernel. Select an enabled provider with `Provider__Key` and supply its credential with `Providers__<provider-key>__ApiKey`, or use `Provider__ApiKey` as the single-provider fallback; local providers require no key. Any module can add another chat or model provider by implementing the public provider contract, without adding a kernel allowlist. AI types outside the kernel's chat contract—including speech transcription, speaker diarization, image generation, video generation, embeddings, reranking, and future modalities—can instead ship as specialized module-owned pipelines that contribute their own typed actions, tools, Jobs and Events, endpoints, and configuration to the same compiled graph.

| Module package | Providers and selectable keys | Credential |
| --- | --- | --- |
| `SharpClaw.Modules.Providers.OpenAICompatible` | OpenAI (`openai`), DeepSeek (`deepseek`), OpenRouter (`openrouter`), Eden AI (`eden-ai`), Google Gemini OpenAI shim (`google-gemini-openai`), Google Vertex AI OpenAI shim (`google-vertex-ai-openai`), Z.AI (`zai`), Vercel AI Gateway (`vercel-ai-gateway`), xAI (`xai`), Groq (`groq`), Cerebras (`cerebras`), Mistral (`mistral`), GitHub Copilot (`github-copilot`), MiniMax (`minimax`), and a custom OpenAI-compatible endpoint (`custom`) | API key or token; GitHub Copilot supports device-code authorization, and `custom` also requires its endpoint. |
| `SharpClaw.Modules.Providers.Anthropic` | Anthropic (`anthropic`) | API key. |
| `SharpClaw.Modules.Providers.Google` | Google Gemini (`google-gemini`) and Google Vertex AI (`google-vertex-ai`) | Gemini API key or Vertex AI OAuth access token. |
| `SharpClaw.Modules.Providers.Ollama` | Ollama (`ollama`) | None. |
| `SharpClaw.Modules.Providers.LlamaSharp` | Local GGUF inference (`llamasharp`) | None. |

## Bring Your Own Storage

Storage is completely modular: install one of the packages below or any compatible third-party persistence module and select its provider key. The Runtime discovers every storage provider through the same module loader, validates it before readiness, and has no provider allowlist or silent fallback; each module owns its provider configuration and, where applicable, its migrations.

| Module package | Provider key | Deployment notes |
| --- | --- | --- |
| `SharpClaw.Persistence.JSONColdStore` | `JSONColdStore` | Default local option; creates durable storage without a database server or migrations. The former `JsonFile` key remains an alias. |
| `SharpClaw.Persistence.PostgreSQL` | `PostgreSQL` | Set `ConnectionStrings__PostgreSQL` and apply the migrations owned by this module. The former `Postgres` key remains an alias. |
| `SharpClaw.Persistence.SQLServer` | `SQLServer` | For SQL Server, set `ConnectionStrings__SQLServer` and apply the migrations owned by this module. The former `SqlServer` key remains an alias. |
| `SharpClaw.Persistence.SQLite` | `SQLite` | Set `ConnectionStrings__SQLite` and apply the migrations owned by this module. |

## Getting Started

Download a packaged build from [SharpClaw releases](https://github.com/SharpClaw-NET/SharpClaw/releases), or build the repository with the .NET SDK selected by `global.json` using the commands below. Configure one enabled provider and model in the Runtime environment, use **Chat** for model requests, and use **Settings** to manage the Runtime endpoint and optional Gateway process; install optional packages only for the conversation state, authorization, agent workflows, or integrations your application needs.

```powershell
dotnet restore SharpClaw.slnx
dotnet build SharpClaw.slnx -c Release --no-restore
```

## Documentation

Start with the [kernel architecture specification](docs/SharpClaw-Kernel-Architecture-Specification.md) for product ownership and module boundaries, then use [database configuration](docs/Database-Configuration.md), the [Core API](docs/Core-API-documentation.md), [Core CLI](docs/Core-CLI-documentation.md), [Gateway](docs/Gateway-documentation.md), [logging](docs/Logging.md), and [provider parameters](docs/Provider-Parameters.md) for the corresponding operating surfaces.

## License And Security

SharpClaw is licensed under the [GNU Affero General Public License version 3 or later](LICENSE.md), subject to the exceptions stated in the license file. Report vulnerabilities privately through [GitHub Security Advisories](https://github.com/SharpClaw-NET/SharpClaw/security/advisories/new) rather than opening a public issue.
