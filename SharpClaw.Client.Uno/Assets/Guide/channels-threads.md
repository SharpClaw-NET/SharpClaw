<![CDATA[# Channels & Threads

Learn how to organize conversations effectively with channels, threads, and contexts.

## Channels

A **channel** is a conversation space with:

- A specific default agent
- Optional allowed agents (multi-agent support)
- Permission overrides
- Default resource assignments
- Tool awareness configuration

### Creating a Channel

1. Click **+ New Channel** in the sidebar
2. Fill in the form:
   - **Name**: Descriptive label (e.g., "Code Review", "Research")
   - **Agent**: Primary AI assistant for this channel
   - **Context** (optional): Organizational group
3. Click **Create**

### Channel Settings

Right-click a channel in the sidebar and select **Settings** to access:

- **Rename** or **Delete** channel
- **Agent Assignment**: Change default agent or add allowed agents
- **Thread Management**: Create, edit, and delete threads
- **Permission Overrides**: Fine-tune what the agent can do in this channel
- **Default Resources**: Pre-assign shell environments, containers, etc.
- **Tool Awareness**: Control which tool schemas are sent to the agent
- **Custom Chat Header**: Override the metadata header template
- **Disable Chat Header**: Remove all metadata headers for this channel

### Multi-Agent Channels

Allow multiple agents in a single channel:

1. Open channel settings
2. Scroll to **Allowed Agents**
3. Click **+ Add** and select agents
4. When chatting, use the **agent:** dropdown to switch between them

## Threads

A **thread** is a conversation within a channel with:

- Independent message history
- Configurable history limits
- Isolated context from other threads

### Creating a Thread

1. Select a channel
2. In the chat header, click **+** next to the thread dropdown
3. Enter:
   - **Name**: Thread topic (e.g., "Bug #142", "User Stories")
   - **Max Messages** (optional): Limit recent message count (default: 50)
   - **Max Characters** (optional): Limit total character count (default: 100,000)
4. Click **Create**

### Using Threads

- **Select a thread** from the dropdown to activate it
- All messages are saved to that thread
- History is sent to the agent based on thread limits
- **[No thread]**: One-off mode — no history, each message is isolated

### Thread Limits

Both max messages and max characters apply simultaneously:

- **Max Messages**: Keep only the most recent N messages
- **Max Characters**: Trim oldest messages until total chars < limit

This prevents token overflow on long conversations while retaining recent context.

### Thread Management

Right-click a channel, select **Settings**, then go to the **Threads** tab:

- **Rename** a thread
- **Edit limits** (max messages, max characters)
- **Delete** a thread (removes all its messages permanently)

### When to Use Threads

- **Topic Separation**: Different subjects in the same project
- **Ephemeral Chats**: Short-term discussions that don't pollute main history
- **Context Control**: Limit what the agent "remembers"

### When to Use One-Off Mode

Select **[No thread]** when:

- Testing a single prompt
- Quick calculations or translations
- You don't want previous messages affecting the response

## Contexts

A **context** is an organizational container for channels, like a workspace or project.

### Creating a Context

1. Open the **channel creation form**
2. In the **Context** field, select **+ Create Context...**
3. Enter a name and description
4. Optionally assign a default agent

### Context Features

- **Default Agent**: All new channels in this context inherit this agent
- **Channel Grouping**: Channels are grouped under context headings in the sidebar
- **Permission Inheritance**: Channels can inherit permission sets from the context
- **Default Resources**: Shared resource assignments for all channels

### Editing a Context

Right-click any channel in that context and select **Context Settings**.

### Collapsing Contexts

Click the **▼** or **▶** icon next to a context name in the sidebar to expand/collapse its channels.

## Channel Tabs

When a channel is selected, tabs appear in the chat header:

- **[chat]**: Main conversation view
- **[tasks]**: Task definitions and instances for this channel
- **[jobs]**: Background jobs submitted by the agent
- **[settings]**: Channel configuration
- **[bots]**: Bot integration status (if gateway is enabled)

Switch between them to access different features while staying in the same channel.

## Cost Tracking

Token usage is displayed above the chat input:

- **Channel Total**: Cumulative cost for all messages in this channel
- **Thread Total** (when a thread is selected): Cost for messages in this thread only

Costs are calculated from provider pricing and persisted even if models are deleted.

## Cross-Thread History Access

Agents can read history from threads in other channels if:

1. The agent has the **CanReadCrossThreadHistory** permission
2. The target channel also has **CanReadCrossThreadHistory** enabled (opt-in)
3. The agent is the primary agent or in the allowed agents list for that channel

Use the inline tool `list_accessible_threads` to discover available threads, then `read_thread_history` to fetch messages.

## Best Practices

- **Use threads for projects**: One thread per feature, bug, or task
- **One-off for quick queries**: Don't clutter history with transient prompts
- **Name threads descriptively**: Future you will thank you
- **Set conservative limits**: Long histories cost more tokens and slow down inference
- **Leverage contexts for organization**: Group related channels (e.g., "Work", "Personal")

## Next Steps

Continue to **Agents & Models** to learn how to customize AI behavior.
]]>
