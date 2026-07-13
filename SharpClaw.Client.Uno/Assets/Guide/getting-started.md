<![CDATA[# Getting Started

This guide walks you through setting up SharpClaw from scratch.

## Step 1: First-Time Setup

When you first launch SharpClaw, you'll see the First Setup wizard:

1. **Create Admin Account**: Choose a username and password
2. **Configure Default Provider** (optional): Add an API key for OpenAI or another provider
3. **Create First Agent** (optional): Set up your initial AI assistant

You can skip optional steps and configure them later in Settings.

## Step 2: Add AI Providers

Providers are the API services that power your AI models.

### Adding a Provider

1. Go to **Settings** → **Providers**
2. Click the **+** button to expand the creation form
3. Enter a name (e.g., "OpenAI Production")
4. Select the provider key
5. Click **Create**

### Setting API Keys

After creating a provider:

1. Click on its name in the list
2. Enter your API key in the password field
3. Click **[ Set Key ]**

### Syncing Models

Once the API key is set:

1. Click **↻ Sync models from provider**
2. All available models will be imported automatically

### Supported Providers

- OpenAI (GPT-4, GPT-4o, o1, o3, etc.)
- Anthropic (Claude models)
- Google Gemini
- Google Vertex AI
- OpenRouter (unified gateway)
- XAI (Grok)
- Groq (fast inference)
- Cerebras (ultra-fast)
- Mistral AI
- GitHub Copilot (device code login)
- Vercel AI Gateway
- ZAI
- Minimax
- Custom (any OpenAI-compatible API)

### Device Code Providers

GitHub Copilot uses OAuth device code flow:

1. Create a GitHub Copilot provider
2. Click **[ Start Login ]**
3. A code and verification URL appear
4. The URL opens in your browser automatically
5. Enter the displayed code
6. Return to SharpClaw — it will poll and complete authentication

## Step 3: Create Agents

Agents are AI assistants powered by specific models.

### Quick Agent Creation

1. Go to **Settings** → **Agents**
2. Click **↻ Sync agents from models** to auto-create one agent per model

### Manual Agent Creation

1. Go to **Settings** → **Agents**
2. Click **+** to expand the form
3. Enter agent name
4. Select a model
5. (Optional) Add a system prompt to customize behavior
6. Click **Create**

### Editing Agents

Click an agent's name to access:

- **System Prompt**: Instructions that guide the agent's behavior
- **Provider Parameters**: JSON overrides for API requests (e.g., `{"temperature": 0.7}`)
- **Custom Chat Header**: Override the metadata header sent with messages
- **Role Assignment**: Grant specific permissions via roles

## Step 4: Create Your First Channel

Channels are conversation spaces.

1. Click **+ New Channel** in the sidebar
2. Enter a channel name
3. Select an agent
4. Click **Create**

The channel appears in the sidebar under "Default Context".

## Step 5: Start Chatting

1. Click your channel in the sidebar
2. Type a message in the input box at the bottom
3. Press **Enter** or click **Send**

Your message is sent to the agent, and the response streams in real-time.

## Step 6: Create Threads (Optional)

Threads organize conversations with history limits.

1. Select a channel
2. In the chat header, click the **+** button next to the thread dropdown
3. Enter a thread name
4. Optionally set max messages and/or max characters
5. Click **Create**

Select the thread from the dropdown to use it. Messages in threads remember previous context.

**One-off mode**: When no thread is selected, each message is isolated with no history.

## Step 7: Explore Advanced Features

- **Roles & Permissions**: Control what agents can do
- **Jobs**: Background tasks with approval workflows
- **Tasks**: C# automation scripts for complex workflows
- **Bot Integrations**: Connect Telegram, Discord, WhatsApp bots
- **Gateway**: Public-facing API proxy

See the corresponding guide sections for details.

## Common First-Time Issues

**"Provider sync failed"**: Check that your API key is valid and active.

**"No models available"**: Sync models from a provider first.

**"Agent not responding"**: Verify the provider's API is reachable and the model exists.

**"Connection failed"**: The backend API may not be running. Check Settings → Gateway for process status.

## Next Steps

Continue to **Channels & Threads** to learn advanced conversation organization.
]]>
