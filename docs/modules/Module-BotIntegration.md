# SharpClaw Module: Bot Integration

> **Module ID:** `sharpclaw_bot_integration`
> **Display Name:** Bot Integration
> **Version:** 1.0.0
> **Tool Prefix:** `bi`
> **Platforms:** All
> **Exports:** none
> **Requires:** none

---

## How to Enable

| Setting | Value |
|---------|-------|
| **.env key** | `Modules:sharpclaw_bot_integration` |
| **Default** | ❌ Disabled |
| **Prerequisites** | None |
| **Platform** | All |

To enable, add to your core `.env` (`Infrastructure/Environment/.env`) Modules section:

```jsonc
"sharpclaw_bot_integration": "true"
```

To disable, set to `"false"` or remove the key (missing = disabled).

**Runtime toggle** (no restart required):

```
module disable sharpclaw_bot_integration
module enable sharpclaw_bot_integration
```

See [Module Enablement Guide](Module-Enablement-Guide.md) for full details.

---

## Overview

The Bot Integration module provides outbound bot messaging to multiple
platforms: Telegram, Discord, WhatsApp, Slack, Matrix, Signal, Email,
and Teams. It also manages bot integration CRUD (pre-seeded rows for
each `BotType`). All senders use standard HTTP or SMTP.

Bot tokens are AES-GCM encrypted at rest (same mechanism as provider
API keys).

Tools are dispatched via the SharpClaw module system
(`AgentActionType = ModuleAction`). Tool names are prefixed with `bi_`
when sent to the model.

---

## Table of Contents

- [Enums](#enums)
- [Tools](#tools)
  - [bi_send_bot_message](#bi_send_bot_message)
- [REST Endpoints](#rest-endpoints)
- [CLI Commands](#cli-commands)
- [Resource Dependencies](#resource-dependencies)
- [Role Permissions](#role-permissions)
- [Module Manifest](#module-manifest)

---

## Enums

### BotType

| Value | Int | Description |
|-------|-----|-------------|
| `Telegram` | 0 | Telegram Bot API |
| `Discord` | 1 | Discord Bot |
| `WhatsApp` | 2 | WhatsApp Business API |
| `Slack` | 3 | Slack Bot |
| `Matrix` | 4 | Matrix protocol |
| `Signal` | 5 | Signal messaging |
| `Email` | 6 | SMTP email |
| `Teams` | 7 | Microsoft Teams |

Rows are **pre-seeded** on startup for each BotType. There are no POST
or DELETE endpoints — you only update existing rows.

---

## Tools

### bi_send_bot_message

Send a direct message via a bot platform. The `recipientId` format is
platform-specific.

**Parameters:**

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `resourceId` | string (GUID) | yes | Bot integration GUID |
| `recipientId` | string | yes | Platform-specific recipient (see below) |
| `message` | string | yes | Message text to send |
| `subject` | string | no | Email subject line (email only) |

**Recipient ID formats:**

| Platform | Format |
|----------|--------|
| Telegram | Chat ID (numeric string) |
| Discord | User ID (snowflake) |
| WhatsApp | Phone number (E.164, e.g. `+1234567890`) |
| Slack | User ID |
| Matrix | User ID (`@user:server`) |
| Signal | Phone number (E.164) |
| Email | Email address |
| Teams | User ID |

**Permission:** Per-resource — requires bot integration access grant.

**Returns:** Confirmation message with bot integration ID and recipient.

---

## REST Endpoints

```
GET    /bots                       List all bot integrations
GET    /bots/{id}                  Get by ID
GET    /bots/type/{type}           Get by type name
PUT    /bots/{id}                  Update (enabled, botToken, defaultChannelId)
GET    /bots/config/{type}         Decrypted config for gateway use
```

### BotIntegrationResponse

```json
{
  "id": "guid",
  "botType": "Telegram",
  "enabled": false,
  "hasBotToken": true,
  "defaultChannelId": "guid | null",
  "createdAt": "datetime",
  "updatedAt": "datetime"
}
```

---

## CLI Commands

The module registers a top-level `bot` command:

```
bot list                                        List all bot integrations
bot get <id>                                    Show a bot integration
bot update <id> [--enabled true|false]          Enable/disable
                [--token <tok>]                 Set bot token
                [--channel <channelId>]          Set default channel
bot config <type>                               Show decrypted config
```

---

## Resource Dependencies

| Resource Type | Used by |
|---------------|---------|
| Bot Integrations | `bi_send_bot_message` |

---

## Role Permissions

### Per-resource arrays

| Array | Resource Type | Tools |
|-------|---------------|-------|
| Bot integration accesses | BotIntegrations | `bi_send_bot_message` |

---

## Module Manifest

```json
{
  "id": "sharpclaw_bot_integration",
  "displayName": "Bot Integration",
  "version": "1.0.0",
  "toolPrefix": "bi",
  "entryAssembly": "SharpClaw.Modules.BotIntegration",
  "minHostVersion": "1.0.0",
  "platforms": null,
  "executionTimeoutSeconds": 30,
  "exports": [],
  "requires": []
}
```
