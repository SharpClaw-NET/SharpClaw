# SharpClaw

> **⚠️ Alpha.** Single developer, early stage, lots of untested surface area. [Read the full disclaimer.](DISCLAIMER.md)

> **📢 Developer notice.** The developer of SharpClaw has been hired to work on an undisclosed commercial enterprise tool that is inspired by this project. Due to time constraints and contractual limitations that cannot be disclosed, future development on SharpClaw will be limited. **The project is not abandoned.** SharpClaw remains open-source under [AGPL-3.0](LICENSE.md), and that will not change regardless of what happens with the commercial version. Contributions, issues, and community involvement are still welcome.

A local AI agent platform built on .NET 10.

## What it does

SharpClaw gives AI agents structured tools instead of a terminal. Every tool call is schema-validated, permission-checked, logged, and auditable. Agents can be paused, approved, denied, or cancelled mid-operation. The system supports multi-agent coordination where each agent sees only the tools and resources its role allows.

The agent can plug directly into your IDE (Visual Studio 2026, VS Code) over a live WebSocket bridge. It reads your code, makes edits through the editor's own APIs, triggers builds, reads compiler output, navigates symbols. It writes code the way you do, in the environment you use, and it checks that the code compiles before it moves on.

## What this looks like

**An agent that writes its own tools.** You need your agent to talk to a piece of software it has never seen before. The agent opens Visual Studio through the EditorBridge, writes a new module implementing `ISharpClawModule`, declares the tool schemas and permission descriptors, builds the project, reads compiler output, fixes errors, registers the module, and starts using the new tools. All in one session. No restart. The module system supports runtime enable/disable, so new capabilities come online the moment they compile. The permission system ensures the agent can only do this if its role explicitly allows it.

**Multi-agent pipelines with real trust boundaries.** A research agent has web access but no shell. A coding agent has editor access and the safe shell but no web. A review agent can approve or deny the other agents' actions but cannot execute anything itself. Each agent sees only the tools its role permits (via Tool Awareness Sets), so it cannot even attempt what it is not allowed to do. This is not configuration. It is the permission model and module system working together.

**IDE integration as a development loop, not a parlor trick.** The agent navigates to a symbol definition, reads surrounding context, makes a targeted edit, triggers a build, reads the compiler diagnostic, fixes the issue, runs the tests, and iterates. Same loop a human developer uses. Same IDE. Same undo stack.

**Every action is a record.** Every tool call produces a job entry with the action key, parameters, result, timestamps, and the full permission resolution chain. You can query what any agent did, when, why, and who approved it.

## Core ideas

- **No built-in tools.** Every tool is owned by a [module](#modules). The host is just a pipeline.
- **Two-stage permissions.** Agent capability check, then channel/context pre-authorization. Per-resource grants, clearance whitelists, approval workflows.
- **No shell interpreter.** `mk8.shell` is a closed verb set compiled into .NET API calls. Arguments are structured arrays. No string concatenation, no injection surface.
- **Typed tool lifecycle.** Queued, Executing, AwaitingApproval, Completed, Failed, Denied, Cancelled, Paused. Every state transition is logged.
- **Task orchestration.** Compiled step trees with conditionals, loops, error handling, and streaming. Not "improvise from conversation history."
- **IDE integration.** EditorBridge connects to VS 2026 and VS Code. Read, edit, build, navigate, all through the editor's own APIs.
- **13+ LLM providers.** OpenAI, Anthropic, Google, Groq, Mistral, and more. Encrypted key storage. Automatic capability inference. Responses API routing for OpenAI.

## Architecture

```
Contracts -> Utils -> Infrastructure -> Core -> API
                                                 |
                                            Gateway (optional, public-facing reverse proxy)
                                                 |
                              Uno Client / CLI / IDE Extensions / Bot Integrations
```

**Modules** own all tools. **Core** owns the pipeline (chat, jobs, permissions). **Infrastructure** owns persistence (EF Core, JSON sync). **API** exposes HTTP endpoints. **Gateway** adds rate limiting and endpoint toggles for public access.

## Bundled modules and 1.0.0

Not all modules currently bundled with SharpClaw will ship in 1.0.0. The essential modules (Module Dev Kit, editor tools, context tools, agent orchestration) are permanent. The remaining modules — ComputerUse, Transcription, OfficeApps, BotIntegration, and others — exist in the alpha to stress-test support for advanced features like hardware capture, COM interop, and third-party service integration. They will be gradually phased out of the default distribution as the platform matures.

Users are expected to create and share their own modules. The module system is designed to make this straightforward: implement `ISharpClawModule`, declare your tools, and build. An agent with the right permissions can even develop, compile, and register a new module from within SharpClaw itself, in a single session, without a restart.

## Modules

Every tool the LLM can call belongs to a module. Modules are self-contained C# projects that declare tool schemas, permission descriptors, and execution handlers. They can be enabled or disabled at runtime without restart.

| Module | Prefix | What it does |
|---|---|---|
| ComputerUse | `cu` | Window enumeration, app launch, focus, capture, desktop click/type |
| Mk8Shell | `mk8` | Safe shell with closed verb set, structured args, binary allowlist |
| DangerousShell | `ds` | Unrestricted shell. Separate module, separate permissions |
| AgentOrchestration | `ao` | Sub-agent creation, cross-agent coordination, approval chains |
| ContextTools | `ctx` | Thread navigation, history access, inline wait |
| Transcription | `tx` | Live audio via Whisper, Groq, or local models |
| WebAccess | `wa` | Search engines, localhost access, website interaction |
| OfficeApps | `oa` | Excel COM interop (Windows) |
| DatabaseAccess | `dba` | Structured database operations |
| VS2026Editor | `vs` | Visual Studio 2026 integration via WebSocket |
| VSCodeEditor | `vsc` | VS Code integration via WebSocket |
| BotIntegration | `bot` | Telegram, Discord, WhatsApp, and more |

Writing your own module means implementing `ISharpClawModule`, declaring your tools, and dropping the assembly in. The system handles discovery, registration, prefix validation, and permission integration.

## Permissions

Not a config toggle. The pipeline enforces this on every tool call.

**Clearance levels:** Independent, ApprovedByWhitelistedUser, ApprovedByWhitelistedAgent, PendingApproval, Denied.

**Resolution order:** Channel -> Context -> Agent Role. Each level can grant, restrict, or require approval. Per-resource grants mean Agent A can access Container 1 but not Container 2. The wildcard grant is immutable to prevent escalation.

Tool Awareness Sets control which tools each agent can even see in the prompt, reducing token cost and preventing capability leakage.

## mk8.shell

The agent's shell access. Not bash.

- Closed verb set. If it's not in `Mk8ShellVerb`, it can't run.
- Structured arguments via `string[]`. Never interpolated into a command string.
- No shell process. `ProcessStartInfo.UseShellExecute = false` + `ArgumentList.Add()`.
- Binary allowlist. Write-blocked paths. Git branch protection. Env variable allowlist.
- Workspace-scoped paths. Agents can't escape the sandbox.

Only `ProcRun` spawns a process. Everything else (file I/O, HTTP, text manipulation, JSON, archives, networking) runs in-memory via .NET APIs.

See [mk8.shell Reference](mk8.shell/mk8.shell.md) for the full verb list and execution model.

## Getting started

1. Build and run the API project:
   ```
   dotnet build SharpClaw.Application.API/SharpClaw.Application.API.csproj
   dotnet run --project SharpClaw.Application.API/SharpClaw.Application.API.csproj
   ```
2. Launch the Uno client (or use the CLI).
3. Create an admin account on first setup.
4. Add a provider (OpenAI, Anthropic, etc.) and set an API key.
5. Sync models, create an agent, open a channel, and start chatting.

The Uno client includes a built-in user guide covering setup, providers, agents, channels, permissions, jobs, tasks, and bot integrations.

## CLI

```
sharpclaw help
```

The CLI covers everything the API does: providers, models, agents, channels, threads, chat, jobs, tasks, permissions, roles, resources, modules, and more. Useful for scripting, headless operation, and quick diagnostics.

## Project layout

| Project | Role |
|---|---|
| `SharpClaw.Contracts` | Shared DTOs, enums, interfaces |
| `SharpClaw.Utils` | Common utilities |
| `SharpClaw.Application.Infrastructure` | EF Core persistence, entity models |
| `SharpClaw.Application.Infrastructure.Tasks` | Task compilation and step definitions |
| `SharpClaw.Application.Core` | Services, chat, jobs, permissions, providers, clients |
| `SharpClaw.Application.API` | HTTP endpoints, middleware, startup |
| `SharpClaw.Gateway` | Public-facing reverse proxy with rate limiting |
| `SharpClaw.Uno` | Cross-platform UI (Skia renderer, Uno Platform) |
| `mk8.shell` | Restricted agent command language |
| `mk8.shell.startup` | Shell bootstrapping |
| `DefaultModules/*` | All built-in modules (see table above) |
| `SharpClaw.Tests` | NUnit + FluentAssertions |
| `SharpClaw.UITests` | UI automation tests |

## Documentation

| Document | What it covers |
|---|---|
| [Core API Reference](docs/Core-API-documentation.md) | Full HTTP API with request/response shapes |
| [Core API Skill Reference](docs/Core-API-skill.md) | Compact endpoint listing for agent consumption |
| [Gateway API Reference](docs/Gateway-documentation.md) | Public gateway endpoints, rate limiting, toggles |
| [Provider Parameters](docs/Provider-Parameters.md) | Per-provider completion parameter support and validation |
| [Module System Design](docs/Module-System-Design.md) | Module architecture, contracts, permission model, lifecycle |
| [mk8.shell Reference](mk8.shell/mk8.shell.md) | Verb set, execution model, hardening, sandboxing |
| [Security Policy](SECURITY.md) | Vulnerability reporting |
| [Contributing](CONTRIBUTING.md) | CLA, contribution guidelines |

## How it compares to OpenClaw

OpenClaw proved people want a local AI agent. It has 180K+ GitHub stars, 20+ messaging channel integrations, and a massive community. It deserves credit for opening the category.

But its architecture has a ceiling. The core tool model is `bash`. The LLM generates a shell command string, a shell interpreter runs it, stdout comes back. That's it. No parameter validation, no permission checks, no approval workflows, no structured audit trail. The agent runs with the host user's full privileges. Microsoft and Cisco have both published advisories telling enterprises to isolate or avoid it. The sandbox toggle has a known unfixed bug. ~20% of community skills were found to contain malicious code.

SharpClaw's answer is to not give the agent a shell in the first place.

| | SharpClaw | OpenClaw |
|---|---|---|
| Tool model | Typed modules with JSON Schema, permissions, job lifecycle | `bash` string in, stdout string out |
| Permissions | Two-stage, per-resource, cascading clearance, approval workflows | Host user privileges for everything |
| Shell | `mk8.shell`: closed verbs, structured args, no interpreter | `bash` with broken sandbox toggle |
| IDE integration | Live WebSocket to VS 2026 and VS Code | None (file I/O via shell) |
| Task system | Compiled plans, conditionals, streaming, pause/resume | Agent improvises from conversation |
| Containers | Persistent, mandatory, per-agent access grants | Ephemeral Docker exec (when toggle works) |
| Credential storage | AES-GCM encryption | Plaintext config files |
| Audit | Full job records for every tool call | stdout/stderr capture |
| Multi-agent | Typed orchestration, cross-agent approval, tool awareness sets | Sub-agent spawning, no coordination protocol |

An agent that can connect to your IDE, write a module, build it, verify it compiles, register it, and start using it in the same session is a different kind of thing than an agent that cats files and pipes to sed. SharpClaw is building the former.

## License

[GNU Affero General Public License v3.0](LICENSE.md)

## Security

Report vulnerabilities via [GitHub Private Vulnerability Reporting](https://github.com/mkn8rn/SharpClaw/security/advisories/new). Do not open public issues for security bugs.
