# SharpClaw Module: Dangerous Shell

> **Module ID:** `sharpclaw_dangerous_shell`
> **Display Name:** Dangerous Shell
> **Version:** 1.0.0
> **Tool Prefix:** `ds`
> **Platforms:** Windows, Linux, macOS
> **Exports:** none
> **Requires:** none

---

## Overview

The Dangerous Shell module provides **unsandboxed** real shell execution
— Bash, PowerShell, CommandPrompt, and Git. The raw command string is
handed directly to the shell interpreter with **no sandboxing, no
allowlist, and no path validation**. Safety relies entirely on the
permission system's clearance requirements.

Tools are dispatched via the SharpClaw module system
(`AgentActionType = ModuleAction`). Tool names are prefixed with `ds_`
when sent to the model — for example, `execute_dangerous_shell` becomes
`ds_execute_dangerous_shell`.

> ⚠️ **This module is inherently dangerous by design.** Use restrictive
> clearance levels (e.g. `ApprovedByWhitelistedUser`) and limit
> `dangerousShellAccesses` grants to specific system users.

---

## Table of Contents

- [Enums](#enums)
- [Tools](#tools)
  - [ds_execute_dangerous_shell](#ds_execute_dangerous_shell)
- [CLI Commands](#cli-commands)
- [Resource Dependencies](#resource-dependencies)
- [Role Permissions](#role-permissions)
- [Module Manifest](#module-manifest)

---

## Enums

### DangerousShellType

| Value | Int | Description |
|-------|-----|-------------|
| `Bash` | 0 | Linux/macOS (or WSL on Windows) |
| `PowerShell` | 1 | Cross-platform via `pwsh` (or `powershell` on Windows) |
| `CommandPrompt` | 2 | Windows only (`cmd /C`) |
| `Git` | 3 | Cross-platform; command is split into git sub-command + args |

All four shell types are **always dangerous**. They are never routed
through mk8.shell and are never considered "safe". The safe counterpart
is `SafeShellType.Mk8Shell` (see the [mk8.shell module](Module-Mk8Shell.md)).

---

## Tools

### ds_execute_dangerous_shell

Execute a raw command in an unsandboxed shell interpreter. Inherently
dangerous — bypasses all sandbox restrictions. Requires clearance.

**Parameters:**

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `resourceId` | string (GUID) | yes | SystemUser GUID |
| `shellType` | string | yes | `"Bash"`, `"PowerShell"`, `"CommandPrompt"`, or `"Git"` |
| `command` | string | yes | Raw command string |
| `workingDirectory` | string | no | Optional CWD override (defaults to current directory) |

**Permission:** Per-resource — requires `dangerousShellAccesses` grant
for the target system user.

**Timeout:** 300 seconds.

**Returns:** stdout of the executed command, or `"(no output)"` if
empty. Throws on non-zero exit code with stderr.

**Shell resolution:**

| Type | Executable | Arguments |
|------|-----------|-----------|
| Bash | `bash` | `-c <command>` |
| PowerShell | `powershell` (Windows) / `pwsh` (other) | `-NoProfile -NonInteractive -Command <command>` |
| CommandPrompt | `cmd` | `/C <command>` |
| Git | `git` | command split by spaces |

---

## CLI Commands

This module does not register top-level CLI commands. Dangerous shell
jobs are submitted via the standard job pipeline:

```
job submit <channelId> ds_execute_dangerous_shell <systemUserId> --shell PowerShell --command "Get-Process"
```

---

## Resource Dependencies

| Resource Type | Used by |
|---------------|---------|
| System Users | `ds_execute_dangerous_shell` |

System users represent the OS-level identity under which the shell
process runs.

---

## Role Permissions

### Per-resource arrays

| Array | Resource Type | Tools |
|-------|---------------|-------|
| `dangerousShellAccesses` | SystemUsers | `ds_execute_dangerous_shell` |

---

## Module Manifest

```json
{
  "id": "sharpclaw_dangerous_shell",
  "displayName": "Dangerous Shell",
  "version": "1.0.0",
  "toolPrefix": "ds",
  "entryAssembly": "SharpClaw.Modules.DangerousShell",
  "minHostVersion": "1.0.0",
  "platforms": ["windows", "linux", "macos"],
  "executionTimeoutSeconds": 300,
  "exports": [],
  "requires": []
}
```
