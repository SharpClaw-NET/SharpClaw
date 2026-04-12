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
  Edit task name, interval, or retries.
  Params: resource_id (task GUID, required),
          name (string, optional),
          repeatIntervalMinutes (int, optional — 0=remove),
          maxRetries (int, optional)
  Permission: per-resource (Task)

ao_access_skill (alias: access_skill)
  Retrieve a skill's instruction text.
  Params: resource_id (skill GUID, required)
  Permission: per-resource (Skill)

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
RESOURCE DEPENDENCIES
────────────────────────────────────────
- Agents — for manage_agent, edit_agent_header
- Tasks — for edit_task
- Skills — for access_skill
- Channels — for edit_channel_header

────────────────────────────────────────
ROLE PERMISSIONS (relevant)
────────────────────────────────────────
Global flags: canCreateSubAgents, canEditAgentHeader, canEditChannelHeader
Per-resource: agentAccesses, taskAccesses, skillAccesses,
  agentHeaderAccesses, channelHeaderAccesses
