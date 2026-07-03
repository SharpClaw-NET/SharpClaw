SharpClaw Module: Agent Orchestration — Agent Skill Reference

Module ID: sharpclaw_agent_orchestration
Display Name: Agent Orchestration
Tool Prefix: ao
Version: 1.0.0
Platforms: All
Exports: none
Requires: none

────────────────────────────────────────
ENABLING
────────────────────────────────────────
.env key: Modules:sharpclaw_agent_orchestration
Default: disabled
Prerequisites: none
Platform: All

To enable, add to your core .env (Infrastructure/Environment/.env) Modules section:
  "sharpclaw_agent_orchestration": "true"

To disable, set to "false" or remove the key (missing = disabled).

Runtime toggle (no restart required):
  module disable sharpclaw_agent_orchestration
  module enable sharpclaw_agent_orchestration

See docs/Module-Enablement-Guide.md for full details.

────────────────────────────────────────
OVERVIEW
────────────────────────────────────────
Agent lifecycle management: sub-agent creation, agent management, task
editing, skill access, and custom chat header editing. All tools flow
through the standard job pipeline.

Tools are dispatched via the module system (AgentActionType = ModuleAction).
Tool names are prefixed with "ao_" when sent to the model.

────────────────────────────────────────
TOOLS (6)
────────────────────────────────────────

ao_create_sub_agent
  Create a sub-agent (permissions ≤ creator's).
  Params: name (string, required), modelId (GUID, required),
          systemPrompt (string, optional)
  Permission: global (CreateSubAgents)

ao_manage_agent (alias: manage_agent)
  Update agent name, systemPrompt, or modelId.
  Params: resource_id (agent GUID, required),
          name (string, optional), systemPrompt (string, optional),
          modelId (GUID, optional)
  Permission: per-resource (Agent)

ao_edit_task (alias: edit_task)
  Edit Agent Orchestration task name, interval, or retries.
  Params: resource_id (AoTask GUID, required),
          name (string, optional),
          repeatIntervalMinutes (int, optional — 0=remove),
          maxRetries (int, optional)
  Permission: per-resource (AoTask)

ao_access_skill (alias: access_skill)
  Retrieve an Agent Orchestration skill's instruction text.
  Params: resource_id (AoSkill GUID, required)
  Permission: per-resource (AoSkill)

ao_edit_agent_header (alias: edit_agent_header)
  Set or clear the custom chat header for an agent.
  Params: resource_id (agent GUID, required),
          header (string, optional — empty to clear)
  Permission: per-resource (AgentHeader)

ao_edit_channel_header (alias: edit_channel_header)
  Set or clear the custom chat header for a channel.
  Params: resource_id (channel GUID, required),
          header (string, optional — empty to clear)
  Permission: per-resource (ChannelHeader)

────────────────────────────────────────
TASK-SCRIPT OPERATIONS
────────────────────────────────────────
Core owns ordinary C# task-language statements: declaration, assignment,
control flow, return, logging, delay, structured response parsing, and
cancellation waits. This module contributes real host operations registered
with TaskStepRegistry:
  Chat, ChatStream, ChatToThread        — agent chat
  Emit, ParseResponse                   — output / parsing
  FindModel, FindProvider, FindAgent    — entity lookup
  CreateAgent, CreateThread             — entity creation
  CreateRole, FindRole,
    SetRolePermissions, AssignRole      — roles / permissions
  CreateChannel, FindChannel,
    AddAllowedAgent                     — channels

If this module is disabled, scripts using these module operations or its
triggers are rejected by `task preflight`. Pure C# control flow stays valid
because it is Core-owned.

────────────────────────────────────────
TRIGGERS
────────────────────────────────────────
Schedule           — cron scheduler (cron expression + timezone)
OnStartup          — LifecycleTriggerSource (host startup)
OnShutdown         — LifecycleTriggerSource (host shutdown)
OnTaskCompleted    — TaskChainTriggerSource (source task completes)
OnTaskFailed       — TaskChainTriggerSource (source task fails)
OnTrigger(name)    — custom dispatch to arbitrary trigger key
OnEvent(type, ...) — EventBusTriggerSource (internal event bus)
OnFileChanged(...) — FileChangedTriggerSource (filesystem watch)
OnTimer (handler)  — LifecycleTriggerSource in-script timer
                     (key: sharpclaw.task_scripting.timer)

If this module is disabled, these triggers are flagged by `task preflight`
and removed from `task trigger-sources`.

────────────────────────────────────────
MODULE-OWNED CLI RESOURCES
────────────────────────────────────────
AoTask resources live in the Agent Orchestration module's persistent
scheduled-job store. The store is owned by the module and follows it into the
sidecar process, separately from Core task schedule rows.

  resource aotask add <name> [--next-run <timestamp>] [--repeat-minutes <n>] [--max-retries <n>]
  resource aotask get <id>
  resource aotask list
  resource aotask update <id> [--name <name>] [--repeat-minutes <n>] [--max-retries <n>]
  resource aotask delete <id>

Alias: resource aot ...

AoSkill resources live in the Agent Orchestration module's persistent skill
store. The skill text is still retrieved through the module-owned
access_skill tool, but it no longer depends on an in-process EF context.

  resource aoskill add <name> --text <skillText> [--description <description>]
  resource aoskill get <id>
  resource aoskill list
  resource aoskill update <id> [--name <name>] [--description <description>] [--text <skillText>]
  resource aoskill delete <id>

Alias: resource aos ...

────────────────────────────────────────
RESOURCE DEPENDENCIES
────────────────────────────────────────
- Agents — for manage_agent, edit_agent_header
- AoTask — for edit_task
- AoSkill — for access_skill
- Channels — for edit_channel_header

────────────────────────────────────────
ROLE PERMISSIONS (relevant)
────────────────────────────────────────
Global flags: canCreateSubAgents, canEditAgentHeader, canEditChannelHeader
Per-resource: agentAccesses, taskAccesses, skillAccesses,
  agentHeaderAccesses, channelHeaderAccesses
