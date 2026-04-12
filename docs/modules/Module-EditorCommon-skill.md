SharpClaw Module: Editor Common — Agent Skill Reference

Module ID: sharpclaw_editor_common
Display Name: Editor Common
Tool Prefix: edc
Version: 1.0.0
Platforms: All
Exports: editor_bridge, editor_session
Requires: none

────────────────────────────────────────
ENABLING
────────────────────────────────────────
.env key: Modules:sharpclaw_editor_common
Default: disabled
Prerequisites: none
Platform: All

To enable, add to your core .env (Infrastructure/Environment/.env) Modules section:
  "sharpclaw_editor_common": "true"

To disable, set to "false" or remove the key (missing = disabled).

IMPORTANT: This is a dependency for VS 2026 Editor and VS Code Editor.
Disabling this module will cascade-disable both editor modules.

Exports: editor_bridge (EditorBridgeService),
         editor_session (EditorSessionService).

Runtime toggle (no restart required):
  module disable sharpclaw_editor_common
  module enable sharpclaw_editor_common

See docs/Module-Enablement-Guide.md for full details.

────────────────────────────────────────
OVERVIEW
────────────────────────────────────────
Infrastructure module: shared editor bridge (WebSocket) and session
management services consumed by VS 2026 and VS Code editor modules.
No LLM-callable tools — provides DI services, contract exports,
REST/WebSocket endpoints only.

────────────────────────────────────────
ENUMS
────────────────────────────────────────
EditorType: VisualStudio2026 (0), VisualStudioCode (1), Other (2).

────────────────────────────────────────
TOOLS
────────────────────────────────────────
None. This is an infrastructure module only.

────────────────────────────────────────
WEBSOCKET PROTOCOL
────────────────────────────────────────
WS /editor/ws (no JWT required)
Registration: { editorType, editorVersion?, workspacePath? }
Request (server→ext): { requestId, action, params }
Response (ext→server): { requestId, success, data?, error? }
Timeout: 30s per request.

────────────────────────────────────────
REST ENDPOINTS
────────────────────────────────────────
GET /editor/sessions              List connected editor sessions
POST/GET/PUT/DELETE /resources/editorsessions  Editor session resource CRUD

────────────────────────────────────────
CLI
────────────────────────────────────────
resource editorsession list          List all editor sessions
resource editorsession get <id>      Show an editor session
resource editorsession delete <id>   Delete an editor session

Aliases: editor, es

────────────────────────────────────────
EXPORTED CONTRACTS
────────────────────────────────────────
editor_bridge  → EditorBridgeService
editor_session → EditorSessionService

Required by VS 2026 Editor and VS Code Editor modules.
