# SharpClaw Module: VS 2026 Editor

> **Module ID:** `sharpclaw_vs2026_editor`
> **Display Name:** VS 2026 Editor
> **Version:** 1.0.0
> **Tool Prefix:** `vs26`
> **Platforms:** Windows only
> **Exports:** none
> **Requires:** `editor_bridge`, `editor_session` (from [Editor Common](Module-EditorCommon.md))

---

## Overview

The VS 2026 Editor module provides Visual Studio 2026 integration —
read/write files, diagnostics, builds, and terminal commands through a
connected VS 2026 extension. All tool actions are dispatched via the
Editor Common module's WebSocket bridge.

Tools are dispatched via the SharpClaw module system
(`AgentActionType = ModuleAction`). Tool names are prefixed with `vs26_`
when sent to the model. The `vs26_` prefix is stripped before forwarding
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

### vs26_read_file

Read file content from a connected VS 2026 instance. Optional
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

### vs26_get_open_files

List open files/tabs in the connected VS 2026 instance.

**Parameters:**

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `targetId` | string (GUID) | yes | EditorSession GUID |

**Permission:** Per-resource — requires `editorSessionAccesses` grant.

---

### vs26_get_selection

Get the active file, cursor position, and selected text in VS 2026.

**Parameters:**

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `targetId` | string (GUID) | yes | EditorSession GUID |

**Permission:** Per-resource — requires `editorSessionAccesses` grant.

---

### vs26_get_diagnostics

Get errors and warnings from VS 2026. Optional filePath to scope.

**Parameters:**

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `targetId` | string (GUID) | yes | EditorSession GUID |
| `filePath` | string | no | File path to scope results |

**Permission:** Per-resource — requires `editorSessionAccesses` grant.

---

### vs26_apply_edit

Replace a line range with new text in VS 2026.

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

### vs26_create_file

Create a new file in the VS 2026 workspace.

**Parameters:**

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `targetId` | string (GUID) | yes | EditorSession GUID |
| `filePath` | string | yes | File path relative to workspace root |
| `content` | string | no | Initial file content |

**Permission:** Per-resource — requires `editorSessionAccesses` grant.

---

### vs26_delete_file

Delete a file from the VS 2026 workspace.

**Parameters:**

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `targetId` | string (GUID) | yes | EditorSession GUID |
| `filePath` | string | yes | File path relative to workspace root |

**Permission:** Per-resource — requires `editorSessionAccesses` grant.

---

### vs26_show_diff

Show a diff view in VS 2026 for user review (accept/reject).

**Parameters:**

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `targetId` | string (GUID) | yes | EditorSession GUID |
| `filePath` | string | yes | File path relative to workspace root |
| `proposedContent` | string | yes | Proposed file content |
| `diffTitle` | string | no | Diff view title |

**Permission:** Per-resource — requires `editorSessionAccesses` grant.

---

### vs26_run_build

Trigger a build task in the connected VS 2026 instance and return
output.

**Parameters:**

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `targetId` | string (GUID) | yes | EditorSession GUID |

**Permission:** Per-resource — requires `editorSessionAccesses` grant.

---

### vs26_run_terminal

Run a command in the VS 2026 integrated terminal.

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

Editor sessions are auto-created when a VS 2026 extension connects to
the WebSocket bridge. The module validates that the session's
`EditorType` is `VisualStudio2026` before dispatching.

---

## Role Permissions

### Per-resource arrays

| Array | Resource Type | Tools |
|-------|---------------|-------|
| `editorSessionAccesses` | EditorSessions | All VS 2026 tools |

---

## Module Manifest

```json
{
  "id": "sharpclaw_vs2026_editor",
  "displayName": "VS 2026 Editor",
  "version": "1.0.0",
  "toolPrefix": "vs26",
  "entryAssembly": "SharpClaw.Modules.VS2026Editor",
  "minHostVersion": "1.0.0",
  "platforms": ["windows"],
  "exports": [],
  "requires": ["editor_bridge", "editor_session"]
}
```
