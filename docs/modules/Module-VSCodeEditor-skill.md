SharpClaw Module: VS Code Editor — Agent Skill Reference

Module ID: sharpclaw_vscode_editor
Display Name: VS Code Editor
Tool Prefix: vsc
Version: 1.0.0
Platforms: All
Exports: none
Requires: editor_bridge, editor_session (from Editor Common)

────────────────────────────────────────
ENABLING
────────────────────────────────────────
.env key: Modules:sharpclaw_vscode_editor
Default: DISABLED (not listed in default .env)
Prerequisites: Editor Common (sharpclaw_editor_common) must be enabled.
  Provides required contracts: editor_bridge, editor_session.
Platform: All

To enable, add BOTH keys to your core .env Modules section:
  "sharpclaw_editor_common": "true",
  "sharpclaw_vscode_editor": "true"

If Editor Common is disabled, this module will be excluded during
dependency resolution with a missing-contract error.

To disable, set to "false" or remove the key (missing = disabled).

Runtime toggle (no restart required):
  module disable sharpclaw_vscode_editor
  module enable sharpclaw_vscode_editor

See docs/Module-Enablement-Guide.md for full details.

────────────────────────────────────────
OVERVIEW
────────────────────────────────────────
Visual Studio Code integration — read/write files, diagnostics, builds,
and terminal commands through a connected VS Code extension via WebSocket.

Tools are dispatched via the module system (AgentActionType = ModuleAction).
Tool names are prefixed with "vsc_" when sent to the model.

────────────────────────────────────────
TOOLS (10)
────────────────────────────────────────

vsc_read_file
  Read file content. Optional startLine/endLine for partial reads.
  Params: targetId (EditorSession GUID, required),
          filePath (string, required), startLine (int, optional),
          endLine (int, optional)
  Permission: per-resource (EditorSession)

vsc_get_open_files
  List open files/tabs in VS Code.
  Params: targetId (EditorSession GUID, required)
  Permission: per-resource (EditorSession)

vsc_get_selection
  Get active file, cursor position, and selected text.
  Params: targetId (EditorSession GUID, required)
  Permission: per-resource (EditorSession)

vsc_get_diagnostics
  Get errors and warnings. Optional filePath to scope.
  Params: targetId (EditorSession GUID, required),
          filePath (string, optional)
  Permission: per-resource (EditorSession)

vsc_apply_edit
  Replace a line range with new text.
  Params: targetId (EditorSession GUID, required),
          filePath (string, required), startLine (int, required),
          endLine (int, required), newText (string, required)
  Permission: per-resource (EditorSession)

vsc_create_file
  Create a new file in the workspace.
  Params: targetId (EditorSession GUID, required),
          filePath (string, required), content (string, optional)
  Permission: per-resource (EditorSession)

vsc_delete_file
  Delete a file from the workspace.
  Params: targetId (EditorSession GUID, required),
          filePath (string, required)
  Permission: per-resource (EditorSession)

vsc_show_diff
  Show diff view for user review (accept/reject).
  Params: targetId (EditorSession GUID, required),
          filePath (string, required), proposedContent (string, required),
          diffTitle (string, optional)
  Permission: per-resource (EditorSession)

vsc_run_build
  Trigger a build and return output.
  Params: targetId (EditorSession GUID, required)
  Permission: per-resource (EditorSession)

vsc_run_terminal
  Run a command in the integrated terminal.
  Params: targetId (EditorSession GUID, required),
          command (string, required),
          workingDirectory (string, optional)
  Permission: per-resource (EditorSession)

────────────────────────────────────────
RESOURCE DEPENDENCIES
────────────────────────────────────────
- EditorSessions — for all tools (auto-created on extension connect)

────────────────────────────────────────
ROLE PERMISSIONS (relevant)
────────────────────────────────────────
Per-resource: editorSessionAccesses (EditorSessions)
