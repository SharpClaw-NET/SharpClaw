# Module Enablement Guide

How to enable, disable, and configure SharpClaw modules.

> **Creation guide:** [../guides/Module-Creation-Guide.md](../guides/Module-Creation-Guide.md)
> **Agent creation skill:** [../guides/Module-Creation-skill.md](../guides/Module-Creation-skill.md)

---

## How the Module System Works

SharpClaw modules are self-contained feature packages that extend the
platform with tools, REST endpoints, CLI commands, and resource types.
Each module implements `ISharpClawModule` and is discovered at startup.

### Lifecycle

1. **Discovery** — `ModuleLoader` discovers all bundled modules compiled
   into the solution.
2. **Configuration sync** — `ModuleService.SyncStateFromConfigAsync` reads
   the `Modules` section of the core `.env` and updates the database. Only
   modules with an explicit `"true"` value are enabled.
3. **Registration** — Enabled modules are registered with `ModuleRegistry`.
4. **Dependency resolution** — Modules are sorted into initialization
   order based on `ExportedContracts` and `RequiredContracts`. Missing
   non-optional dependencies prevent a module from loading.
5. **Initialization** — `InitializeAsync` is called in dependency order.
   If it throws, the module is disabled and its contracts are poisoned
   (dependents cascade-fail).
6. **Seed data** — `SeedDataAsync` runs on first install (when the
   `.seeded` marker file is absent).
7. **Endpoint mapping** — Each module's `MapEndpoints` registers REST
   routes.
8. **Runtime** — Modules can be enabled/disabled at runtime via CLI
   without restart.
9. **Shutdown** — `ShutdownAsync` is called during graceful application
   shutdown.

### Configuration

Module state is controlled by the `Modules` section of the core `.env`
file at `Infrastructure/Environment/.env`. The file uses JSON-with-comments
format.

```jsonc
"Modules": {
  "sharpclaw_providers_openai_compat": "true",
  "sharpclaw_providers_anthropic": "true",
  "sharpclaw_web_access": "true"
  // ...
}
```

**Rules:**

- `"true"` → enabled
- `"false"` or **missing key** → disabled
- `.dev.env` overrides `.env` in development mode
- `.modules.env` (if present) is loaded between `.env` and `.dev.env`

**Defaults policy:**

- The base `.env` enables only the **provider modules** so a clean install
  can talk to LLM and STT backends out of the box. Every feature module
  (computer use, editor bridges, web access, transcription, etc.) ships
  set to `"false"` and must be opted into per deployment.
- The development override `.dev.env` enables **every** bundled module so
  contributors get full coverage during local work.

### CLI Runtime Management

```
module list                    List all modules with status
module get <id>                Show module details
module enable <id>             Enable a module (no restart required)
module disable <id>            Disable a module (no restart required)
```

### Dependency Contracts

Some modules **export** named service contracts. Other modules may
**require** those contracts. If a required (non-optional) contract's
provider is not enabled, the dependent module fails to load.

Optional requirements degrade gracefully — the module loads but the
feature backed by that contract is unavailable.

---

## Quick Reference Table

Feature modules are disabled in the base `.env`; provider modules are enabled.
The development override (`.dev.env`) enables everything.

| Module | ID | Base default | Platform | Requires |
|--------|----|--------------|----------|----------|
| [Agent Orchestration](#agent-orchestration) | `sharpclaw_agent_orchestration` | ❌ Disabled | All | — |
| [Bot Integration](#bot-integration) | `sharpclaw_bot_integration` | ❌ Disabled | All | — |
| [Computer Use](#computer-use) | `sharpclaw_computer_use` | ❌ Disabled | Windows | — |
| [Dangerous Shell](#dangerous-shell) | `sharpclaw_dangerous_shell` | ❌ Disabled | Win / Linux / macOS | — |
| [Database Access](#database-access) | `sharpclaw_database_access` | ❌ Disabled | Win / Linux / macOS | — |
| [Editor Common](#editor-common) | `sharpclaw_editor_common` | ❌ Disabled | All | — |
| [HTTP](#http) | `sharpclaw_http` | ❌ Disabled | All | — |
| [Metrics](#metrics) | `sharpclaw_metrics` | ❌ Disabled | All | — |
| [mk8.shell](#mk8shell) | `sharpclaw_mk8shell` | ❌ Disabled | Win / Linux / macOS | — |
| [Module Dev Kit](#module-development-kit) | `sharpclaw_module_dev` | ❌ Disabled | All | Computer Use *(optional)* |
| [Office Apps](#office-apps) | `sharpclaw_office_apps` | ❌ Disabled | Win / Linux / macOS | — |
| [Providers — Anthropic](#providers--anthropic) | `sharpclaw_providers_anthropic` | ✅ Enabled | All | — |
| [Providers — Google](#providers--google) | `sharpclaw_providers_google` | ✅ Enabled | All | — |
| [Providers — LlamaSharp](#providers--llamasharp) | `sharpclaw_providers_llamasharp` | ✅ Enabled | All | — |
| [Providers — Ollama](#providers--ollama) | `sharpclaw_providers_ollama` | ✅ Enabled | All | — |
| [Providers — OpenAI-Compatible](#providers--openai-compatible) | `sharpclaw_providers_openai_compat` | ✅ Enabled | All | — |
| [Providers — Whisper (local)](#providers--whisper-local) | `sharpclaw_providers_whisper` | ✅ Enabled | All | — |
| [System Audio](#system-audio) | `sharpclaw_systemaudio` | ❌ Disabled | Windows | — |
| [Transcription](#transcription) | `sharpclaw_transcription` | ❌ Disabled | Windows | **System Audio** |
| [VS 2026 Editor](#vs-2026-editor) | `sharpclaw_vs2026_editor` | ❌ Disabled | Windows | **Editor Common** |
| [VS Code Editor](#vs-code-editor) | `sharpclaw_vscode_editor` | ❌ Disabled | All | **Editor Common** |
| [Web Access](#web-access) | `sharpclaw_web_access` | ❌ Disabled | All | — |

---

## Dependency Graph

```
┌──────────────────┐
│  Editor Common   │──exports──▶ editor_bridge, editor_session
└────────┬─────────┘
         │ required by
    ┌────┴────┐
    ▼         ▼
┌─────────┐ ┌───────────┐
│ VS 2026 │ │  VS Code  │
│ Editor  │ │  Editor   │
└─────────┘ └───────────┘

┌──────────────────┐
│  Computer Use    │──exports──▶ window_management, desktop_input
└────────┬─────────┘
         │ optional
         ▼
┌──────────────────┐
│   Module Dev     │  (falls back to Process.GetProcessesByName)
└──────────────────┘

┌──────────────────┐
│  System Audio    │──exports──▶ system_audio_capture
└────────┬─────────┘
         │ required by
         ▼
┌──────────────────┐
│  Transcription   │  (consumes IAudioCaptureProvider)
└──────────────────┘

┌──────────────────────────┐
│ Providers — Whisper      │──exports──▶ transcription_stt_local
└──────────┬───────────────┘
           │ optional consumer
           ▼
┌──────────────────┐
│  Transcription   │  (cloud STT always available; local backend appears
└──────────────────┘   in the factory only when this module is enabled)
```

Provider modules (`sharpclaw_providers_*`) are otherwise standalone — each
registers one or more `IProviderPlugin` instances and disappears from the
factory's catalogue when disabled.

---

## Task trigger ownership

The task system is extensible, so not every trigger kind is hosted by Core itself.
If you use task triggers, module state directly affects which trigger sources are
available on the current host.

| Trigger area | Owner |
|---|---|
| Cron, event bus, task-completed, task-failed, file changed, startup/shutdown, custom trigger contracts | Core / Agent Orchestration |
| Webhook, host reachable/unreachable, network changed | HTTP |
| Generic metric polling (`MetricThreshold`) | Metrics |
| Window focus/blur, hotkeys, idle/active, screen lock/unlock, device connected/disconnected, process started/stopped, OS shortcut launchers | Computer Use |
| Query returns rows | Database Access |

Practical effect:

- a task can still be saved when a module-owned trigger's module is disabled
- preflight reports a warning recommending the missing module
- `task trigger-sources` and `GET /tasks/trigger-sources` show the active sources for the
  current host
- enabling the owning module makes those sources available to the trigger host again
- the HTTP and Metrics modules are required for any task that uses webhook,
  host-reachability, network, or metric-threshold triggers

---

## Per-Module Enablement

### Agent Orchestration

| Setting | Value |
|---------|-------|
| **.env key** | `Modules:sharpclaw_agent_orchestration` |
| **Default** | ❌ Disabled |
| **Platform** | All |
| **Prerequisites** | None |
| **Exports** | None |

Sub-agent creation, agent management, task editing, skill access, and
custom chat header editing. All tools flow through the job pipeline.

```jsonc
// Infrastructure/Environment/.env → Modules section
"sharpclaw_agent_orchestration": "true"
```

---

### Bot Integration

| Setting | Value |
|---------|-------|
| **.env key** | `Modules:sharpclaw_bot_integration` |
| **Default** | ❌ Disabled |
| **Platform** | All |
| **Prerequisites** | None |
| **Exports** | None |

Outbound bot messaging to Telegram, Discord, WhatsApp, Slack, Matrix,
Signal, Email, and Teams. All senders use standard HTTP or SMTP.

```jsonc
"sharpclaw_bot_integration": "true"
```

---

### Computer Use

| Setting | Value |
|---------|-------|
| **.env key** | `Modules:sharpclaw_computer_use` |
| **Default** | ❌ Disabled |
| **Platform** | **Windows only** |
| **Prerequisites** | None |
| **Exports** | `window_management` (`IWindowManager`), `desktop_input` (`IDesktopInput`) |

Desktop awareness, window management, display capture, input simulation,
clipboard access, native application control. Exports contracts used
optionally by Module Dev.

```jsonc
"sharpclaw_computer_use": "true"
```

---

### Dangerous Shell

| Setting | Value |
|---------|-------|
| **.env key** | `Modules:sharpclaw_dangerous_shell` |
| **Default** | ❌ Disabled |
| **Platform** | Windows, Linux, macOS |
| **Prerequisites** | None |
| **Exports** | None |

Real (unsandboxed) shell execution — Bash, PowerShell, CommandPrompt,
Git. No sandbox restrictions; safety relies entirely on the permission
system's clearance requirements.

```jsonc
"sharpclaw_dangerous_shell": "true"
```

> ⚠️ **Security note:** This module bypasses all sandbox protections.
> Ensure agents using it have appropriate clearance levels configured.

---

### Database Access

| Setting | Value |
|---------|-------|
| **.env key** | `Modules:sharpclaw_database_access` |
| **Default** | ❌ Disabled |
| **Platform** | Windows, Linux, macOS |
| **Prerequisites** | None |
| **Exports** | None |

Register and query internal/external databases. Supports PostgreSQL,
MySQL, SQLite, and MSSQL with read-only safety by default.

```jsonc
"sharpclaw_database_access": "true"
```

---

### Editor Common

| Setting | Value |
|---------|-------|
| **.env key** | `Modules:sharpclaw_editor_common` |
| **Default** | ❌ Disabled |
| **Platform** | All |
| **Prerequisites** | None |
| **Exports** | `editor_bridge` (`EditorBridgeService`), `editor_session` (`EditorSessionService`) |

**Infrastructure module** — shared WebSocket editor bridge and session
management. No LLM-callable tools. Required by VS 2026 Editor and
VS Code Editor modules.

```jsonc
"sharpclaw_editor_common": "true"
```

> **Important:** If you disable this module, both VS 2026 Editor and
> VS Code Editor will fail to load (missing required contracts).

---

### HTTP

| Setting | Value |
|---------|-------|
| **.env key** | `Modules:sharpclaw_http` |
| **Default** | ❌ Disabled |
| **Platform** | All |
| **Prerequisites** | None |
| **Exports** | None |

Owns the task-script HTTP request step
(`HttpGet`/`HttpPost`/`HttpPut`/`HttpDelete` → `http_request`) and the
network-trigger family (webhook, host reachable/unreachable, network
changed). No LLM-callable tools — pure task-pipeline contributions.

```jsonc
"sharpclaw_http": "true"
```

> Required for any task that uses HTTP request steps or network/webhook
> triggers. See [Module-Http.md](Module-Http.md).

---

### Metrics

| Setting | Value |
|---------|-------|
| **.env key** | `Modules:sharpclaw_metrics` |
| **Default** | ❌ Disabled |
| **Platform** | All |
| **Prerequisites** | None |
| **Exports** | None |

Owns the `MetricThreshold` task trigger and the built-in
`ITaskMetricProvider` implementations (pending job count, pending task
count, scheduler pending job count). No LLM-callable tools.

```jsonc
"sharpclaw_metrics": "true"
```

> Required for any task that uses the metric-threshold trigger. See
> [Module-Metrics.md](Module-Metrics.md).

---

### mk8.shell

| Setting | Value |
|---------|-------|
| **.env key** | `Modules:sharpclaw_mk8shell` |
| **Default** | ❌ Disabled |
| **Platform** | Windows, Linux, macOS |
| **Prerequisites** | None |
| **Exports** | None |

Sandboxed mk8.shell script execution and sandbox container lifecycle
management. Uses the mk8.shell engine for safe, allowlisted operations.

```jsonc
"sharpclaw_mk8shell": "true"
```

---

### Module Development Kit

| Setting | Value |
|---------|-------|
| **.env key** | `Modules:sharpclaw_module_dev` |
| **Default** | ❌ **Disabled** (not listed in default `.env`) |
| **Platform** | All |
| **Prerequisites** | Computer Use *(optional — `window_management` contract)* |
| **Exports** | None |

Autonomous module authoring, building, hot-loading, and development
introspection tools. Lets agents scaffold, build, and iterate on new
SharpClaw modules, plus inspect live processes and the host dev
environment.

**To enable**, add to the `Modules` section of your core `.env`:

```jsonc
"sharpclaw_module_dev": "true"
```

If Computer Use is also enabled, process inspection gains
window-title → PID resolution. Without it, falls back to
`Process.GetProcessesByName`.

---

### Office Apps

| Setting | Value |
|---------|-------|
| **.env key** | `Modules:sharpclaw_office_apps` |
| **Default** | ❌ Disabled |
| **Platform** | Windows, Linux, macOS |
| **Prerequisites** | None |
| **Exports** | None |

Document session management, file-based spreadsheet operations
(ClosedXML / CsvHelper), and live Excel COM Interop (Windows only).

```jsonc
"sharpclaw_office_apps": "true"
```

---

### Providers — Anthropic

| Setting | Value |
|---------|-------|
| **.env key** | `Modules:sharpclaw_providers_anthropic` |
| **Default** | ✅ Enabled |
| **Platform** | All |
| **Prerequisites** | None |
| **Exports** | None |

Registers the native Anthropic `IProviderPlugin` (Anthropic
`/v1/messages` wire format, not OpenAI-compatible).

```jsonc
"sharpclaw_providers_anthropic": "true"
```

---

### Providers — Google

| Setting | Value |
|---------|-------|
| **.env key** | `Modules:sharpclaw_providers_google` |
| **Default** | ✅ Enabled |
| **Platform** | All |
| **Prerequisites** | None |
| **Exports** | None |

Registers the native Google plugins (Gemini and Vertex AI), using
Google's `generateContent` wire format. The OpenAI-compatible Gemini
/ Vertex AI shims are owned by the OpenAI-Compatible providers module.

```jsonc
"sharpclaw_providers_google": "true"
```

---

### Providers — LlamaSharp

| Setting | Value |
|---------|-------|
| **.env key** | `Modules:sharpclaw_providers_llamasharp` |
| **Default** | ✅ Enabled |
| **Platform** | All (CPU fallback when no GPU) |
| **Prerequisites** | None |
| **Exports** | None |

In-process llama.cpp inference via LlamaSharp. Owns the
`LocalModelFile` entity and exposes `/models/local` REST and
`localmodel` CLI surfaces. Configured by the `Local:*` keys in
`.env`.

```jsonc
"sharpclaw_providers_llamasharp": "true"
```

---

### Providers — Ollama

| Setting | Value |
|---------|-------|
| **.env key** | `Modules:sharpclaw_providers_ollama` |
| **Default** | ✅ Enabled |
| **Platform** | All |
| **Prerequisites** | None (user-managed Ollama server) |
| **Exports** | None |

Thin OpenAI-compatible client targeting a user-managed Ollama server,
with model listing routed through Ollama's `/api/tags` endpoint.
Supports automatic endpoint discovery and does not require an API
key.

```jsonc
"sharpclaw_providers_ollama": "true"
```

---

### Providers — OpenAI-Compatible

| Setting | Value |
|---------|-------|
| **.env key** | `Modules:sharpclaw_providers_openai_compat` |
| **Default** | ✅ Enabled |
| **Platform** | All |
| **Prerequisites** | None |
| **Exports** | None |

Registers the OpenAI-protocol plugin family — OpenAI, DeepSeek, OpenRouter,
ZAI, Vercel AI Gateway, xAI, Groq, Cerebras, Mistral, GitHub Copilot,
Minimax, Custom, plus Google Gemini and Vertex AI OpenAI-compatible
shims. All share `OpenAiCompatibleApiClient` as their wire-format
base.

```jsonc
"sharpclaw_providers_openai_compat": "true"
```

---

### Providers — Whisper (local)

| Setting | Value |
|---------|-------|
| **.env key** | `Modules:sharpclaw_providers_whisper` |
| **Default** | ✅ Enabled |
| **Platform** | All |
| **Prerequisites** | None |
| **Exports** | `transcription_stt_local` (`ITranscriptionApiClient`) |

Local Whisper.net STT backend. Contributes an additional
`ITranscriptionApiClient` to the catalog consumed by the Transcription
module's factory; when this module is disabled the local backend
simply disappears and Transcription falls back to cloud STT (OpenAI /
Groq).

```jsonc
"sharpclaw_providers_whisper": "true"
```

---

### System Audio

| Setting | Value |
|---------|-------|
| **.env key** | `Modules:sharpclaw_systemaudio` |
| **Default** | ❌ Disabled |
| **Platform** | **Windows only** (WASAPI audio capture) |
| **Prerequisites** | None |
| **Exports** | `system_audio_capture` (`IAudioCaptureProvider`) |

Input audio device CRUD and WASAPI audio capture. Owns the
`InputAudio` resource type and exports the `system_audio_capture`
contract consumed by Transcription.

```jsonc
"sharpclaw_systemaudio": "true"
```

> **Important:** Required by Transcription. Disabling this module
> cascade-disables `sharpclaw_transcription`. See
> [Module-SystemAudio.md](Module-SystemAudio.md).

---

### Transcription

| Setting | Value |
|---------|-------|
| **.env key** | `Modules:sharpclaw_transcription` |
| **Default** | ❌ Disabled |
| **Platform** | **Windows only** (depends on WASAPI audio capture) |
| **Prerequisites** | **System Audio** (`system_audio_capture`) |
| **Exports** | `transcription_stt` (`ITranscriptionApiClient`) |

Live audio transcription and STT provider integration. Audio is
captured via the System Audio module (WASAPI, normalised to mono
16 kHz 16-bit PCM). Cloud providers: OpenAI Whisper, Groq. The
local Whisper.net backend is contributed by the
`sharpclaw_providers_whisper` module when enabled.

```jsonc
"sharpclaw_systemaudio": "true",
"sharpclaw_transcription": "true"
```

> **If System Audio is disabled**, Transcription will be excluded
> during dependency resolution with a missing-contract error.

---

### VS 2026 Editor

| Setting | Value |
|---------|-------|
| **.env key** | `Modules:sharpclaw_vs2026_editor` |
| **Default** | ❌ **Disabled** (not listed in default `.env`) |
| **Platform** | **Windows only** |
| **Prerequisites** | **Editor Common** (`editor_bridge`, `editor_session`) |
| **Exports** | None |

Visual Studio 2026 integration — read/write files, diagnostics, builds,
and terminal commands through a connected VS 2026 extension via
WebSocket.

**To enable**, add to the `Modules` section of your core `.env` and
ensure Editor Common is also enabled:

```jsonc
"sharpclaw_editor_common": "true",
"sharpclaw_vs2026_editor": "true"
```

> **If Editor Common is disabled**, VS 2026 Editor will be excluded
> during dependency resolution with a missing-contract error.

---

### VS Code Editor

| Setting | Value |
|---------|-------|
| **.env key** | `Modules:sharpclaw_vscode_editor` |
| **Default** | ❌ **Disabled** (not listed in default `.env`) |
| **Platform** | All |
| **Prerequisites** | **Editor Common** (`editor_bridge`, `editor_session`) |
| **Exports** | None |

Visual Studio Code integration — read/write files, diagnostics, builds,
and terminal commands through a connected VS Code extension via
WebSocket.

**To enable**, add to the `Modules` section of your core `.env` and
ensure Editor Common is also enabled:

```jsonc
"sharpclaw_editor_common": "true",
"sharpclaw_vscode_editor": "true"
```

> **If Editor Common is disabled**, VS Code Editor will be excluded
> during dependency resolution with a missing-contract error.

---

### Web Access

| Setting | Value |
|---------|-------|
| **.env key** | `Modules:sharpclaw_web_access` |
| **Default** | ❌ Disabled |
| **Platform** | All |
| **Prerequisites** | None |
| **Exports** | None |

Localhost access (headless browser + direct HTTP), external website
access with SSRF protection, and multi-provider search engine queries.

```jsonc
"sharpclaw_web_access": "true"
```

---

## Full `.env.template` Modules Section

This is the complete `Modules` section from the base `.env.template`.
Provider modules ship enabled so a clean install can talk to LLM and
STT backends; every feature module ships disabled and must be opted
into per deployment.

```jsonc
"Modules": {
  "sharpclaw_agent_orchestration": "false",
  "sharpclaw_bot_integration": "false",
  "sharpclaw_computer_use": "false",
  "sharpclaw_dangerous_shell": "false",
  "sharpclaw_database_access": "false",
  "sharpclaw_editor_common": "false",
  "sharpclaw_http": "false",
  "sharpclaw_metrics": "false",
  "sharpclaw_mk8shell": "false",
  "sharpclaw_module_dev": "false",
  "sharpclaw_office_apps": "false",
  "sharpclaw_providers_anthropic": "true",
  "sharpclaw_providers_google": "true",
  "sharpclaw_providers_llamasharp": "true",
  "sharpclaw_providers_ollama": "true",
  "sharpclaw_providers_openai_compat": "true",
  "sharpclaw_providers_whisper": "true",
  "sharpclaw_systemaudio": "false",
  "sharpclaw_transcription": "false",
  "sharpclaw_vs2026_editor": "false",
  "sharpclaw_vscode_editor": "false",
  "sharpclaw_web_access": "false"
}
```

The `.dev.env.template` mirrors this block with **every** module set to
`"true"` for local development.

---

## Troubleshooting

### Module not loading

1. Check the `.env` `Modules` section — the key must be exactly the
   module ID with value `"true"`.
2. Check platform — Windows-only modules (Computer Use, Transcription,
   VS 2026 Editor) will not load on Linux/macOS.
3. Check dependencies — `module list` shows which modules are loaded.
   If a dependency module is disabled, dependents are excluded.
4. Check logs — `InitializeAsync` failures are logged and the module is
   auto-disabled.

### Module cascade failure

If a provider module (e.g. Editor Common) fails to initialize, all
modules that require its contracts (VS 2026 Editor, VS Code Editor)
are automatically excluded. Fix the provider module first.

### Runtime enable/disable

```
module enable sharpclaw_vscode_editor    # Enable without restart
module disable sharpclaw_vscode_editor   # Disable without restart
module list                              # Verify status
```

Runtime changes take effect immediately. Endpoints remain mapped
(returning 503 when disabled) since routes cannot be removed after
`app.Build()`.

### Task trigger not firing

1. Run `task preflight <taskId>` and look for warning-level module recommendations.
2. Run `task trigger-sources` to confirm the source exists on the current host.
3. Make sure the owning module is enabled:
   - `sharpclaw_http` for webhook, host-reachable/unreachable, network-changed
   - `sharpclaw_metrics` for `MetricThreshold`
   - `sharpclaw_computer_use` for hotkeys, process events, desktop events, and
     `OsShortcut`
   - `sharpclaw_database_access` for `OnQueryReturnsRows`
4. Re-enable the task's triggers with `task triggers enable <taskId>` if needed.
