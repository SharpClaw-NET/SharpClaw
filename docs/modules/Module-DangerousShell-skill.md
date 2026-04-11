SharpClaw Module: Dangerous Shell — Agent Skill Reference

Module ID: sharpclaw_dangerous_shell
Display Name: Dangerous Shell
Tool Prefix: ds
Version: 1.0.0
Platforms: Windows, Linux, macOS
Exports: none
Requires: none

────────────────────────────────────────
OVERVIEW
────────────────────────────────────────
Unsandboxed real shell execution — Bash, PowerShell, CommandPrompt, Git.
Raw commands are handed directly to the interpreter. No sandboxing, no
allowlist. Safety relies entirely on permission clearance requirements.

Tools are dispatched via the module system (AgentActionType = ModuleAction).
Tool names are prefixed with "ds_" when sent to the model.

────────────────────────────────────────
ENUMS
────────────────────────────────────────
DangerousShellType: Bash (0), PowerShell (1), CommandPrompt (2), Git (3).
All four are always dangerous. Never routed through mk8.shell.

────────────────────────────────────────
TOOLS (1)
────────────────────────────────────────

ds_execute_dangerous_shell
  Execute a raw command in an unsandboxed shell interpreter.
  Params: resourceId (SystemUser GUID, required),
          shellType (string, required — "Bash"|"PowerShell"|"CommandPrompt"|"Git"),
          command (string, required — raw command string),
          workingDirectory (string, optional — CWD override)
  Permission: per-resource (DangerousShell / SystemUser)
  Timeout: 300s
  Returns: stdout or "(no output)". Throws on non-zero exit code.

Shell resolution:
  Bash         → bash -c <command>
  PowerShell   → powershell (Win) or pwsh (other) -NoProfile -NonInteractive -Command <command>
  CommandPrompt→ cmd /C <command>
  Git          → git <command split by spaces>

────────────────────────────────────────
CLI
────────────────────────────────────────
No module-specific CLI commands. Use standard job submission:
  job submit <channelId> ds_execute_dangerous_shell <systemUserId> --shell PowerShell --command "Get-Process"

────────────────────────────────────────
RESOURCE DEPENDENCIES
────────────────────────────────────────
- SystemUsers — for execute_dangerous_shell

────────────────────────────────────────
ROLE PERMISSIONS (relevant)
────────────────────────────────────────
Per-resource: dangerousShellAccesses (SystemUsers)
