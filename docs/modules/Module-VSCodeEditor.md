# SharpClaw Module: VS Code Editor

> **Module ID:** `sharpclaw_vscode_editor`
> **Display Name:** VS Code Editor
> **Version:** 1.0.0
> **Tool Prefix:** `vsc`
> **Platforms:** All
> **Exports:** none
> **Requires:** `editor_bridge`, `editor_session` (from [Editor Common](Module-EditorCommon.md))

---

## How to Enable

| Setting | Value |
|---------|-------|
| **.env key** | `Modules:sharpclaw_vscode_editor` |
| **Default** | ❌ **Disabled** (not listed in default `.env`) |
| **Prerequisites** | **[Editor Common](Module-EditorCommon.md)** must be enabled |
| **Platform** | All |

This module requires the `editor_bridge` and `editor_session` contracts
exported by the Editor Common module. Both modules must be enabled.

Add **both** keys to your core `.env` (`Infrastructure/Environment/.env`) Modules section:

```jsonc
"sharpclaw_editor_common": "true",
"sharpclaw_vscode_editor": "true"
```

> ⚠️ If Editor Common is disabled, this module will be **excluded during
> dependency resolution** with a missing-contract error.

To disable, set to `"false"` or remove the key (missing = disabled).

**Runtime toggle** (no restart required):

```
module disable sharpclaw_vscode_editor
module enable sharpclaw_vscode_editor
```

See [Module Enablement Guide](Module-Enablement-Guide.md) for full details.

---

## Overview

The VS Code Editor module provides Visual Studio Code integration —
read/write files, diagnostics, builds, and terminal commands through a
connected VS Code extension. All tool actions are dispatched via the
Editor Common module's WebSocket bridge.

Tools are dispatched via the SharpClaw module system
(`AgentActionType = ModuleAction`). Tool names are prefixed with `vsc_`
when sent to the model. The `vsc_` prefix is stripped before forwarding
the action to the WebSocket bridge.

---

## Table of Contents

- [Tools](#tools)
- [CLI Commands](#cli-commands)
- [Resource Dependencies](#resource-dependencies)
- [Role Permissions](#role-permissions)
- [Module Manifest](#module-manifest)

---

## Tools

### vsc_read_file

Read file content from a connected VS Code instance. Optional
startLine/endLine for partial reads.

**Parameters:**

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `targetId` | string (GUID) | yes | EditorSession GUID |
| `filePath` | string | yes | File path relative to workspace root |
| `startLine` | integer | no | Start line (1-based) |
| `endLine` | integer | no | End line (1-based, inclusive) |

**Permission:** Per-resource — requires `editorSessionAccesses` grant.

---

### vsc_get_open_files

List open files/tabs in the connected VS Code instance.

**Parameters:**

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `targetId` | string (GUID) | yes | EditorSession GUID |

**Permission:** Per-resource — requires `editorSessionAccesses` grant.

---

### vsc_get_selection

Get the active file, cursor position, and selected text in VS Code.

**Parameters:**

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `targetId` | string (GUID) | yes | EditorSession GUID |

**Permission:** Per-resource — requires `editorSessionAccesses` grant.

---

### vsc_get_diagnostics

Get errors and warnings from VS Code. Optional filePath to scope.

**Parameters:**

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `targetId` | string (GUID) | yes | EditorSession GUID |
| `filePath` | string | no | File path to scope results |

**Permission:** Per-resource — requires `editorSessionAccesses` grant.

---

### vsc_apply_edit

Replace a line range with new text in VS Code.

**Parameters:**

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `targetId` | string (GUID) | yes | EditorSession GUID |
| `filePath` | string | yes | File path relative to workspace root |
| `startLine` | integer | yes | Start line (1-based) |
| `endLine` | integer | yes | End line (1-based, inclusive) |
| `newText` | string | yes | Replacement text |

**Permission:** Per-resource — requires `editorSessionAccesses` grant.

---

### vsc_create_file

Create a new file in the VS Code workspace.

**Parameters:**

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `targetId` | string (GUID) | yes | EditorSession GUID |
| `filePath` | string | yes | File path relative to workspace root |
| `content` | string | no | Initial file content |

**Permission:** Per-resource — requires `editorSessionAccesses` grant.

---

### vsc_delete_file

Delete a file from the VS Code workspace.

**Parameters:**

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `targetId` | string (GUID) | yes | EditorSession GUID |
| `filePath` | string | yes | File path relative to workspace root |

**Permission:** Per-resource — requires `editorSessionAccesses` grant.

---

### vsc_show_diff

Show a diff view in VS Code for user review (accept/reject).

**Parameters:**

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `targetId` | string (GUID) | yes | EditorSession GUID |
| `filePath` | string | yes | File path relative to workspace root |
| `proposedContent` | string | yes | Proposed file content |
| `diffTitle` | string | no | Diff view title |

**Permission:** Per-resource — requires `editorSessionAccesses` grant.

---

### vsc_run_build

Trigger a build task in the connected VS Code instance and return
output.

**Parameters:**

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `targetId` | string (GUID) | yes | EditorSession GUID |

**Permission:** Per-resource — requires `editorSessionAccesses` grant.

---

### vsc_run_terminal

Run a command in the VS Code integrated terminal.

**Parameters:**

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `targetId` | string (GUID) | yes | EditorSession GUID |
| `command` | string | yes | Command to run |
| `workingDirectory` | string | no | Working directory |

**Permission:** Per-resource — requires `editorSessionAccesses` grant.

---

## CLI Commands

This module does not register additional CLI commands. Editor sessions
are managed via the Editor Common module's `editorsession` command.

---

## Resource Dependencies

| Resource Type | Used by |
|---------------|---------|
| Editor Sessions | All tools (via `targetId`) |

Editor sessions are auto-created when a VS Code extension connects to
the WebSocket bridge. The module validates that the session's
`EditorType` is `VisualStudioCode` before dispatching.

---

## Role Permissions

### Per-resource arrays

| Array | Resource Type | Tools |
|-------|---------------|-------|
| `editorSessionAccesses` | EditorSessions | All VS Code tools |

---

## Module Manifest

```json
{
  "id": "sharpclaw_vscode_editor",
  "displayName": "VS Code Editor",
  "version": "1.0.0",
  "toolPrefix": "vsc",
  "entryAssembly": "SharpClaw.Modules.VSCodeEditor",
  "minHostVersion": "1.0.0",
  "platforms": null,
  "exports": [],
  "requires": ["editor_bridge", "editor_session"]
}
```
