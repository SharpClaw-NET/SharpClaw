# SharpClaw Module: Module Development Kit

> **Module ID:** `sharpclaw_module_dev`
> **Display Name:** Module Development Kit
> **Version:** 1.0.0
> **Tool Prefix:** `mdk`
> **Platforms:** All (process inspection features are Windows-enhanced)
> **Exports:** none
> **Requires:** `window_management` (optional — improves PID resolution)

---

## How to Enable

| Setting | Value |
|---------|-------|
| **.env key** | `Modules:sharpclaw_module_dev` |
| **Default** | ❌ **Disabled** (not listed in default `.env`) |
| **Prerequisites** | [Computer Use](Module-ComputerUse.md) *(optional)* |
| **Platform** | All |

This module is **disabled by default**. To enable, add to your core
`.env` (`Infrastructure/Environment/.env`) Modules section:

```jsonc
"sharpclaw_module_dev": "true"
```

For enhanced process inspection (window-title → PID resolution),
also enable Computer Use:

```jsonc
"sharpclaw_computer_use": "true",
"sharpclaw_module_dev": "true"
```

Without Computer Use, process inspection falls back to
`Process.GetProcessesByName` (less precise).

To disable, set to `"false"` or remove the key (missing = disabled).

**Runtime toggle** (no restart required):

```
module disable sharpclaw_module_dev
module enable sharpclaw_module_dev
```

See [Module Enablement Guide](Module-Enablement-Guide.md) for full details.

---

## Overview

The Module Development Kit (MDK) enables an LLM agent to **autonomously
author, build, hot-load, test, and iterate** on new SharpClaw modules
without human intervention. It also provides deep development-environment
introspection: live process inspection, PE export enumeration, COM type
library discovery, and SDK/runtime querying.

All workspace I/O is sandboxed to the `external-modules/` directory.
Path traversal, absolute paths, reserved device names, and non-allowlisted
extensions are rejected. Only `.cs` and `.json` files can be written.

Tools are dispatched via the SharpClaw module system
(`AgentActionType = ModuleAction`). Tool names are prefixed with `mdk_`
when sent to the model — for example, `scaffold_module` becomes
`mdk_scaffold_module`.

---

## Table of Contents

- [Tools](#tools)
  - [mdk_scaffold_module](#mdk_scaffold_module)
  - [mdk_write_file](#mdk_write_file)
  - [mdk_read_file](#mdk_read_file)
  - [mdk_list_files](#mdk_list_files)
  - [mdk_build_module](#mdk_build_module)
  - [mdk_load_module](#mdk_load_module)
  - [mdk_unload_module](#mdk_unload_module)
  - [mdk_test_tool](#mdk_test_tool)
  - [mdk_inspect_process](#mdk_inspect_process)
  - [mdk_discover_com_interfaces](#mdk_discover_com_interfaces)
  - [mdk_enumerate_dev_environment](#mdk_enumerate_dev_environment)
- [Inline Tools](#inline-tools)
  - [mdk_describe_module_system](#mdk_describe_module_system)
  - [mdk_list_loaded_modules](#mdk_list_loaded_modules)
- [CLI Commands](#cli-commands)
- [Role Permissions](#role-permissions)
- [Required Contracts](#required-contracts)
- [Security Model](#security-model)
- [Module Manifest](#module-manifest)

---

## Tools

### mdk_scaffold_module

Generate a complete module project from a specification. Creates
`.csproj`, module class, `module.json`, and optional tool stubs in
`external-modules/{module_id}/`.

**Parameters:**

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `module_id` | string | yes | Module ID (`^[a-z][a-z0-9_]{0,39}$`) |
| `display_name` | string | yes | Human-readable name |
| `tool_prefix` | string | yes | Tool prefix (`^[a-z][a-z0-9]{0,19}$`) |
| `description` | string | no | Module description |
| `tools` | array | no | Tool stubs to generate (see below) |

Each tool stub object:

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `name` | string | yes | Tool name |
| `description` | string | no | Tool description |
| `parameters_hint` | string | no | Hint for parameter schema |

**Permission:** Global — requires `CanScaffoldModules` flag.

**Returns:** JSON with `moduleDir` path and list of created files.

---

### mdk_write_file

Write or overwrite a file inside a module workspace. Scoped to
`external-modules/` — cannot write outside it. Only `.cs` and `.json`
extensions are permitted.

**Parameters:**

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `module_id` | string | yes | Target module ID |
| `relative_path` | string | yes | Path relative to module root (e.g. `Services/MyService.cs`) |
| `content` | string | yes | Full file content |

**Permission:** Global — requires `CanWriteModuleFiles` flag.

**Returns:** JSON with written `path` and `bytes_written`.

---

### mdk_read_file

Read a file from a module workspace. Returns content with optional line
truncation.

**Parameters:**

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `module_id` | string | yes | Target module ID |
| `relative_path` | string | yes | Path relative to module root |
| `max_lines` | integer | no | Truncation limit (default: 500) |

**Permission:** Global — requires `CanWriteModuleFiles` flag.

**Returns:** File content as text. Appends truncation notice if exceeded.

---

### mdk_list_files

List the file tree of a module workspace, with optional glob filtering.

**Parameters:**

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `module_id` | string | yes | Target module ID |
| `include_pattern` | string | no | Glob filter (default: `**/*`) |

**Permission:** Global — requires `CanWriteModuleFiles` flag.

**Returns:** JSON array of relative file paths.

---

### mdk_build_module

Compile a module project using `dotnet build`. Returns structured
diagnostics (errors, warnings) and the output DLL path on success.

**Parameters:**

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `module_id` | string | yes | Module to build |
| `configuration` | string | no | `Debug` (default) or `Release` |

**Permission:** Global — requires `CanBuildModules` flag.

**Timeout:** 120 seconds.

**Returns:** JSON with `success` boolean, `errors` and `warnings`
arrays (each with `file`, `line`, `column`, `code`, `message`), and
`output_dll` path.

---

### mdk_load_module

Hot-load a compiled module into the running host. If already loaded,
automatically reloads it (drain → unload → reload).

**Parameters:**

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `module_id` | string | yes | Module ID |

**Permission:** Global — requires `CanLoadModules` flag.

**Returns:** JSON load result from `ModuleService`.

---

### mdk_unload_module

Unload a hot-loaded module from the running host. The module's
`ShutdownAsync` is called and its collectible `AssemblyLoadContext`
is unloaded.

**Parameters:**

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `module_id` | string | yes | Module ID |

**Permission:** Global — requires `CanLoadModules` flag.

**Returns:** JSON with `moduleId` and `unloaded: true`.

---

### mdk_test_tool

Invoke a tool from any loaded module by name. Useful for verifying a
freshly built module's tools work correctly.

**Parameters:**

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `tool_name` | string | yes | Fully qualified tool name (e.g. `my_tool`) |
| `parameters` | object | yes | JSON parameters to pass |
| `timeout_seconds` | integer | no | Override timeout (default: 30) |

**Permission:** Global — requires `CanTestModuleTools` flag.

**Returns:** Raw tool output string from the target module.

---

### mdk_inspect_process

Inspect a live process: loaded modules (DLLs), exported functions,
window classes, and thread info. Read-only reconnaissance. Windows uses
Toolhelp32 and PE parsing; Linux uses `/proc`.

**Parameters:**

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `target` | string | yes | Process name, PID, or window title substring |
| `include` | string[] | no | Sections: `modules`, `exports`, `window_classes`, `threads` |
| `export_filter` | string | no | Regex filter for export names |

**Permission:** Global — requires `CanInspectProcesses` flag.

**Timeout:** 60 seconds.

**Returns:** JSON with `processId`, `processName`, modules (with
exports), window classes, threads, and any errors.

---

### mdk_discover_com_interfaces

Deep-dive into a COM type library: enumerate interfaces, coclasses,
methods, parameters, and return types. **Windows only.**

**Parameters:**

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `typelib_path` | string | yes | Path to `.tlb` or `.dll` with embedded type lib |
| `interface_filter` | string | no | Regex to filter interface names |
| `include_inherited` | boolean | no | Include inherited members (default: `false`) |

**Permission:** Global — requires `CanInspectProcesses` flag.

**Timeout:** 60 seconds.

**Returns:** JSON with `path`, `entries` (interfaces/coclasses with
methods), and any errors.

---

### mdk_enumerate_dev_environment

Report the full development environment: installed .NET SDKs, runtimes,
global tools, contracts assembly version and path, host version, all
loaded modules with their exported contracts, and the external modules
directory path.

**Parameters:** none (empty object)

**Permission:** Global — requires `CanScaffoldModules` flag.

**Returns:** JSON `DevEnvironmentInfo` object.

---

## Inline Tools

Inline tools run without creating a job record and are resolved
in-context by the chat pipeline.

### mdk_describe_module_system

Return a concise reference card of the SharpClaw module contract:
`ISharpClawModule` interface, tool definitions, permissions, contract
system, manifest format, and hot-loading mechanics.

**Parameters:** none

---

### mdk_list_loaded_modules

Return the current list of loaded modules with IDs, prefixes, tool
counts, inline tool counts, and exported contract names.

**Parameters:** none

---

## CLI Commands

The module registers a top-level `mdk` command (alias: `module-dev`):

```
mdk scaffold <id> <name> <prefix> [description]   Scaffold a new module
mdk build <id> [--release]                         Build a module
mdk load <id>                                      Hot-load a module
mdk unload <id>                                    Unload a module
mdk reload <id>                                    Reload a module
mdk inspect <process_name_or_pid>                  Inspect a process
mdk env                                            Show dev environment
mdk list                                           List module workspaces
```

---

## Role Permissions

### Global flags

| Flag | Description | Tools |
|------|-------------|-------|
| `CanScaffoldModules` | Create new module project skeletons | `mdk_scaffold_module`, `mdk_enumerate_dev_environment` |
| `CanWriteModuleFiles` | Read/write files in module workspaces | `mdk_write_file`, `mdk_read_file`, `mdk_list_files` |
| `CanBuildModules` | Compile module projects | `mdk_build_module` |
| `CanLoadModules` | Hot-load or unload modules | `mdk_load_module`, `mdk_unload_module` |
| `CanTestModuleTools` | Invoke tools for testing | `mdk_test_tool` |
| `CanInspectProcesses` | Process and COM introspection | `mdk_inspect_process`, `mdk_discover_com_interfaces` |

There are no per-resource permissions — all tools use global flags only.

---

## Required Contracts

| Contract Name | Interface | Optional | Description |
|---------------|-----------|----------|-------------|
| `window_management` | `IWindowManager` | yes | Window-title → PID resolution for process inspection. Falls back to `Process.GetProcessesByName` when unavailable. |

The MDK does not export any contracts.

---

## Security Model

### Workspace sandbox

- All file operations are scoped to `external-modules/{module_id}/`.
- Module IDs are validated against `^[a-z][a-z0-9_]{0,39}$`.
- Relative paths are validated: no traversal (`..`), no absolute paths,
  no null bytes, no reserved Windows device names (CON, PRN, NUL, etc.).
- Resolved paths are canonicalized and checked to remain inside the
  module directory.

### Extension allowlist

Only `.cs` and `.json` files may be written. Binary files (`.dll`,
`.exe`, etc.) can only be produced through the `mdk_build_module` tool
which invokes `dotnet build` as a subprocess.

### Build isolation

Module compilation runs in a separate `dotnet build` process with a
120-second timeout. The host process is never affected by compilation
errors.

### Hot-load isolation

External modules load into a **collectible `AssemblyLoadContext`**,
meaning they can be cleanly unloaded without restarting the host.

---

## Module Manifest

```json
{
  "id": "sharpclaw_module_dev",
  "displayName": "Module Development Kit",
  "version": "1.0.0",
  "toolPrefix": "mdk",
  "entryAssembly": "SharpClaw.Modules.ModuleDev",
  "minHostVersion": "1.0.0",
  "description": "Autonomous module authoring, building, hot-loading, and development introspection tools.",
  "platforms": null,
  "exports": [],
  "requires": [
    {
      "contractName": "window_management",
      "serviceType": "SharpClaw.Contracts.Modules.Contracts.IWindowManager",
      "optional": true
    }
  ]
}
```
