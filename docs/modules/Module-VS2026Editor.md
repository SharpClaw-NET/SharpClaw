# SharpClaw Module: VS 2026 Editor

> **Module ID:** `sharpclaw_vs2026_editor`
> **Display Name:** VS 2026 Editor
> **Version:** 1.0.0
> **Tool Prefix:** `vs26`
> **Platforms:** Windows only
> **Exports:** none
> **Requires:** `editor_bridge`, `editor_session` (from [Editor Common](Module-EditorCommon.md))

---

## How to Enable

| Setting | Value |
|---------|-------|
| **.env key** | `Modules:sharpclaw_vs2026_editor` |
| **Default** | ❌ **Disabled** (not listed in default `.env`) |
| **Prerequisites** | **[Editor Common](Module-EditorCommon.md)** must be enabled |
| **Platform** | Windows only |

This module requires the `editor_bridge` and `editor_session` contracts
exported by the Editor Common module. Both modules must be enabled.

Add **both** keys to your core `.env` (`Infrastructure/Environment/.env`) Modules section:

```jsonc
"sharpclaw_editor_common": "true",
"sharpclaw_vs2026_editor": "true"
```

> ⚠️ If Editor Common is disabled, this module will be **excluded during
> dependency resolution** with a missing-contract error.

To disable, set to `"false"` or remove the key (missing = disabled).

**Runtime toggle** (no restart required):

```
module disable sharpclaw_vs2026_editor
module enable sharpclaw_vs2026_editor
```

See [Module Enablement Guide](Module-Enablement-Guide.md) for full details.

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

**Requires VS2026 Extension**: This module requires the SharpClaw VS2026
Extension to be installed in Visual Studio 2026. See [Extension Setup](#extension-setup).

---

## Table of Contents

- [Extension Setup](#extension-setup)
- [Tools](#tools)
- [CLI Commands](#cli-commands)
- [Resource Dependencies](#resource-dependencies)
- [Role Permissions](#role-permissions)
- [Module Manifest](#module-manifest)

---

## Extension Setup

### Installation

1. **Download VSIX Package**:
   - From GitHub releases: https://github.com/mkn8rn/SharpClaw/releases
   - Or build from source (see [Development Guide](../../sharpclaw-vs2026/DEVELOPMENT.md))

2. **Install Extension**:
   ```powershell
   # Option 1: Double-click VSIX file
   explorer.exe SharpClaw.VS2026Extension.vsix
   
   # Option 2: Command line
   VSIXInstaller.exe /quiet SharpClaw.VS2026Extension.vsix
   ```

3. **Restart Visual Studio 2026**

### Connection

The extension auto-connects to SharpClaw backend on startup. Manual controls:

- **Tools → Connect to SharpClaw** - Manually connect
- **Tools → Disconnect from SharpClaw** - Manually disconnect

Status is shown in the VS status bar.

### Configuration

Default connection: `ws://localhost:5163/api/editor/bridge`

To change the port, update `SharpClaw.Application.API` listen URL in:
`Infrastructure/Environment/.env`

```jsonc
"Api": {
  "ListenUrl": "http://localhost:5163"
}
```

### Building Extension from Source

The extension is **excluded from regular solution builds**. To build:

```powershell
# Build VSIX package
dotnet build sharpclaw-vs2026/SharpClaw.VS2026Extension.csproj -c Release /p:PublishVSExtension=true

# Or use publish script
sharpclaw-vs2026\.vsextension\publish.ps1
```

See [Development Guide](../../sharpclaw-vs2026/DEVELOPMENT.md) for full details.

---

## Tools

> **Implementation Status**: All tool operations are fully implemented.

### vs26_read_file

Read file content from a connected VS 2026 instance. Optional
startLine/endLine for partial reads.

**Status:** ✅ Fully implemented

**Parameters:**

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `targetId` | string (GUID) | yes | EditorSession GUID |
| `filePath` | string | yes | File path relative to workspace root |
| `startLine` | integer | no | Start line (1-based) |
| `endLine` | integer | no | End line (1-based, inclusive) |

**Permission:** Per-resource — requires `editorSessionAccesses` grant.

---

### vs26_write_file

Write content to a file in the connected VS 2026 workspace.

**Status:** ✅ Fully implemented

**Parameters:**

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `targetId` | string (GUID) | yes | EditorSession GUID |
| `filePath` | string | yes | File path relative to workspace root |
| `content` | string | yes | Full file content to write |

**Permission:** Per-resource — requires `editorSessionAccesses` grant.

---

### vs26_get_open_files

List open files/tabs in the connected VS 2026 instance.

**Status:** ✅ Fully implemented

**Parameters:**

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `targetId` | string (GUID) | yes | EditorSession GUID |

**Permission:** Per-resource — requires `editorSessionAccesses` grant.

---

### vs26_get_selection

Get the active file, cursor position, and selected text in VS 2026.

**Status:** ✅ Fully implemented

**Parameters:**

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `targetId` | string (GUID) | yes | EditorSession GUID |

**Permission:** Per-resource — requires `editorSessionAccesses` grant.

---

### vs26_get_diagnostics

Get errors and warnings from VS 2026. Optional filePath to scope.

**Status:** ✅ Fully implemented (Error List category filter cycling for severity)

**Parameters:**

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `targetId` | string (GUID) | yes | EditorSession GUID |
| `filePath` | string | no | File path to scope results |

**Permission:** Per-resource — requires `editorSessionAccesses` grant.

---

### vs26_apply_edit

Replace a line range with new text in VS 2026.

**Status:** ✅ Fully implemented (file-based line replacement)

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

**Status:** ✅ Fully implemented

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

**Status:** ✅ Fully implemented

**Parameters:**

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `targetId` | string (GUID) | yes | EditorSession GUID |
| `filePath` | string | yes | File path relative to workspace root |

**Permission:** Per-resource — requires `editorSessionAccesses` grant.

---

### vs26_show_diff

Show a diff view in VS 2026 for user review (accept/reject).

**Status:** ✅ Fully implemented (IVsDifferenceService comparison window)

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

**Status:** ✅ Fully implemented (async DTE SolutionBuild with OnBuildDone event)

**Parameters:**

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `targetId` | string (GUID) | yes | EditorSession GUID |

**Permission:** Per-resource — requires `editorSessionAccesses` grant.

---

### vs26_run_terminal

Run a command in the VS 2026 integrated terminal.

**Status:** ✅ Fully implemented (cmd.exe subprocess with stdout/stderr capture)

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
