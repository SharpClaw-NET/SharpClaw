SharpClaw Module: mk8.shell — Agent Skill Reference

Module ID: sharpclaw_mk8shell
Display Name: mk8.shell
Tool Prefix: mk8
Version: 1.0.0
Platforms: Windows, Linux, macOS
Exports: none
Requires: none

────────────────────────────────────────
ENABLING
────────────────────────────────────────
.env key: Modules:sharpclaw_mk8shell
Default: disabled
Prerequisites: none
Platform: Windows, Linux, macOS

To enable, add to your core .env (Infrastructure/Environment/.env) Modules section:
  "sharpclaw_mk8shell": "true"

To disable, set to "false" or remove the key (missing = disabled).

Runtime toggle (no restart required):
  module disable sharpclaw_mk8shell
  module enable sharpclaw_mk8shell

See docs/Module-Enablement-Guide.md for full details.

────────────────────────────────────────
OVERVIEW
────────────────────────────────────────
Sandboxed mk8.shell script execution and sandbox container lifecycle.
mk8.shell is a closed-verb DSL that never invokes a real shell interpreter.
Scripts go through Expand → Resolve → Sanitize → Compile before execution.

Tools are dispatched via the module system (AgentActionType = ModuleAction).
Tool names are prefixed with "mk8_" when sent to the model.

────────────────────────────────────────
ENUMS
────────────────────────────────────────
SafeShellType: Mk8Shell (0). Always safe. Never invokes real shells.

────────────────────────────────────────
TOOLS (2)
────────────────────────────────────────

mk8_execute_mk8_shell
  Execute an mk8.shell script in a registered sandbox container.
  Pipeline: Expand → Resolve → Sanitize → Compile → Execute.
  Params: resourceId (container GUID, conditional),
          sandboxId (string, conditional — sandbox name),
          script (object, required — Mk8ShellScript JSON)
  Permission: per-resource (Container)
  Timeout: 300s
  Returns: execution summary with per-step results.

mk8_create_mk8_sandbox
  Create an mk8.shell sandbox container. Registers with mk8.shell
  local registry and creates a container resource.
  Params: name (string, required — alphanumeric),
          path (string, required — base path),
          description (string, optional)
  Permission: global (CreateContainers)

────────────────────────────────────────
CLI
────────────────────────────────────────
resource container add mk8shell <name> <path>   Create an mk8shell sandbox
resource container get <id>                      Show a container
resource container list                          List all containers
resource container update <id> [description]     Update a container
resource container delete <id>                   Delete a container
resource container sync                          Import from mk8.shell registry

────────────────────────────────────────
RESOURCE DEPENDENCIES
────────────────────────────────────────
- Containers — for execute_mk8_shell, create_mk8_sandbox

────────────────────────────────────────
ROLE PERMISSIONS (relevant)
────────────────────────────────────────
Global flags: canCreateContainers
Per-resource: safeShellAccesses, containerAccesses (Containers)
