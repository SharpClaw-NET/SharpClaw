# Provider Completion Parameters Reference

This document is the overview reference for SharpClaw's completion
parameter system. For full details on any individual provider — wire
format, supported parameters, ranges, and provider-specific behaviour —
see the dedicated provider docs linked below.

The canonical source of truth in code is
[`CompletionParameterSpec.cs`](../SharpClaw.Runtime.BLL/Clients/CompletionParameterSpec.cs).

> ⚠️ **Completeness disclaimer:** Provider API surfaces are large and
> change frequently. The typed completion parameter support documented
> here is a best-effort mapping that may be incomplete or drift from
> upstream provider changes over time. This first-class support is not
> expected to reach full parity with every provider until at least
> SharpClaw **1.0.0**, and potentially beyond. Until then, developers
> should use the [`providerParameters` escape-hatch](#providerparameters-escape-hatch)
> (the JSON key-value pair dictionary) for any parameter that is not yet
> modelled as a typed field or whose validation constraints have changed
> upstream.

---

## First-class support philosophy

SharpClaw's goal is **first-class provider parameter support** — you
should never need to have two or three provider docs open alongside
SharpClaw's own just to get something working. Every parameter that
SharpClaw models as a typed field is validated, documented, and mapped
to the correct wire format automatically. You configure it once and it
works across providers that support it.

If something is missing, broken, or the docs here are incomplete,
**please open a GitHub issue first:**

> 🐛 **https://github.com/mkn8rn/SharpClaw/issues**

This is the fastest way to get a typed parameter added, a validation
range corrected, or a new provider endpoint wired in. The project
actively tracks upstream API changes, and issues ensure nothing falls
through the cracks.

The [`providerParameters` escape-hatch](#providerparameters-escape-hatch)
exists as a **temporary fallback** — it lets you unblock yourself
immediately by injecting raw key-value pairs into the API request while
a proper typed field is being added. It is not intended as the primary
way to use SharpClaw long-term. If you find yourself relying on it for
a common parameter, that is a sign the typed support should be expanded
and an issue is welcome.

---

## Provider docs

Each provider has a dedicated page with full parameter tables, wire
format examples, and provider-specific notes:

| Provider | `ProviderKey` | Protocol | Doc |
|---|---|---|---|
| OpenAI | `openai` | Chat Completions + Responses API | [providers/OpenAI.md](providers/OpenAI.md) |
| DeepSeek | `deepseek` | OpenAI-compatible | [providers/DeepSeek.md](providers/DeepSeek.md) |
| Anthropic | `anthropic` | Anthropic Messages API | [providers/Anthropic.md](providers/Anthropic.md) |
| OpenRouter | `openrouter` | OpenAI-compatible | [providers/OpenRouter.md](providers/OpenRouter.md) |
| Eden AI | `eden-ai` | OpenAI-compatible gateway | [providers/Eden-AI.md](providers/Eden-AI.md) |
| Google Vertex AI (native) | `google-vertex-ai` | Native `generateContent` | [providers/Google-Vertex-AI.md](providers/Google-Vertex-AI.md) |
| Google Gemini (native) | `google-gemini` | Native `generateContent` | [providers/Google-Gemini.md](providers/Google-Gemini.md) |
| ZAI (Zhipu AI) | `zai` | OpenAI-compatible | [providers/ZAI.md](providers/ZAI.md) |
| Vercel AI Gateway | `vercel-ai-gateway` | OpenAI-compatible | [providers/Vercel-AI.md](providers/Vercel-AI.md) |
| xAI (Grok) | `xai` | OpenAI-compatible | [providers/xAI.md](providers/xAI.md) |
| Groq | `groq` | OpenAI-compatible | [providers/Groq.md](providers/Groq.md) |
| Cerebras | `cerebras` | OpenAI-compatible | [providers/Cerebras.md](providers/Cerebras.md) |
| Mistral | `mistral` | OpenAI-compatible | [providers/Mistral.md](providers/Mistral.md) |
| GitHub Copilot | `github-copilot` | OpenAI-compatible | [providers/GitHub-Copilot.md](providers/GitHub-Copilot.md) |
| Custom | `custom` | OpenAI-compatible (user endpoint) | [providers/Custom.md](providers/Custom.md) |
| LlamaSharp | `llamasharp` | Sidecar-local GBNF grammar-constrained | [providers/LlamaSharp.md](providers/LlamaSharp.md) |
| Minimax | `minimax` | OpenAI-compatible | [providers/Minimax.md](providers/Minimax.md) |
| Google Gemini (OpenAI) | `google-gemini-openai` | OpenAI-compatible | [providers/Google-Gemini-OpenAI.md](providers/Google-Gemini-OpenAI.md) |
| Google Vertex AI (OpenAI) | `google-vertex-ai-openai` | OpenAI-compatible | [providers/Google-Vertex-AI-OpenAI.md](providers/Google-Vertex-AI-OpenAI.md) |
| Ollama | `ollama` | OpenAI-compatible (user-managed server) | [providers/Ollama.md](providers/Ollama.md) |

---

## Parameter support matrix

| Parameter | OpenAI | DeepSeek | Anthropic | OpenRouter | Eden AI | Vertex AI | Vertex AI OAI | Gemini | Gemini OAI | xAI | Groq | Cerebras | Mistral | Copilot | ZAI | Vercel | Minimax | LlamaSharp | Custom | Ollama |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| `temperature` | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| `topP` | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| `topK` | ❌ | ❌ | ✅ | ✅ | ❌ | ✅ | ❌ | ✅ | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ | ✅ | ✅ | ❌ |
| `frequencyPenalty` | ✅ | ❌ | ❌ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ❌ | ❌ | ✅ | ✅ | ✅ | ❌ | ✅ | ✅ | ✅ |
| `presencePenalty` | ✅ | ❌ | ❌ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ❌ | ❌ | ✅ | ✅ | ✅ | ❌ | ✅ | ✅ | ✅ |
| `stop` | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| `seed` | ✅ | ❌ | ❌ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ❌ | ✅ | ✅ | ✅ |
| `responseFormat` | ✅ | ✅ | ❌ | ✅ | ✅ | ✅² | ⚠️¹ | ✅² | ⚠️¹ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ❌ | ✅³ | ✅ | ✅ |
| `reasoningEffort` | ✅ | ✅ | ❌ | ❌ | ✅ | ✅ | ✅ | ✅ | ✅ | ❌ | ❌ | ❌ | ❌ | ✅ | ❌ | ❌ | ❌ | ⚠️⁴ | ✅ | ❌ |

> ¹ Google's OpenAI-compatible endpoints (`google-gemini-openai`,
> `google-vertex-ai-openai`) only accept the full `json_schema` variant.
> The simplified `{"type": "json_object"}` form is **rejected** at
> validation time.
>
> ² Native Google endpoints (`GoogleGemini`, `GoogleVertexAI`) map
> `responseFormat` to `responseMimeType` and accept both `json_object`
> and `json_schema`. The `json_schema` variant additionally extracts
> the inner schema and sets `responseSchema` in `generationConfig`.
>
> ³ LlamaSharp accepts both `{"type":"json_object"}` (mapped to a
> generic JSON GBNF grammar) and `{"type":"json_schema", …}` (converted
> by `LlamaSharpJsonSchemaConverter` into a schema-specific GBNF
> attached to `DefaultSamplingPipeline.Grammar`). The converter covers
> the OpenAI strict-mode subset — `type`, `properties`, `required`,
> `additionalProperties`, `items`, `enum`, `const`, `anyOf`/`oneOf`,
> simple `allOf`, and local `$ref`/`$defs`. Schema features outside
> that matrix (`pattern`, `patternProperties`, `minProperties`,
> `maxProperties`, `uniqueItems`, strict numeric ranges, non-local
> `$ref`, `not`, `if`/`then`/`else`, `contains`) degrade gracefully to
> the generic JSON grammar with a debug-category warning listing the
> unsupported keywords. Tool calling takes precedence — when a request
> contains tools and a `responseFormat`, the tool-envelope grammar wins
> and `responseFormat` is ignored with a debug-category log line.
>
> ⁴ LlamaSharp accepts `reasoningEffort` informationally only. llama.cpp
> has no reasoning-budget knob; the value is surfaced to the model via a
> `reasoning-effort: {level} (informational; this model has no
> mechanical reasoning-effort control)` segment in the default chat
> header, and as the `{{reasoning-effort}}` tag in custom header
> templates. The sampling pipeline does not mechanically enforce it.

---

## Key differences at a glance

| Constraint | Providers affected |
|---|---|
| Temperature max **1.0** | Anthropic, Mistral |
| Temperature max **1.5** | Cerebras |
| `topK` model-dependent | Google Gemini (native), Google Vertex AI (native) |
| `topK` max **128** | LlamaSharp |
| `topK` **not supported** (OAI schema has no `top_k`) | google-gemini-openai, google-vertex-ai-openai, ollama |
| Stop sequences max **5** | Google (all four types) |
| Stop sequences max **8 192** | Anthropic |
| `json_object` rejected | google-gemini-openai, google-vertex-ai-openai |
| No `frequencyPenalty` / `presencePenalty` | DeepSeek, Anthropic, Cerebras, Mistral, Minimax |
| `"xhigh"` reasoning | OpenAI, DeepSeek, GitHub Copilot |
| Eden AI gateway routing | Model IDs use `provider/model`, with `@edenai` for smart routing |
| Hidden thinking-state replay | DeepSeek (preserves provider-specific `reasoning_content` across tool-call turns) |
| `responseFormat` grammar-constrained subset | LlamaSharp (strict-mode schema subset via GBNF; exotic keywords degrade to generic JSON with a logged warning; tool calling takes precedence) |
| `reasoningEffort` informational only | LlamaSharp (no mechanical reasoning-budget knob in llama.cpp; surfaced via chat header) |
| Tool calling: model-dependent reliability | LlamaSharp, Ollama, Custom |

---

## `providerParameters` escape-hatch

The `providerParameters` dictionary (`Dictionary<string, JsonElement>?`)
is a **temporary fallback** for provider-specific options that SharpClaw
does not yet model as typed fields. Keys are merged into the API request
payload alongside typed parameters so they can supply additional values.

Keys that the client already sets (e.g. `model`, `messages`, `tools`)
are **never overwritten** — user-supplied parameters are additive only.

> ℹ️ **This is a stopgap, not a feature.** If you find yourself using
> `providerParameters` for something that should be a typed field,
> please [open a GitHub issue](https://github.com/mkn8rn/SharpClaw/issues)
> so it can be added with proper validation and documentation.

> The `.env` flag `Agent:DisableCustomProviderParameters=true` disables
> the escape-hatch entirely. When set, `providerParameters` is ignored
> and only typed fields are sent.

---

## Validation

SharpClaw validates typed completion parameters at **two** levels:

### 1. Write-time validation (agent create / update)

When an agent is created or updated via `POST /agents` or
`PUT /agents/{id}`, the typed parameters are validated against the
`CompletionParameterSpec` for the agent's model provider. Invalid
parameters produce an immediate **HTTP 400** response:

```json
{
  "error": "Invalid completion parameters",
  "provider": "Anthropic",
  "validationErrors": [
    "Invalid temperature value 1.5 for 'Anthropic'. Expected range: 0.0–1.0.",
    "'Anthropic' does not support the 'frequencyPenalty' parameter. Remove it or switch to a provider that supports it (OpenAI, OpenRouter, xAI, Groq)."
  ]
}
```

### 2. Chat-time safety net

Before every chat completion request, the parameters are validated
again as a safety net (catches agents created before validation existed,
or model changes outside the update flow). The same structured error
format is returned.

### Custom / unknown providers

The `custom` provider key uses a permissive passthrough — all
parameters are accepted with wide ranges. This avoids blocking users
whose custom endpoint supports parameters that SharpClaw cannot
pre-verify.

### Error message format

Every validation error includes:
- The **provider name** that was violated
- The **parameter name** and the **invalid value**
- The **expected range** or list of valid values
- For unsupported parameters: a list of **providers that do support it**
