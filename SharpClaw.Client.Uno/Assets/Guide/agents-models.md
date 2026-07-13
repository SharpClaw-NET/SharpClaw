<![CDATA[# Agents & Models

Understand how agents and models work, and how to customize them.

## Models

A **model** represents a specific AI language model from a provider.

### Model Sources

Models are imported by syncing from providers:

1. Go to **Settings** → **Providers**
2. Click a provider's name
3. Click **↻ Sync models from provider**

Models are auto-detected and created with capabilities inferred from their names.

### Model Capabilities

SharpClaw infers model capabilities automatically:

- **Chat**: Standard conversational models
- **Vision**: Can process images (e.g., GPT-4o, Claude 3/4, Gemini)
- **Image Generation**: Create images from prompts
- **Embedding**: Vector representations for semantic search

These capabilities control what tools and inputs the model accepts.

### Local Models

Add models from HuggingFace:

1. Go to **Settings** → **Models**
2. Expand the **Add Local Model** section
3. Enter a HuggingFace model URL (e.g., `https://huggingface.co/TheBloke/Llama-2-7B-GGUF`)
4. Click **[ List Files ]**
5. Select a quantization (GGUF file)
6. Click **[ Download ]**

Downloaded models appear in the models list and can power agents.

**Note**: Local model inference requires the backend to be configured with `Local__GpuLayerCount` and other settings in the core `.env` file.

## Agents

An **agent** is an AI assistant powered by a specific model.

### Agent Anatomy

Every agent has:

- **Name**: Human-readable identifier
- **Model**: The AI model powering responses
- **System Prompt**: Instructions guiding behavior
- **Role**: Permission set defining capabilities
- **Provider Parameters**: JSON overrides for API requests
- **Custom Chat Header**: Metadata template override

### Creating Agents

**Quick method** (auto-create from models):

1. Go to **Settings** → **Agents**
2. Click **↻ Sync agents from models**

One agent per model is created with the model's name.

**Manual method** (custom configuration):

1. Go to **Settings** → **Agents**
2. Click **+** to expand the form
3. Enter a name
4. Select a model
5. Optionally add a system prompt
6. Click **Create**

### System Prompts

The **system prompt** is a set of instructions sent before every user message.
Core may append a small native-tool instruction suffix when tool schemas are
enabled. Set `Chat:DisableDefaultSystemPrompt=true` in the Core `.env` to
suppress that core suffix while keeping the prompt saved on the agent.

**Example system prompts**:

- `"You are a helpful coding assistant. Provide concise, accurate code examples."`
- `"You are a creative writer. Write in a poetic, descriptive style."`
- `"Answer all questions as if you are a pirate."`

**Best practices**:

- Be specific about the agent's role and expertise
- Include output format instructions if needed
- Keep it concise — long prompts cost tokens on every message
- Use custom chat headers for dynamic context (user, role, time, etc.)

### Editing Agents

Click an agent's name in **Settings** → **Agents** to access:

#### System Prompt Editor

Update instructions and click **Save Prompt**.

#### Provider Parameters

JSON key-value pairs merged into every API request:

```json
{
  "temperature": 0.7,
  "top_p": 0.9,
  "max_tokens": 2048,
  "response_mime_type": "application/json"
}
```

Use this for:

- Temperature/top-p tuning
- Max token limits
- Gemini-specific response formats
- Anthropic thinking budgets
- OpenAI structured output schemas

Click **Save Parameters** to apply.

#### Custom Chat Header

Override the default metadata header sent with messages.
Core `.env` can disable generated default headers with
`Chat:DisableDefaultHeaders=true`; explicit agent and channel custom headers
still run. `Chat:DisableHeaderTagExpansion=true` keeps those explicit custom
headers as literal text, so no built-in tags, resource tags, or module-owned
tags are resolved. `AgentOrchestration:DisableAccessibleThreadsHeader=true`
makes `{{accessible-threads}}` expand to an empty string when tag expansion is
still enabled.
`Chat:DisableModuleHeaderTags=true` prevents module-owned header tags from
executing. `Chat:CacheMaxMegabytes` controls the unified chat cache memory
budget for header state and recently-used channel/thread/agent token totals
attached to chat responses.

**Available tags**:

- Context: `{{time}}`, `{{user}}`, `{{via}}`, `{{role}}`, `{{bio}}`, `{{agent-name}}`, `{{agent-role}}`, `{{grants}}`, `{{agent-grants}}`, `{{editor}}`, `{{accessible-threads}}`
- Resources: `{{Agents}}`, `{{Models}}`, `{{Providers}}`, `{{Channels}}`, `{{Threads}}`, `{{Roles}}`, `{{Users}}`, `{{Containers}}`, etc.

**Example custom header**:

```
[user: {{user}} | agent: {{agent-name}} | time: {{time}}]
Available agents: {{Agents:{Name}}}
```

Click **Save Header** to apply.

#### Role Assignment

Assign a role to grant permissions:

1. Select a role from the dropdown
2. Click **Assign Role**

See **Permissions & Roles** for details on configuring permissions.

### Agent vs. Model

- **Model**: The raw AI service (GPT-4, Claude, etc.)
- **Agent**: Configured instance with prompts, permissions, and behavior customization

You can create multiple agents using the same model but with different prompts or roles.

**Example**:

- `GPT-4o-Coder` → model: GPT-4o, prompt: "You are a coding assistant"
- `GPT-4o-Writer` → model: GPT-4o, prompt: "You are a creative writer"

## Multi-Agent Chat

Channels support multiple agents:

1. Open channel settings
2. Add agents to **Allowed Agents**
3. In chat, use the **agent:** dropdown to switch

This is useful for:

- Comparing responses from different models
- Specialized agents for different tasks in the same project
- Fallback agents when primary is unavailable

## Agent Selection Priority

When sending a message in a channel:

1. If you selected an agent from the dropdown, that agent is used
2. Otherwise, the channel's default agent is used
3. If the channel has no default, the context's default agent is inherited

## Responses API vs. Chat Completions

SharpClaw automatically routes requests to the appropriate API:

- **Responses API** (`/v1/responses`): Used for all modern models (GPT-4o, o1, o3, Claude, Gemini, etc.)
- **Chat Completions** (`/v1/chat/completions`): Legacy API for GPT-3.5/GPT-4 families only

The routing is transparent — you don't need to configure anything.

## Vision Models

Models with **Vision** capability accept images:

- GPT-4o, GPT-4 Turbo, GPT-4 Vision
- Claude 3/4 families
- Gemini 1.5/2.0
- Pixtral, Llama 3.2 Vision, Grok Vision

When chatting with a vision model, image attachments are sent as multipart content.

**Non-vision models** receive only the text description; the image is stripped.

## Next Steps

Continue to **Chat Features** to learn about streaming, attachments, and inline tools.
]]>
