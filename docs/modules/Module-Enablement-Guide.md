# Module Enablement Guide

How to enable, disable, and configure SharpClaw modules.

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
  "sharpclaw_agent_orchestration": "true",
  "sharpclaw_computer_use": "true",
  "sharpclaw_editor_common": "true"
  // ...
}
```

**Rules:**

- `"true"` → enabled
- `"false"` or **missing key** → disabled
- `.dev.env` overrides `.env` in development mode
- `.modules.env` (if present) is loaded between `.env` and `.dev.env`

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

| Module | ID | Default | Platform | Requires |
|--------|----|---------|----------|----------|
| [Agent Orchestration](#agent-orchestration) | `sharpclaw_agent_orchestration` | ❌ Disabled | All | — |
| [Bot Integration](#bot-integration) | `sharpclaw_bot_integration` | ❌ Disabled | All | — |
| [Computer Use](#computer-use) | `sharpclaw_computer_use` | ❌ Disabled | Windows | — |
| [Context Tools](#context-tools) | `sharpclaw_context_tools` | ❌ Disabled | All | — |
| [Dangerous Shell](#dangerous-shell) | `sharpclaw_dangerous_shell` | ❌ Disabled | Win / Linux / macOS | — |
| [Database Access](#database-access) | `sharpclaw_database_access` | ❌ Disabled | Win / Linux / macOS | — |
| [Editor Common](#editor-common) | `sharpclaw_editor_common` | ❌ Disabled | All | — |
| [mk8.shell](#mk8shell) | `sharpclaw_mk8shell` | ❌ Disabled | Win / Linux / macOS | — |
| [Module Dev Kit](#module-development-kit) | `sharpclaw_module_dev` | ❌ Disabled | All | Computer Use *(optional)* |
| [Office Apps](#office-apps) | `sharpclaw_office_apps` | ❌ Disabled | Win / Linux / macOS | — |
| [Transcription](#transcription) | `sharpclaw_transcription` | ❌ Disabled | Windows | — |
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
```

All other modules are standalone with no inter-module dependencies.

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

### Context Tools

| Setting | Value |
|---------|-------|
| **.env key** | `Modules:sharpclaw_context_tools` |
| **Default** | ❌ Disabled |
| **Platform** | All |
| **Prerequisites** | None |
| **Exports** | None |

Lightweight inline tools that execute directly in the ChatService
streaming loop: `wait`, `list_accessible_threads`, `read_thread_history`.

```jsonc
"sharpclaw_context_tools": "true"
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

### Transcription

| Setting | Value |
|---------|-------|
| **.env key** | `Modules:sharpclaw_transcription` |
| **Default** | ❌ Disabled |
| **Platform** | **Windows only** (WASAPI audio capture) |
| **Prerequisites** | None |
| **Exports** | `transcription_stt` (`ITranscriptionApiClient`), `transcription_audio_capture` (`IAudioCaptureProvider`) |

Live audio transcription, input audio device management, and STT
provider integration. Audio captured via WASAPI, normalised to mono
16 kHz 16-bit PCM. Providers: OpenAI Whisper, Groq, local Whisper.net.

```jsonc
"sharpclaw_transcription": "true"
```

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

This is the complete `Modules` section from the `.env.template`.
All modules are **disabled by default** (commented out). Uncomment the
block and the modules you want to enable.

```jsonc
// "Modules": {
//   "sharpclaw_agent_orchestration": "true",
//   "sharpclaw_bot_integration": "true",
//   "sharpclaw_computer_use": "true",
//   "sharpclaw_context_tools": "true",
//   "sharpclaw_dangerous_shell": "true",
//   "sharpclaw_database_access": "true",
//   "sharpclaw_editor_common": "true",
//   "sharpclaw_mk8shell": "true",
//   "sharpclaw_module_dev": "true",
//   "sharpclaw_office_apps": "true",
//   "sharpclaw_transcription": "true",
//   "sharpclaw_vs2026_editor": "true",
//   "sharpclaw_vscode_editor": "true",
//   "sharpclaw_web_access": "true"
// }
```

The `.dev.env.template` enables all modules for development environments.

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
