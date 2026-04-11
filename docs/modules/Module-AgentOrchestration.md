# SharpClaw Module: Agent Orchestration

> **Module ID:** `sharpclaw_agent_orchestration`
> **Display Name:** Agent Orchestration
> **Version:** 1.0.0
> **Tool Prefix:** `ao`
> **Platforms:** All
> **Exports:** none
> **Requires:** none

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

Edit a task's name, repeat interval, or max retries.

**Parameters:**

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `resource_id` | string (GUID) | yes | Target task GUID |
| `name` | string | no | New name |
| `repeatIntervalMinutes` | integer | no | Minutes between repeats (0 = remove) |
| `maxRetries` | integer | no | Max retries |

**Permission:** Per-resource — requires `taskAccesses` grant.

**Aliases:** `edit_task`

---

### ao_access_skill

Retrieve a skill's instruction text.

**Parameters:**

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `resource_id` | string (GUID) | yes | Target skill GUID |

**Permission:** Per-resource — requires `skillAccesses` grant.

**Aliases:** `access_skill`

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

## Resource Dependencies

| Resource Type | Used by |
|---------------|---------|
| Agents | `ao_manage_agent`, `ao_edit_agent_header` |
| Tasks | `ao_edit_task` |
| Skills | `ao_access_skill` |
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
| `taskAccesses` | Tasks | `ao_edit_task` |
| `skillAccesses` | Skills | `ao_access_skill` |
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
  "platforms": null,
  "executionTimeoutSeconds": 30,
  "exports": [],
  "requires": []
}
```
