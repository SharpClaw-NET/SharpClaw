<![CDATA[# Troubleshooting

Solutions to common SharpClaw issues.

## Connection Issues

### "Backend API is not reachable"

**Symptoms:** Boot page shows connection errors, retry loop.

**Causes:**
- Backend process crashed or failed to start
- Port 48923 is already in use
- Firewall blocking localhost connections

**Solutions:**

1. **Check backend status:**
   - If the Uno client auto-starts the backend, check the Process Lifecycle settings
   - Verify `Backend:Enabled=true` in Interface .env

2. **Restart backend:**
   - Close all SharpClaw instances
   - Kill any lingering `SharpClaw.Application.API.exe` processes:
     ```powershell
     Get-Process | Where-Object { $_.ProcessName -like "*SharpClaw*" } | Stop-Process -Force
     ```
   - Relaunch the Uno client

3. **Check port availability:**
   ```powershell
   netstat -ano | findstr :48923
   ```
   If another process is using port 48923, either kill it or change `Api:ListenUrl` in Core .env.

4. **Check logs:**
   - Go to Settings → Gateway → Process Logs (if backend logs are visible there)
   - Look for startup errors (missing encryption key, database migration failures, etc.)

### "Gateway is not responding"

**Symptoms:** Bot integrations don't work, gateway status shows OFFLINE.

**Causes:**
- Gateway process not started
- Port 48924 is in use
- API key mismatch between gateway and backend

**Solutions:**

1. **Start gateway:**
   - Go to Settings → Gateway
   - Click **▶ Start**

2. **Check gateway logs:**
   - View the Process Logs section
   - Look for errors: port conflicts, API connection failures, authentication errors

3. **Restart both backend and gateway:**
   - Stop gateway (■ Stop button)
   - Restart the Uno client (this restarts the backend)
   - Start gateway again

4. **Verify .env config:**
   - Ensure `Api:ListenUrl` in Core .env matches the backend's actual bind address
   - Ensure `Gateway:Url` in Interface .env is set correctly

## Authentication Issues

### "Login failed"

**Symptoms:** Login page rejects credentials.

**Causes:**
- Incorrect username or password
- Admin account not initialized
- JWT secret mismatch

**Solutions:**

1. **Verify credentials:**
   - Double-check username and password (case-sensitive)
   - If first-time setup didn't complete, you may need to reset the database

2. **Reset admin password:**
   - Edit Core .env → `Admin:Password` → Save & Restart
   - On next boot, the admin password is updated

3. **Reset database (last resort):**
   - Go to Settings → Danger Zone (admin only)
   - Click **Reset Database**
   - **Warning:** This deletes all data (providers, agents, channels, messages)

### "Token expired"

**Symptoms:** API calls return 401, app kicks you to login.

**Causes:**
- JWT token expired (default: 24 hours)
- Refresh token rotation failed

**Solutions:**

1. **Log out and log back in:**
   - Click **Logout** in sidebar footer
   - Re-authenticate

2. **Check JWT secret consistency:**
   - Ensure `Jwt:Secret` in Core .env hasn't changed since login
   - If it changed, all existing tokens are invalidated

## Provider Issues

### "Provider sync failed"

**Symptoms:** Model sync returns an error.

**Causes:**
- Invalid API key
- API endpoint unreachable
- Rate limit exceeded
- Provider type mismatch (e.g., OpenAI key with Anthropic provider type)

**Solutions:**

1. **Verify API key:**
   - Go to Settings → Providers → click provider
   - Re-enter the API key and click **[ Set Key ]**
   - Test by syncing models again

2. **Check provider type:**
   - Ensure the provider type matches the key (OpenAI keys won't work with Anthropic provider)

3. **Test API endpoint externally:**
   ```bash
   curl -H "Authorization: Bearer YOUR_KEY" https://api.openai.com/v1/models
   ```

4. **Check rate limits:**
   - Some providers (GitHub Copilot, free tiers) have strict rate limits
   - Wait a few minutes and retry

### "Model not found"

**Symptoms:** Agent can't send messages, error says model doesn't exist.

**Causes:**
- Model was deleted
- Model ID changed
- Provider was deleted (orphaned model)

**Solutions:**

1. **Re-sync models:**
   - Go to Settings → Providers → click provider → **↻ Sync models**

2. **Reassign agent:**
   - Go to Settings → Agents → click agent
   - Select a different model from the dropdown
   - Click **Update**

3. **Check model availability:**
   - Some models are gated (e.g., GPT-4 requires approval from OpenAI)
   - Verify your API key has access to the model

## Agent Issues

### "Agent not responding"

**Symptoms:** Messages send but no response arrives, spinner hangs.

**Causes:**
- Model API is down
- Network timeout
- Agent has no model assigned
- Provider API key is missing or revoked

**Solutions:**

1. **Check agent configuration:**
   - Go to Settings → Agents → click agent
   - Verify a model is selected

2. **Check model status:**
   - Go to Settings → Models
   - Verify the model's provider has a valid API key

3. **Test provider connectivity:**
   - Go to Settings → Providers → sync models
   - If sync fails, the provider is unreachable

4. **Check for API outages:**
   - Visit status pages: [OpenAI Status](https://status.openai.com), [Anthropic Status](https://status.anthropic.com)

### "Permission denied" errors in chat

**Symptoms:** Agent tries to execute an action but gets denied.

**Causes:**
- Agent doesn't have the required permission
- Clearance level is PendingApproval but job wasn't approved
- Resource doesn't exist or is inaccessible

**Solutions:**

1. **Check agent role:**
   - Go to Settings → Agents → click agent → **Role** tab
   - Verify the agent has a role assigned

2. **Check role permissions:**
   - Go to Settings → Roles → click role
   - Grant the required permission (e.g., SafeShellAccesses for `execute_shell`)

3. **Approve jobs:**
   - Go to channel → **[jobs]** tab
   - Approve any jobs in **AwaitingApproval** status

4. **Set clearance to Independent (if trusted):**
   - Go to Settings → Roles → click role → **Permissions** tab
   - Set each action's clearance to **Independent** for auto-approval

## Job Issues

### "Job stuck in Queued"

**Symptoms:** Job never transitions to Executing.

**Causes:**
- Job orchestrator not running
- Resource is inaccessible
- Database transaction deadlock

**Solutions:**

1. **Refresh the channel:**
   - Switch to another channel and back
   - Force-refresh the job list

2. **Restart backend:**
   - Close Uno client
   - Relaunch

3. **Cancel and resubmit:**
   - Click job → **Cancel**
   - Agent re-submits the action

### "Job failed immediately"

**Symptoms:** Job goes from Queued → Failed with an error.

**Causes:**
- Resource doesn't exist (e.g., container ID is invalid)
- Misconfigured resource (e.g., shell sandbox not initialized)
- Permission check failed

**Solutions:**

1. **Check job logs:**
   - Click the job to view its execution log
   - Read the error message (usually very specific)

2. **Verify resource:**
   - If the job references a resource (container, shell, audio device), verify it exists in Settings → Advanced

3. **Check default resources:**
   - Go to channel settings → **Default Resources**
   - Ensure defaults are set for the action type

## Process Lifecycle Issues

### "Processes don't persist on exit"

**Symptoms:** Backend/gateway stop when you close the Uno client, even with persistent mode enabled.

**Causes:**
- Persistent mode toggle isn't saved
- Environment variable override

**Solutions:**

1. **Verify persistent mode is ON:**
   - Go to Settings → Gateway → **Process Lifecycle**
   - Ensure toggle shows **Keep processes running on exit** (green)

2. **Check Interface .env:**
   - Go to >env → Application Interface
   - Verify `Processes:Persistent=true`

3. **Test by checking process list:**
   - Close Uno client
   - Run `Get-Process | Where-Object { $_.ProcessName -like "*SharpClaw*" }`
   - Backend and gateway should still be running

### "Auto-start doesn't work on Windows login"

**Symptoms:** Processes don't launch automatically when you log in.

**Causes:**
- Auto-start toggle not enabled
- Startup scripts not refreshed (MSIX path changed)

**Solutions:**

1. **Enable auto-start:**
   - Go to Settings → Gateway → **Process Lifecycle**
   - Toggle **Launch at Windows startup** to ON

2. **Refresh startup scripts:**
   - Close and reopen the Uno client (scripts are refreshed on shutdown)
   - Check `shell:startup` folder for `.vbs` files

3. **Check script paths:**
   - Open the startup folder: Win+R → `shell:startup` → Enter
   - Open the `.vbs` files in a text editor
   - Verify the paths point to valid executables

## Chat Issues

### "Messages not sending"

**Symptoms:** Message input is grayed out or send button doesn't respond.

**Causes:**
- No agent assigned to channel
- Backend disconnected
- Channel context is invalid

**Solutions:**

1. **Check channel agent:**
   - Go to channel settings → **Agent** tab
   - Ensure an agent is selected

2. **Reconnect to backend:**
   - Refresh the app (Ctrl+R)
   - If that fails, restart the Uno client

3. **Check channel validity:**
   - If the channel was deleted from another client, it may be stale
   - Create a new channel

### "Chat history is empty"

**Symptoms:** Previous messages don't appear in chat.

**Causes:**
- Thread is newly created (no messages yet)
- History limit exceeded (oldest messages were trimmed)
- Database query error

**Solutions:**

1. **Check thread history limits:**
   - Go to channel settings → **Threads** → click thread
   - Verify MaxMessages and MaxCharacters are reasonable (not 0)

2. **Switch between one-off and thread mode:**
   - If using a thread, try switching to "No Thread" (one-off mode)
   - If in one-off mode, create a thread

3. **Verify backend logs:**
   - Check for database errors in the backend logs

### "Cost tracking shows $0.00"

**Symptoms:** Token usage isn't accumulating.

**Causes:**
- Provider doesn't report token usage (rare)
- Model doesn't support usage metadata
- Streaming response parsing failed

**Solutions:**

1. **Check provider type:**
   - Most major providers (OpenAI, Anthropic, etc.) report usage
   - Custom providers may not implement usage tracking

2. **Verify model capabilities:**
   - Some embedding-only models don't track chat usage

3. **Test with a known-good model:**
   - Send a message with `gpt-4o` or `claude-3-5-sonnet-20241022`
   - These models always report usage

## Bot Integration Issues

### "Bot not receiving messages"

**Symptoms:** Messages sent to Telegram/Discord bot don't reach SharpClaw.

**Causes:**
- Bot integration disabled
- Gateway not running
- Webhook not configured (platform-side)
- Default channel/thread not set

**Solutions:**

1. **Verify bot is enabled:**
   - Go to Settings → Bot Integrations
   - Ensure status shows **● enabled** (green)

2. **Check gateway status:**
   - Go to Settings → Gateway
   - Ensure status shows **● RUNNING**

3. **Set default routing:**
   - Click bot integration → set **Default Channel ID** and **Default Thread ID**

4. **Check platform webhook config:**
   - Telegram: Ensure webhook URL is set via BotFather
   - Discord: Verify bot has Message Content Intent enabled

### "Bot responses are delayed"

**Symptoms:** Bot replies take several seconds to arrive.

**Causes:**
- High model latency
- Gateway rate limiting
- Network latency

**Solutions:**

1. **Use faster models:**
   - Switch to `gpt-4o-mini`, `claude-3-haiku`, or `groq/llama` for lower latency

2. **Check gateway rate limits:**
   - Rate limits may be throttling responses
   - View gateway logs for "429" errors

3. **Test provider latency:**
   - Send a message directly in the Uno client to isolate the issue
   - If slow there too, it's the provider/model, not the gateway

## Environment Configuration Issues

### "Cannot edit Core .env"

**Symptoms:** Settings → >env → Application Core shows "Access Denied".

**Causes:**
- User is not admin
- `EnvEditor:AllowNonAdmin=false` in Core .env

**Solutions:**

1. **Log in as admin:**
   - Use the admin credentials configured during first-time setup

2. **Enable non-admin access (as admin):**
   - Edit Core .env → `EnvEditor:AllowNonAdmin=true` → Save & Restart

### "Changes don't persist after restart"

**Symptoms:** .env edits revert after closing the app.

**Causes:**
- File write permissions
- .env file locked by another process
- Changes not saved before restart

**Solutions:**

1. **Always click Save & Restart:**
   - Don't manually edit the file while the app is running
   - Use the built-in editor and click the save button

2. **Check file permissions:**
   - Ensure the .env file isn't read-only
   - Run as administrator if needed (Windows)

3. **Close other editors:**
   - If you have the .env file open in VS Code or another editor, close it

## Next Steps

If your issue isn't listed here:

- Check the **[jobs]** or **[tasks]** logs for detailed error messages
- View backend/gateway process logs in Settings → Gateway
- Report bugs via the **Report Issue** button (sidebar footer)
- Join the Matrix community for real-time help
]]>
