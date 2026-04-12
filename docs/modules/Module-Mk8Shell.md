# SharpClaw Module: mk8.shell

> **Module ID:** `sharpclaw_mk8shell`
> **Display Name:** mk8.shell
> **Version:** 1.0.0
> **Tool Prefix:** `mk8`
> **Platforms:** Windows, Linux, macOS
> **Exports:** none
> **Requires:** none

---

## How to Enable

| Setting | Value |
|---------|-------|
| **.env key** | `Modules:sharpclaw_mk8shell` |
| **Default** | ❌ Disabled |
| **Prerequisites** | None |
| **Platform** | Windows, Linux, macOS |

To enable, add to your core `.env` (`Infrastructure/Environment/.env`) Modules section:

```jsonc
"sharpclaw_mk8shell": "true"
```

To disable, set to `"false"` or remove the key (missing = disabled).

**Runtime toggle** (no restart required):

```
module disable sharpclaw_mk8shell
module enable sharpclaw_mk8shell
```

See [Module Enablement Guide](Module-Enablement-Guide.md) for full details.

---

## Overview

The mk8.shell module provides **sandboxed** script execution through the
mk8.shell DSL and sandbox container lifecycle management. mk8.shell is a
closed-verb, sandboxed language that **never invokes a real shell
interpreter**. Even its `ProcRun` and `Git*` verbs go through
binary-allowlist validation, path sandboxing, and argument sanitisation
before spawning a process.

Tools are dispatched via the SharpClaw module system
(`AgentActionType = ModuleAction`). Tool names are prefixed with `mk8_`
when sent to the model — for example, `execute_mk8_shell` becomes
`mk8_execute_mk8_shell`.

---

## Table of Contents

- [Enums](#enums)
- [Tools](#tools)
  - [mk8_execute_mk8_shell](#mk8_execute_mk8_shell)
  - [mk8_create_mk8_sandbox](#mk8_create_mk8_sandbox)
- [CLI Commands](#cli-commands)
- [Resource Dependencies](#resource-dependencies)
- [Role Permissions](#role-permissions)
- [Execution Pipeline](#execution-pipeline)
- [Module Manifest](#module-manifest)

---

## Enums

### SafeShellType

| Value | Int | Description |
|-------|-----|-------------|
| `Mk8Shell` | 0 | Sandboxed mk8.shell DSL — never invokes a real shell interpreter |

mk8.shell is **always safe** by definition. Bash, PowerShell,
CommandPrompt, and Git are **always dangerous** — see the
[Dangerous Shell module](Module-DangerousShell.md).

---

## Tools

### mk8_execute_mk8_shell

Execute an mk8.shell script within a registered sandbox container.
Scripts are compiled through the full mk8.shell pipeline
(Expand → Resolve → Sanitize → Compile) before execution.

**Parameters:**

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `resourceId` | string (GUID) | conditional | Container GUID (required if `sandboxId` not provided) |
| `sandboxId` | string | conditional | Sandbox name (required if `resourceId` not provided) |
| `script` | object | yes | Serialised `Mk8ShellScript` JSON |

**Permission:** Per-resource — requires `containerAccesses` grant
(delegates to `AccessContainerAsync`).

**Timeout:** 300 seconds.

**Returns:** Execution summary with step-by-step results (verb, status,
attempts, duration, errors).

---

### mk8_create_mk8_sandbox

Create a new mk8.shell sandbox container. The sandbox is registered
with mk8.shell's local registry and a container resource is created.

**Parameters:**

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `name` | string | yes | Sandbox name (alphanumeric) |
| `path` | string | yes | Base path where the sandbox directory will be created |
| `description` | string | no | Optional description |

**Permission:** Global — requires `canCreateContainers` flag.

**Returns:** Confirmation with container ID and sandbox directory path.

---

## CLI Commands

The module registers a `container` resource command:

```
resource container add mk8shell <name> <path>   Create an mk8shell sandbox
resource container get <id>                      Show a container
resource container list                          List all containers
resource container update <id> [description]     Update a container
resource container delete <id>                   Delete a container
resource container sync                          Import from mk8.shell registry
```

---

## Resource Dependencies

| Resource Type | Used by |
|---------------|---------|
| Containers | `mk8_execute_mk8_shell`, `mk8_create_mk8_sandbox` |

Containers can be synced from the local mk8.shell registry via
`POST /resources/containers/sync` or `resource container sync`.

---

## Role Permissions

### Global flags

| Flag | Tools |
|------|-------|
| `canCreateContainers` | `mk8_create_mk8_sandbox` |

### Per-resource arrays

| Array | Resource Type | Tools |
|-------|---------------|-------|
| `safeShellAccesses` | Containers | `mk8_execute_mk8_shell` |
| `containerAccesses` | Containers | `mk8_execute_mk8_shell` |

---

## Execution Pipeline

mk8.shell scripts go through a multi-stage pipeline:

1. **Expand** — ForEach, If, Batch expanders
2. **Resolve** — variable resolution, workspace-relative paths
3. **Sanitize** — binary allowlist, path sandboxing, env key blocking,
   git flag validation, free-text constraints
4. **Compile** — produce executable command objects

Execution supports retries, timeouts, failure modes, output caps, and
`$PREV` piping. Sandboxes use `$WORKSPACE` and `$CWD`. Paths are
workspace-relative only.

---

## Module Manifest

```json
{
  "id": "sharpclaw_mk8shell",
  "displayName": "mk8.shell",
  "version": "1.0.0",
  "toolPrefix": "mk8",
  "entryAssembly": "SharpClaw.Modules.Mk8Shell",
  "minHostVersion": "1.0.0",
  "platforms": ["windows", "linux", "macos"],
  "executionTimeoutSeconds": 30,
  "exports": [],
  "requires": []
}
```
