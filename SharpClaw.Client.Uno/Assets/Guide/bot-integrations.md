<![CDATA[# Bot Integrations

Connect SharpClaw to external messaging platforms so your agents can interact with users via Telegram, Discord, WhatsApp, and more.

## Overview

Bot integrations require the **SharpClaw Gateway** to be running. The gateway acts as a bridge between external platforms and your internal SharpClaw API, handling incoming messages and routing them to designated channels.

When a message arrives from a bot platform:

1. The gateway receives it
2. Routes it to the configured **default channel** and **default thread**
3. The channel's agent processes the message
4. The response is sent back through the gateway to the original platform

## Supported Platforms

- **Telegram**: Full bot API support with BotFather tokens
- **Discord**: Bot integration with application tokens
- **WhatsApp**: Business API integration (requires Phone Number ID)
- **Slack**: Bot tokens for workspace integration
- **Matrix**: Homeserver URL + access token
- **Signal**: API URL + phone number
- **Email**: SMTP configuration for email-based agents
- **Teams**: Microsoft Teams app integration (requires App ID)

## Creating a Bot Integration

1. Go to **Settings** → **Bot Integrations**
2. Ensure the gateway is running (if not, start it from the **Gateway** tab)
3. Click the **+** button to expand the creation form
4. Fill in the required fields:
   - **Bot name**: A friendly name (e.g., "Production Telegram Bot")
   - **Type**: Select the platform from the dropdown
   - **Token**: Enter the bot API token
   - **Platform-specific config**: Additional fields appear based on platform type
   - **Enabled**: Toggle on to activate immediately

5. Click **Create**

The bot integration is stored in SharpClaw's database with encrypted credentials.

## Platform-Specific Configuration

### Telegram

**What you need:**
- Bot token from [@BotFather](https://t.me/BotFather)

**Token format:** `123456789:ABCdefGHIjklMNOpqrsTUVwxyz`

**No additional platform config required.**

### Discord

**What you need:**
- Bot token from Discord Developer Portal

**Token format:** Long alphanumeric string

**No additional platform config required.**

### WhatsApp

**What you need:**
- WhatsApp Business API token
- Phone Number ID

**Platform config:**
```json
{
  "PhoneNumberId": "your_phone_number_id"
}
```

### Slack

**What you need:**
- Bot User OAuth Token from Slack App settings

**Token format:** Starts with `xoxb-`

**No additional platform config required.**

### Matrix

**What you need:**
- Homeserver URL (e.g., `https://matrix.org`)
- Access token

**Platform config:**
```json
{
  "HomeserverUrl": "https://matrix.org"
}
```

### Signal

**What you need:**
- Signal API URL
- Registered phone number

**Platform config:**
```json
{
  "ApiUrl": "http://localhost:8080",
  "PhoneNumber": "+1234567890"
}
```

### Email

**What you need:**
- SMTP server details

**Platform config:**
```json
{
  "SmtpHost": "smtp.gmail.com",
  "SmtpPort": 587,
  "SmtpUsername": "your-email@gmail.com",
  "SmtpPassword": "your-app-password",
  "FromAddress": "your-email@gmail.com"
}
```

### Teams

**What you need:**
- Microsoft Teams App ID
- Bot Framework credentials

**Platform config:**
```json
{
  "AppId": "your-app-id"
}
```

## Routing Messages to Channels

Each bot integration can specify:

- **Default Channel**: Where incoming messages are routed
- **Default Thread**: Which thread within that channel receives the messages

### Setting Default Routing

1. Click on a bot integration in the list
2. In the detail view, set:
   - **Default Channel ID**: The GUID of the target channel
   - **Default Thread ID** (optional): The GUID of the target thread

If no thread is specified, messages are sent to the channel directly (one-off mode with no history).

### Creating a Dedicated Bot Channel

Best practice: create a separate channel for each bot integration.

1. Create a new channel (e.g., "Telegram Support Bot")
2. Assign an agent with appropriate permissions
3. Create a thread (e.g., "Incoming Messages")
4. Copy the channel ID and thread ID from the URL or API
5. Set them as defaults in the bot integration

## Managing Bot Integrations

### Viewing Integrations

In **Settings** → **Bot Integrations**, you'll see:

- **Name**: Friendly identifier
- **Type**: Platform (Telegram, Discord, etc.)
- **Status**: ● enabled or ○ disabled
- **🔑 icon**: Indicates a bot token is set

### Editing Integrations

Click on a bot integration to:

- Update the name
- Change the token
- Modify platform config
- Set default channel/thread
- Enable or disable the bot

### Deleting Integrations

Click the **🗑** button next to a bot integration to delete it. The gateway will stop processing messages for that platform.

## Security Considerations

- **Tokens are encrypted** at rest using AES-GCM
- Only the gateway needs access to bot tokens — your API remains isolated
- Rate limiting and IP bans are enforced by the gateway to prevent abuse
- Bot tokens are never exposed in API responses (only presence is indicated)

## Troubleshooting

**"Gateway is not running"**: Start the gateway from the **Settings** → **Gateway** tab before managing bots.

**"Gateway returned HTTP 502"**: The gateway cannot reach the internal API. Restart both the backend and gateway.

**"Bot not responding"**: Check that:
- The bot integration is **enabled**
- The bot token is valid
- The default channel and thread exist
- The channel's agent is active

**"Messages not reaching the channel"**: Verify the default channel ID and thread ID are correct. Check the gateway logs for routing errors.

**"401 Authentication failed"**: Restart both the backend and gateway to refresh API keys.

## Gateway Configuration

Bot integrations require the gateway to be running and configured properly. See **Gateway** for process lifecycle management.

## Next Steps

Continue to **Gateway** to learn how to manage the public-facing API proxy.
]]>
