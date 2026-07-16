<![CDATA[# Chat Features

Master the chat interface, streaming, attachments, and inline tools.

## Chat Interface

The main chat area displays:

- **Message bubbles**: User and assistant messages with timestamps
- **Streaming indicators**: Real-time text generation
- **Tool call markers**: Server-authoritative notation for agent actions
- **Cost tracking**: Token usage displayed above input

## Sending Messages

### Basic Sending

1. Type your message in the input box
2. Press **Enter** or click **Send**

### Multi-Line Messages

- **Shift + Enter**: Insert a new line without sending
- **Enter**: Send the message

### Message History

- Scroll up in the chat area to view previous messages
- Thread history is loaded automatically when selecting a thread
- One-off mode shows no history

## Streaming Responses

All agent responses stream in real-time:

- Text appears word-by-word as the model generates it
- Tool calls are annotated inline with `⚙ [ActionType] → Status`
- Streaming continues until the agent finishes or is interrupted

**Streaming is automatic** — no configuration needed.

## Chat Headers

Each user message is prefixed with metadata:

```
[time: 2025-01-14 03:45:22 UTC | user: admin | via: UnoWindows | role: Admin (all permissions) | bio: System administrator | agent-role: Developer (SafeShell, ManageAgent)]
```

This gives the agent context about:

- **When** the message was sent
- **Who** sent it
- **What platform** (CLI, API, Telegram, Discord, UnoWindows, etc.)
- **User role and permissions**
- **User bio** (optional profile text)
- **Agent's own role and permissions** (self-awareness)
- **Accessible threads** from other channels (if cross-thread access is enabled)

### Disabling Headers

To remove headers:

1. Open channel settings
2. Toggle **Disable Chat Header** → On

This is useful for agents that don't need metadata or when testing prompts.
For an instance-wide switch, set `Chat__DisableDefaultHeaders="true"` in the
Core `.env`; this disables generated metadata headers but does not suppress
explicit agent or channel custom headers. Set
`Chat__DisableDefaultSystemPrompt="true"`
to remove the core native-tool instruction suffix while preserving agent system
prompts.

### Custom Headers

Override the default header template:

1. Edit an agent or channel
2. Set **Custom Chat Header** with `{{tag}}` placeholders
3. Save

See **Agents & Models** for available tags.
`Chat__DisableHeaderTagExpansion="true"` sends explicit custom headers as literal
text without resolving tags. `AgentOrchestration__DisableAccessibleThreadsHeader="true"`
makes `{{accessible-threads}}` expand to an empty string when tag expansion is
still enabled.
`Chat__DisableModuleHeaderTags="true"` stops module-owned header tag resolvers
from running. `Chat:CacheMaxMegabytes` controls the unified chat cache memory
budget for header state and recently-used channel/thread/agent token totals
attached to chat responses.

## Tool Call Notation

When agents execute tools, server-authoritative markers are injected into the assistant message:

**Regular job**:

```
⚙ [ExecuteAsSafeShell] → Completed
```

**Approval flow**:

```
⏳ [UnsafeExecuteAsDangerousShell] awaiting approval → Denied
```

**Inline tool**:

```
⚙ [wait] → done
```

**Task tool**:

```
⚙ [task_write_light_data] → done
```

Notation is generated server-side and included in both streaming events and persisted messages.

## Inline Tools

Some tools execute immediately without creating a job:

### `wait`

Pause execution for 1–300 seconds.

**Example**:

```json
{
  "tool_name": "wait",
  "arguments": { "seconds": 5 }
}
```

### `list_accessible_threads`

List threads the agent can read from other channels (requires cross-thread permission).

**Returns**:

```
ThreadName [ChannelTitle] (guid), ...
```

### `read_thread_history`

Fetch conversation history from a cross-channel thread (requires cross-thread permission and channel opt-in).

**Arguments**:

```json
{
  "thread_id": "00000000-0000-0000-0000-000000000000"
}
```

**Returns**: Array of messages from that thread.

## Image Attachments (Vision Models)

Vision-capable models accept images:

1. Click the **📎** button next to the send button
2. Select an image file
3. The image is uploaded and sent with your message

**Supported formats**: PNG, JPEG, GIF, WebP

**Behavior**:

- Vision models receive the image as multipart content
- Non-vision models receive only the text description (image is stripped)

## Cost Tracking

Token costs are tracked per message and aggregated:

- **Channel Total**: All messages in the channel
- **Thread Total**: Messages in the selected thread only

Costs are displayed above the input in the format:

```
channel: 1,234 tokens ($0.0123) [breakdown: in: 800, out: 434]
thread: 567 tokens ($0.0057) [breakdown: in: 350, out: 217]
```

**Breakdown bars** show input vs. output token proportions visually.

## Agent Selection

Use the **agent:** dropdown in the chat header to:

- Switch between allowed agents in a multi-agent channel
- Override the default agent for a single message
- Compare responses from different models

The selected agent persists until you change it.

## Thread Selection

Use the **thread:** dropdown in the chat header to:

- Switch to a different thread
- Create a new thread with the **+** button
- Select **[No thread]** for one-off mode (no history)

## One-Off Mode

When **[No thread]** is selected:

- Each message is isolated
- No history is sent to the agent
- A warning appears: `⚠ One-off mode: each message is an isolated prompt with no conversation history.`

Use this for:

- Quick calculations
- Translations
- Testing prompts without context

## Markdown Rendering

Assistant messages support basic markdown:

- **Bold**: `**text**`
- *Italic*: `*text*`
- `Code`: \`code\`
- Code blocks: \```language ... \```
- Lists: `- item` or `1. item`

Rendering is automatic — no configuration needed.

## Copy Messages

Long-press or right-click a message to copy its text.

## Keyboard Shortcuts

- **Enter**: Send message
- **Shift + Enter**: New line
- **Ctrl + K** (when input is focused): Clear input
- **Ctrl + L**: Focus on the message input

## Next Steps

Continue to **Permissions & Roles** to learn how to control agent capabilities.
]]>
