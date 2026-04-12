# SharpClaw Module: Context Tools

> **Module ID:** `sharpclaw_context_tools`
> **Display Name:** Context Tools
> **Version:** 1.0.0
> **Tool Prefix:** `ct`
> **Platforms:** All
> **Exports:** none
> **Requires:** none

---

## How to Enable

| Setting | Value |
|---------|-------|
| **.env key** | `Modules:sharpclaw_context_tools` |
| **Default** | ❌ Disabled |
| **Prerequisites** | None |
| **Platform** | All |

To enable, add to your core `.env` (`Infrastructure/Environment/.env`) Modules section:

```jsonc
"sharpclaw_context_tools": "true"
```

To disable, set to `"false"` or remove the key (missing = disabled).

**Runtime toggle** (no restart required):

```
module disable sharpclaw_context_tools
module enable sharpclaw_context_tools
```

See [Module Enablement Guide](Module-Enablement-Guide.md) for full details.

---

## Overview

The Context Tools module provides lightweight **inline** context tools
that execute directly in the ChatService streaming loop **without
creating job records**. These tools resolve immediately and return
results to the model in the same turn.

Unlike other modules, Context Tools uses the **inline tool** pipeline
rather than the job pipeline.

---

## Table of Contents

- [Tools (Inline)](#tools-inline)
  - [ct_wait](#ct_wait)
  - [ct_list_accessible_threads](#ct_list_accessible_threads)
  - [ct_read_thread_history](#ct_read_thread_history)
- [Role Permissions](#role-permissions)

---

## Tools (Inline)

### ct_wait

Pause execution for a specified number of seconds. No permissions
required. No tokens consumed while waiting. Useful for waiting on
external processes without polling.

**Parameters:**

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `seconds` | integer | yes | Number of seconds to wait (1–300) |

**Permission:** None required.

---

### ct_list_accessible_threads

List threads from other channels that the agent can read. Returns
thread IDs, names, and parent channel info.

A channel's threads are accessible when:
- The agent is the channel's primary agent or is in `AllowedAgents`.
- The channel's effective permission set has
  `CanReadCrossThreadHistory = true`.
- If the agent's role has `Independent` clearance for this flag, the
  channel opt-in requirement is bypassed.

**Parameters:** none (empty object).

**Permission:** Requires `canReadCrossThreadHistory` on the agent's
role.

**Returns:** JSON array of
`{ threadId, threadName, channelId, channelTitle }`.

---

### ct_read_thread_history

Read conversation history from a thread in another channel. Requires
the same double-gate as `ct_list_accessible_threads`.

**Parameters:**

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `threadId` | string (GUID) | yes | Thread GUID (from `ct_list_accessible_threads`) |
| `maxMessages` | integer | no | Max messages to return (1–200, default 50) |

**Permission:** Requires `canReadCrossThreadHistory` on the agent's
role.

**Returns:** Formatted conversation history as text.

---

## Role Permissions

### Global flags

| Flag | Tools |
|------|-------|
| `canReadCrossThreadHistory` | `ct_list_accessible_threads`, `ct_read_thread_history` |

### Cross-thread access model

Reading another channel's thread history uses a **double-gate** model:

1. Agent's role permission set must have
   `CanReadCrossThreadHistory = true`.
2. Target channel's effective permission set must also have
   `CanReadCrossThreadHistory = true` (opt-in).

`Independent` clearance on the agent's role overrides the channel
opt-in requirement.
