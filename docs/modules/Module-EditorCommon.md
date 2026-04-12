# SharpClaw Module: Editor Common

> **Module ID:** `sharpclaw_editor_common`
> **Display Name:** Editor Common
> **Version:** 1.0.0
> **Tool Prefix:** `edc`
> **Platforms:** All
> **Exports:** `editor_bridge` (`EditorBridgeService`), `editor_session` (`EditorSessionService`)
> **Requires:** none

---

## How to Enable

| Setting | Value |
|---------|-------|
| **.env key** | `Modules:sharpclaw_editor_common` |
| **Default** | ❌ Disabled |
| **Prerequisites** | None |
| **Platform** | All |

To enable, add to your core `.env` (`Infrastructure/Environment/.env`) Modules section:

```jsonc
"sharpclaw_editor_common": "true"
```

To disable, set to `"false"` or remove the key (missing = disabled).

> **Important:** This is an infrastructure module required by **VS 2026 Editor**
> and **VS Code Editor**. Disabling it will cascade-disable both editor modules.

Exports: `editor_bridge` (`EditorBridgeService`), `editor_session` (`EditorSessionService`).

**Runtime toggle** (no restart required):

```
module disable sharpclaw_editor_common
module enable sharpclaw_editor_common
```

See [Module Enablement Guide](Module-Enablement-Guide.md) for full details.

---

## Overview

The Editor Common module is an **infrastructure module** that provides
the shared editor bridge (WebSocket) and session management services
consumed by the VS 2026 and VS Code editor modules. It does **not**
expose any LLM-callable tools — it provides only DI services, contract
exports, REST endpoints, and WebSocket endpoints.

IDE extensions connect via the WebSocket endpoint and enter a
request/response loop managed by `EditorBridgeService`. Editor sessions
are auto-created when an extension connects.

---

## Table of Contents

- [Enums](#enums)
- [WebSocket Protocol](#websocket-protocol)
- [REST Endpoints](#rest-endpoints)
- [CLI Commands](#cli-commands)
- [Resource Dependencies](#resource-dependencies)
- [Exported Contracts](#exported-contracts)

---

## Enums

### EditorType

| Value | Int | Description |
|-------|-----|-------------|
| `VisualStudio2026` | 0 | Visual Studio 2026 IDE |
| `VisualStudioCode` | 1 | Visual Studio Code |
| `Other` | 2 | Other / unrecognised editor |

---

## WebSocket Protocol

### WS /editor/ws

IDE extensions connect here with a WebSocket upgrade. **No JWT
required** (exempt path).

**Registration message (extension → server):**

```json
{
  "editorType": "VisualStudio2026 | VisualStudioCode | Other",
  "editorVersion": "string | null",
  "workspacePath": "string | null"
}
```

**Request (server → extension):**

```json
{
  "requestId": "guid",
  "action": "string",
  "params": { ... }
}
```

**Response (extension → server):**

```json
{
  "requestId": "guid",
  "success": true,
  "data": "string | null",
  "error": "string | null"
}
```

30-second timeout per request.

---

## REST Endpoints

### GET /editor/sessions

List all currently connected editor sessions.

**Response `200`:**

```json
[
  {
    "sessionId": "guid",
    "editorType": "VisualStudio2026",
    "editorVersion": "string | null",
    "workspacePath": "string | null",
    "isConnected": true,
    "connectedAt": "datetime"
  }
]
```

### Editor Session Resources

```
POST   /resources/editorsessions
GET    /resources/editorsessions
GET    /resources/editorsessions/{id}
PUT    /resources/editorsessions/{id}
DELETE /resources/editorsessions/{id}
```

Editor sessions are auto-created when an IDE extension connects.

---

## CLI Commands

The module registers an `editorsession` resource command (aliases:
`editor`, `es`):

```
resource editorsession list                      List all editor sessions
resource editorsession get <id>                  Show an editor session
resource editorsession delete <id>               Delete an editor session
```

Editor sessions are auto-created when an IDE extension connects.
Use `channel defaults <id> set editor <sessionId>` to assign one.

---

## Resource Dependencies

| Resource Type | Used by |
|---------------|---------|
| Editor Sessions | VS 2026 and VS Code editor module tools |

---

## Exported Contracts

| Contract Name | Interface | Description |
|---------------|-----------|-------------|
| `editor_bridge` | `EditorBridgeService` | WebSocket-based IDE bridge for editor extensions |
| `editor_session` | `EditorSessionService` | Editor session CRUD management |

The VS 2026 and VS Code editor modules both require these contracts.
Disabling Editor Common while either editor module is enabled is
rejected with `409 Conflict`.
