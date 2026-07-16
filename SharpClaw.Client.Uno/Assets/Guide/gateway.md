<![CDATA[# Gateway

The SharpClaw Gateway is a public-facing API proxy that handles security, rate limiting, caching, and bot integrations.

## What is the Gateway?

The gateway sits between external clients and your internal SharpClaw API:

```
External Clients → Gateway → Internal API → Agents
```

It provides:

- **Security**: IP-based bans, anti-spam, rate limiting
- **Bot Integrations**: Routes messages from Telegram, Discord, WhatsApp, etc.
- **Caching**: Reduces redundant API calls
- **Isolation**: Keeps your internal API protected

## Gateway vs. Backend

- **Backend** (SharpClaw.Runtime.Host): Internal API that powers the Uno client, CLI, and core logic
- **Gateway** (SharpClaw.Gateway): Public-facing proxy for external access and bot integrations

Both processes can run independently:

- **Backend only**: Use the Uno client and CLI locally
- **Backend + Gateway**: Enable bot integrations and external API access

## Process Lifecycle

The gateway is an optional process managed by the Uno client.

### Starting the Gateway

1. Go to **Settings** → **Gateway**
2. Check the status indicator:
   - **● RUNNING**: Gateway is active
   - **○ OFFLINE**: Gateway is not running
   - **○ NOT ENABLED**: Gateway launch is disabled

3. Click **▶ Start** to launch the bundled gateway executable

The gateway starts in the background and listens on `http://0.0.0.0:48924` by default.

### Stopping the Gateway

Click **■ Stop** to terminate the gateway process. All active bot integrations and external connections are closed.

### Restarting the Gateway

Click **↻ Restart** to stop and immediately restart the gateway. Useful for applying configuration changes.

### Refreshing Status

Click **⟳ Refresh** to probe the gateway's health and update the status indicator.

## Process Lifecycle Settings

The **Process Lifecycle** card controls how the gateway behaves when the Uno client shuts down.

### Persistent Mode

When **persistent mode** is enabled:

- The gateway keeps running when you close the Uno client
- On next launch, the client detects the external process and reattaches
- Useful for unattended operation (e.g., running bot integrations 24/7)

When **persistent mode** is disabled:

- The gateway is killed when you close the Uno client
- Next launch starts a fresh gateway process

**Toggle:** Click the **Keep processes running on exit** switch in the Process Lifecycle card.

### Windows Auto-Start

On Windows, you can register the gateway to auto-launch at login:

- **Enabled**: A startup script is created in `shell:startup` (works with both MSIX and unpackaged deployments)
- **Disabled**: The gateway only runs when you manually start it

**Toggle:** Click the **Launch at Windows startup** switch in the Process Lifecycle card.

**Note:** Startup scripts are refreshed automatically on every app shutdown to handle MSIX version-path changes.

## Gateway Logs

The **Process Logs** section displays real-time output from the gateway:

- **Copy All**: Copy the entire log to clipboard
- **Clear**: Wipe the log buffer
- **Auto-refresh**: Logs update every second while the gateway is running

**Log line count** is displayed next to the section header.

## Gateway URL

The gateway binds to `http://0.0.0.0:48924` by default. This is the server bind address.

Clients should connect to `http://127.0.0.1:48924` (localhost) when accessing from the same machine.

## External vs. Bundled Gateway

The Uno client checks if the gateway is already running before attempting to launch it:

- **External**: The gateway was started manually (e.g., via `dotnet run` during development) — the status indicator shows **● RUNNING (external)**
- **Bundled**: The Uno client launched the gateway from its bundled executable

When an external gateway is detected:

- The **■ Stop** button is disabled (you don't own the process)
- The client reattaches for log viewing and health checks

## Enabling/Disabling Gateway Launch

Gateway launch is controlled by the **Backend:Enabled** setting in the client's `.env` file:

1. Go to **Settings** → click the **>env** button in the sidebar footer
2. Select **Client Interface** (client .env)
3. Navigate to the **Gateway** section
4. Toggle **Enabled**
5. Click **Save & Restart**

When disabled:

- The **▶ Start** button is hidden
- You must start the gateway externally (e.g., via CLI or another deployment)

## Gateway Configuration

The gateway reads its configuration from:

- Internal API key (auto-forwarded by the Uno client)
- Core `.env` file (API listen URL, connection strings, etc.)
- Bot integration database (encrypted tokens and routing rules)

## Security Features

### IP-Based Bans

After 10 violations within 5 minutes, the gateway bans the offending IP for 1 hour.

Violations include:

- Excessive requests (rate limit breaches)
- Invalid payloads
- Missing required headers

### Rate Limiting

- **Global**: 60 requests/minute per IP (sliding window)
- **Auth endpoints**: 5 requests/minute per IP (fixed window)
- **Chat endpoints**: 20 requests/minute per IP (sliding window)

### Anti-Spam

Requests larger than 64KB or missing `Content-Type` are rejected immediately.

## Troubleshooting

**"Gateway executable not found"**: The bundled gateway wasn't published with the Uno app. Publish with `/p:BundleGateway=true` or run the gateway externally.

**"Gateway is not responding"**: Check the Process Logs for errors. Common issues:
- Port 48924 is already in use
- Backend API is unreachable
- API key mismatch

**"Gateway process exited with code X"**: View the Process Logs for the error message. Common exit codes:
- 1: Unhandled exception
- 137: Killed by OS (out of memory)

**"Bot messages not arriving"**: Verify:
- The gateway is running
- Bot integration is enabled
- Default channel and thread are set correctly
- Firewall allows incoming connections on the bot webhook port

**"401 Authentication failed"**: The gateway's API key doesn't match the backend. Restart both processes.

## Advanced: External Gateway Deployment

For production scenarios, deploy the gateway as a standalone service:

1. Publish `SharpClaw.Gateway` as a self-contained executable
2. Configure the Core `.env` file with API listen URL and secrets
3. Set `Backend__Enabled="false"` in the client `.env` to prevent auto-launch
4. Run the gateway as a systemd/Windows service
5. Configure reverse proxy (nginx, Caddy) for HTTPS and public access

## Next Steps

Continue to **Advanced Topics** to explore tool awareness, custom headers, and environment configuration.
]]>
