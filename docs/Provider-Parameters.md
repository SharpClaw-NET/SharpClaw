# Provider Completion Parameters Reference

## Scope

Provider modules own completion parameter support. They define typed values, provider limits, wire mapping, and provider-specific validation.

The Runtime passes the selected chat profile to the provider module through the current provider contract. The base host does not own Agent or channel configuration.

Use the provider-specific documents in `docs/providers/` for wire details and provider limits. Provider behavior can change when a provider package changes.

## Typed Parameters

The current provider contracts use these common parameter names when supported:

```text
temperature
topP
topK
frequencyPenalty
presencePenalty
stop
seed
responseFormat
reasoningEffort
```

The selected provider module decides which values are valid. Do not send a value only because another provider accepts it.

Provider modules can map one typed value to a provider-specific wire name. They can also reject a value before transport when the provider does not support it.

## Provider Extension Parameters

`providerParameters` is an explicit provider extension map. It carries provider-specific values that do not have a common typed field.

Use only keys that the owning provider document supports:

```json
{
  "providerParameters": {
    "custom_option": "value"
  }
}
```

The map does not select another execution path. The selected provider module receives typed values and extension values through the same provider action.

Provider modules decide how extension values map to the provider request. They can reject unsupported values before a successful completion response.

The base Runtime does not expose a fixed REST route for provider parameter administration. A provider or feature module owns any configuration surface that supplies a chat profile.

## Validation

The provider action is the validation boundary for completion parameters. Invalid typed values or unsupported extension values fail the action before the provider returns a successful completion.

The error contract is owned by the current provider and action contracts. Callers must not depend on internal exception text.

The `custom` provider can accept values that the host cannot validate in advance. The custom provider remains responsible for the request result.

## Provider Documents

Use these provider documents for current parameter details:

```text
docs/providers/OpenAI.md
docs/providers/DeepSeek.md
docs/providers/Anthropic.md
docs/providers/OpenRouter.md
docs/providers/Eden-AI.md
docs/providers/Google-Vertex-AI.md
docs/providers/Google-Gemini.md
docs/providers/ZAI.md
docs/providers/Vercel-AI.md
docs/providers/xAI.md
docs/providers/Groq.md
docs/providers/Cerebras.md
docs/providers/Mistral.md
docs/providers/GitHub-Copilot.md
docs/providers/Custom.md
docs/providers/LlamaSharp.md
docs/providers/Minimax.md
docs/providers/Ollama.md
```

The enabled module graph determines which provider keys are available in one Runtime.

## Configuration Boundary

Provider API keys use the provider module and the Runtime provider storage contract. The Runtime protects provider keys according to `Encryption__EncryptProviderKeys`.

Use canonical dotenv syntax for application configuration. Use `__` between sections and keys.

```dotenv
Encryption__EncryptProviderKeys=true
Modules__sharpclaw_providers_openai_compat=true
```

Do not place provider extension JSON in a feature-specific base database table. Use the owning provider or module contract.

## Execution Boundary

One direct chat turn uses one compiled graph, one action dispatcher, one provider action, and one provider transport.

Streaming chat uses the same parameter contract as buffered chat. Request cancellation reaches the provider action and provider transport.

Provider parameters do not create a second chat pipeline, a local fallback, or a compatibility path.
