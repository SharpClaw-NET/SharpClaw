SharpClaw Module: VS 2026 Editor — Agent Skill Reference

Module ID: sharpclaw_vs2026_editor
Display Name: VS 2026 Editor
Tool Prefix: vs26
Version: 1.0.0
Platforms: Windows only
Exports: none
Requires: editor_bridge, editor_session (from Editor Common)

────────────────────────────────────────
OVERVIEW
────────────────────────────────────────
Visual Studio 2026 integration — read/write files, diagnostics, builds,
and terminal commands through a connected VS 2026 extension via WebSocket.

Tools are dispatched via the module system (AgentActionType = ModuleAction).
Tool names are prefixed with "vs26_" when sent to the model.

────────────────────────────────────────
TOOLS (10)
────────────────────────────────────────

vs26_read_file
  Read file content. Optional startLine/endLine for partial reads.
  Params: targetId (EditorSession GUID, required),
          filePath (string, required), startLine (int, optional),
          endLine (int, optional)
  Permission: per-resource (EditorSession)

vs26_get_open_files
  List open files/tabs in VS 2026.
  Params: targetId (EditorSession GUID, required)
  Permission: per-resource (EditorSession)

vs26_get_selection
  Get active file, cursor position, and selected text.
  Params: targetId (EditorSession GUID, required)
  Permission: per-resource (EditorSession)

vs26_get_diagnostics
  Get errors and warnings. Optional filePath to scope.
  Params: targetId (EditorSession GUID, required),
          filePath (string, optional)
  Permission: per-resource (EditorSession)

vs26_apply_edit
  Replace a line range with new text.
  Params: targetId (EditorSession GUID, required),
          filePath (string, required), startLine (int, required),
          endLine (int, required), newText (string, required)
  Permission: per-resource (EditorSession)

vs26_create_file
  Create a new file in the workspace.
  Params: targetId (EditorSession GUID, required),
          filePath (string, required), content (string, optional)
  Permission: per-resource (EditorSession)

vs26_delete_file
  Delete a file from the workspace.
  Params: targetId (EditorSession GUID, required),
          filePath (string, required)
  Permission: per-resource (EditorSession)

vs26_show_diff
  Show diff view for user review (accept/reject).
  Params: targetId (EditorSession GUID, required),
          filePath (string, required), proposedContent (string, required),
          diffTitle (string, optional)
  Permission: per-resource (EditorSession)

vs26_run_build
  Trigger a build and return output.
  Params: targetId (EditorSession GUID, required)
  Permission: per-resource (EditorSession)

vs26_run_terminal
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
