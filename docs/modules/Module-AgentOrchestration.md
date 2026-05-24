# SharpClaw Module: Agent Orchestration

> **Module ID:** `sharpclaw_agent_orchestration`
> **Display Name:** Agent Orchestration
> **Version:** 1.0.0
> **Tool Prefix:** `ao`
> **Platforms:** All
> **Exports:** none
> **Requires:** none

---

## How to Enable

| Setting | Value |
|---------|-------|
| **.env key** | `Modules:sharpclaw_agent_orchestration` |
| **Default** | ❌ Disabled |
| **Prerequisites** | None |
| **Platform** | All |

To enable, add to your core `.env` (`Infrastructure/Environment/.env`) Modules section:

```jsonc
"sharpclaw_agent_orchestration": "true"
```

To disable, set to `"false"` or remove the key (missing = disabled).

**Runtime toggle** (no restart required):

```
module disable sharpclaw_agent_orchestration
module enable sharpclaw_agent_orchestration
```

See [Module Enablement Guide](Module-Enablement-Guide.md) for full details.

---

## Overview

The Agent Orchestration module provides agent lifecycle management
(create sub-agents, manage agents), task editing, skill access, and
custom chat header editing. All tools flow through the standard job
pipeline.

Tools are dispatched via the SharpClaw module system
(`AgentActionType = ModuleAction`). Tool names are prefixed with `ao_`
when sent to the model — for example, `manage_agent` becomes
`ao_manage_agent`.

---

## Table of Contents

- [Tools](#tools)
  - [ao_create_sub_agent](#ao_create_sub_agent)
  - [ao_manage_agent](#ao_manage_agent)
  - [ao_edit_task](#ao_edit_task)
  - [ao_access_skill](#ao_access_skill)
  - [ao_edit_agent_header](#ao_edit_agent_header)
  - [ao_edit_channel_header](#ao_edit_channel_header)
- [Module-owned CLI resources](#module-owned-cli-resources)
- [Resource Dependencies](#resource-dependencies)
- [Role Permissions](#role-permissions)
- [Module Manifest](#module-manifest)

---

## Tools

### ao_create_sub_agent

Create a sub-agent with permissions ≤ the creator's.

**Parameters:**

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `name` | string | yes | Agent name |
| `modelId` | string (GUID) | yes | Model GUID |
| `systemPrompt` | string | no | System prompt |

**Permission:** Global — requires `canCreateSubAgents` flag.

---

### ao_manage_agent

Update an agent's name, systemPrompt, or modelId.

**Parameters:**

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `resource_id` | string (GUID) | yes | Target agent GUID |
| `name` | string | no | New name |
| `systemPrompt` | string | no | New system prompt |
| `modelId` | string (GUID) | no | New model GUID |

**Permission:** Per-resource — requires `agentAccesses` grant.

**Aliases:** `manage_agent`

---

### ao_edit_task

Edit an Agent Orchestration task's name, repeat interval, or max retries.
This tool targets the module-owned `AoTask` resource, not the Core host task
scheduler's `task schedule` rows.

**Parameters:**

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `resource_id` | string (GUID) | yes | Target task GUID |
| `name` | string | no | New name |
| `repeatIntervalMinutes` | integer | no | Minutes between repeats (0 = remove) |
| `maxRetries` | integer | no | Max retries |

**Permission:** Per-resource — requires an `AoTask` resource grant.

**Aliases:** `edit_task`

Create testable target rows with the module CLI:

```text
resource aotask add <name> [--next-run <timestamp>] [--repeat-minutes <n>] [--max-retries <n>]
```

---

### ao_access_skill

Retrieve an Agent Orchestration skill's instruction text. This tool targets the
module-owned `AoSkill` resource.

**Parameters:**

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `resource_id` | string (GUID) | yes | Target skill GUID |

**Permission:** Per-resource — requires an `AoSkill` resource grant.

**Aliases:** `access_skill`

Create testable target rows with the module CLI:

```text
resource aoskill add <name> --text <skillText> [--description <description>]
```

---

### ao_edit_agent_header

Set or clear the custom chat header for an agent.

**Parameters:**

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `resource_id` | string (GUID) | yes | Target agent GUID |
| `header` | string | no | Template string (empty string to clear) |

**Permission:** Per-resource — requires `agentHeaderAccesses` grant.

**Aliases:** `edit_agent_header`

---

### ao_edit_channel_header

Set or clear the custom chat header for a channel.

**Parameters:**

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `resource_id` | string (GUID) | yes | Target channel GUID |
| `header` | string | no | Template string (empty string to clear) |

**Permission:** Per-resource — requires `channelHeaderAccesses` grant.

**Aliases:** `edit_channel_header`

---

## Task-script Steps

Agent Orchestration owns the **statement primitives** of the task script
grammar plus the agent/entity step methods. None of these live in the core
parser — they are contributed via `TaskScriptingParserExtension` and
`AgentOrchestrationStepDescriptorProvider`.

### Statement primitives

The parser emits these directly from script syntax (variables, assignments,
event handlers, control flow, etc.):

| Primitive | Wire step key |
|-----------|---------------|
| `DeclareVariable` | `task_scripting.declare_variable` |
| `Assign` | `task_scripting.assign` |
| `EventHandler` | `task_scripting.event_handler` |
| `Conditional` | `task_scripting.conditional` |
| `Loop` | `task_scripting.loop` |
| `Return` | `task_scripting.return` |
| `Delay` | `task_scripting.delay` |
| `Evaluate` | `task_scripting.evaluate` |
| `Log` | `task_scripting.log` |
| `ParseResponse` | `task_scripting.parse_response` |

### Step methods

Method calls in the task body are dispatched via the central
`TaskStepRegistry`. Agent Orchestration registers the following method names:

| Method | Purpose |
|--------|---------|
| `Chat` | Send a chat turn to an agent and capture the response. |
| `ChatStream` | Stream a chat turn to an agent. |
| `ChatToThread` | Send a chat turn into a specific thread. |
| `Emit` | Emit a structured output value from the task. |
| `ParseResponse` | Parse a previous response into a typed shape (generic capture). |
| `FindModel` | Resolve a model by name / id. |
| `FindProvider` | Resolve a provider by name / id. |
| `FindAgent` | Resolve an agent by name / id. |
| `CreateAgent` | Provision a new agent. |
| `CreateThread` | Provision a new thread. |
| `CreateRole` | Provision a new role. |
| `FindRole` | Resolve a role by name / id. |
| `SetRolePermissions` | Update a role's permission set. |
| `AssignRole` | Assign a role to an agent. |
| `CreateChannel` | Provision a new channel. |
| `FindChannel` | Resolve a channel by name / id. |
| `AddAllowedAgent` | Add an allowed agent to a channel. |

When this module is disabled, scripts that use these methods or the
statement primitives will be rejected by `task preflight`.

---

## Triggers

Agent Orchestration owns the lifecycle, scripting, event-bus, task-chain,
and filesystem trigger attributes. Each is registered through
`AgentOrchestrationTriggerAttributeHandlers` and surfaced as a runtime
`ITaskTriggerSource`.

| Attribute | Trigger source | Notes |
|-----------|----------------|-------|
| `[Schedule(...)]` | Cron scheduler | Cron expression + optional timezone. |
| `[OnStartup]` | `LifecycleTriggerSource` | Fires once at host startup. |
| `[OnShutdown]` | `LifecycleTriggerSource` | Fires once at host shutdown. |
| `[OnTaskCompleted]` | `TaskChainTriggerSource` | Fires when a named source task completes. |
| `[OnTaskFailed]` | `TaskChainTriggerSource` | Fires when a named source task fails. |
| `[OnTrigger(name)]` | Custom dispatch | Routes to an arbitrary named trigger key. |
| `[OnEvent(type, Filter)]` | `EventBusTriggerSource` | Fires on internal event-bus messages. |
| `[OnFileChanged(path, Pattern, Events)]` | `FileChangedTriggerSource` | Fires on filesystem changes. |
| `OnTimer` event handler | `LifecycleTriggerSource` | In-script timer event handler (key `sharpclaw.task_scripting.timer`). |

If this module is disabled, tasks using these triggers are flagged by
`task preflight` and removed from `task trigger-sources`.

---

## Module-owned CLI resources

Agent Orchestration owns the resources used by `edit_task` and `access_skill`.
Use these commands under the Core CLI's module-resource dispatch surface:

### AoTask

`AoTask` resources live in the Agent Orchestration module's persistent
scheduled-job store. The store is owned by the module and follows it into the
sidecar process, while Core host task definitions and task schedule commands
remain separate.

```text
resource aotask add <name> [--next-run <timestamp>] [--repeat-minutes <n>] [--max-retries <n>]
resource aotask get <id>
resource aotask list
resource aotask update <id> [--name <name>] [--repeat-minutes <n>] [--max-retries <n>]
resource aotask delete <id>
```

Alias: `resource aot ...`

Use `resource aotask add` to create a target resource before testing
`job submit <channelId> edit_task <aotaskId> ...`.

### AoSkill

`AoSkill` resources live in the Agent Orchestration module's persistent skill
store. The skill text is still retrieved through the module-owned
`access_skill` tool, but it no longer depends on an in-process EF context.

```text
resource aoskill add <name> --text <skillText> [--description <description>]
resource aoskill get <id>
resource aoskill list
resource aoskill update <id> [--name <name>] [--description <description>] [--text <skillText>]
resource aoskill delete <id>
```

Alias: `resource aos ...`

Use `resource aoskill add` to create a target resource before testing
`job submit <channelId> access_skill <aoskillId> ...`.

---

## Resource Dependencies

| Resource Type | Used by |
|---------------|---------|
| Agents | `ao_manage_agent`, `ao_edit_agent_header` |
| `AoTask` | `ao_edit_task` |
| `AoSkill` | `ao_access_skill` |
| Channels | `ao_edit_channel_header` |

---

## Role Permissions

### Global flags

| Flag | Tools |
|------|-------|
| `canCreateSubAgents` | `ao_create_sub_agent` |
| `canEditAgentHeader` | `ao_edit_agent_header` |
| `canEditChannelHeader` | `ao_edit_channel_header` |

### Per-resource arrays

| Array | Resource Type | Tools |
|-------|---------------|-------|
| `agentAccesses` | Agents | `ao_manage_agent` |
| `AoTask` grants | Agent Orchestration tasks | `ao_edit_task` |
| `AoSkill` grants | Agent Orchestration skills | `ao_access_skill` |
| `agentHeaderAccesses` | Agents | `ao_edit_agent_header` |
| `channelHeaderAccesses` | Channels | `ao_edit_channel_header` |

---

## Module Manifest

```json
{
  "id": "sharpclaw_agent_orchestration",
  "displayName": "Agent Orchestration",
  "version": "1.0.0",
  "toolPrefix": "ao",
  "entryAssembly": "SharpClaw.Modules.AgentOrchestration.dll",
  "minHostVersion": "1.0.0",
  "license": "AGPL-3.0",
  "platforms": null,
  "executionTimeoutSeconds": 30,
  "exports": [],
  "requires": []
}
```
