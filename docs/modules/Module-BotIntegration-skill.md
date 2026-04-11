SharpClaw Module: Bot Integration — Agent Skill Reference

Module ID: sharpclaw_bot_integration
Display Name: Bot Integration
Tool Prefix: bi
Version: 1.0.0
Platforms: All
Exports: none
Requires: none

────────────────────────────────────────
OVERVIEW
────────────────────────────────────────
Outbound bot messaging to Telegram, Discord, WhatsApp, Slack, Matrix,
Signal, Email, and Teams. Bot integration CRUD with pre-seeded rows.
Bot tokens are AES-GCM encrypted at rest.

────────────────────────────────────────
ENUMS
────────────────────────────────────────
BotType: Telegram (0), Discord (1), WhatsApp (2), Slack (3),
  Matrix (4), Signal (5), Email (6), Teams (7).

Rows pre-seeded on startup. No POST/DELETE — only UPDATE.

────────────────────────────────────────
TOOLS (1)
────────────────────────────────────────

bi_send_bot_message
  Send DM via bot platform.
  Params: resourceId (bot integration GUID, required),
          recipientId (string, required — platform-specific),
          message (string, required),
          subject (string, optional — email only)
  Permission: per-resource (BotIntegration)
  Recipient formats: Telegram=chat ID, Discord=user ID,
    WhatsApp/Signal=E.164 phone, Slack=user ID,
    Matrix=@user:server, Email=address, Teams=user ID.

────────────────────────────────────────
REST ENDPOINTS
────────────────────────────────────────
GET    /bots                       List all
GET    /bots/{id}                  Get by ID
GET    /bots/type/{type}           Get by type name
PUT    /bots/{id}                  Update (enabled, botToken, defaultChannelId)
GET    /bots/config/{type}         Decrypted config

────────────────────────────────────────
CLI
────────────────────────────────────────
bot list                            List all bot integrations
bot get <id>                        Show a bot integration
bot update <id> [--enabled] [--token] [--channel]
bot config <type>                   Show decrypted config

────────────────────────────────────────
RESOURCE DEPENDENCIES
────────────────────────────────────────
- BotIntegrations — for send_bot_message

────────────────────────────────────────
ROLE PERMISSIONS (relevant)
────────────────────────────────────────
Per-resource: bot integration accesses (BotIntegrations)
