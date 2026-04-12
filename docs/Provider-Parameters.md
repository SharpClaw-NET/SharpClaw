# Provider Completion Parameters Reference

This document is the overview reference for SharpClaw's completion
parameter system. For full details on any individual provider — wire
format, supported parameters, ranges, and provider-specific behaviour —
see the dedicated provider docs linked below.

The canonical source of truth in code is
[`CompletionParameterSpec.cs`](../SharpClaw.Application.Core/Clients/CompletionParameterSpec.cs).

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

| Provider | `ProviderType` | Protocol | Doc |
|---|---|---|---|
| OpenAI | `OpenAI` (`0`) | Chat Completions + Responses API | [providers/OpenAI.md](providers/OpenAI.md) |
| Anthropic | `Anthropic` (`1`) | Anthropic Messages API | [providers/Anthropic.md](providers/Anthropic.md) |
| OpenRouter | `OpenRouter` (`2`) | OpenAI-compatible | [providers/OpenRouter.md](providers/OpenRouter.md) |
| Google Vertex AI (native) | `GoogleVertexAI` (`3`) | Native `generateContent` (**stub**) | [providers/Google-Vertex-AI.md](providers/Google-Vertex-AI.md) |
| Google Gemini (native) | `GoogleGemini` (`4`) | Native `generateContent` | [providers/Google-Gemini.md](providers/Google-Gemini.md) |
| ZAI (Zhipu AI) | `ZAI` (`5`) | OpenAI-compatible | [providers/ZAI.md](providers/ZAI.md) |
| Vercel AI Gateway | `VercelAIGateway` (`6`) | OpenAI-compatible | [providers/Vercel-AI.md](providers/Vercel-AI.md) |
| xAI (Grok) | `XAI` (`7`) | OpenAI-compatible | [providers/xAI.md](providers/xAI.md) |
| Groq | `Groq` (`8`) | OpenAI-compatible | [providers/Groq.md](providers/Groq.md) |
| Cerebras | `Cerebras` (`9`) | OpenAI-compatible | [providers/Cerebras.md](providers/Cerebras.md) |
| Mistral | `Mistral` (`10`) | OpenAI-compatible | [providers/Mistral.md](providers/Mistral.md) |
| GitHub Copilot | `GitHubCopilot` (`11`) | OpenAI-compatible | [providers/GitHub-Copilot.md](providers/GitHub-Copilot.md) |
| Custom | `Custom` (`12`) | OpenAI-compatible (user endpoint) | [providers/Custom.md](providers/Custom.md) |
| Local (LLamaSharp) | `Local` (`13`) | In-process | [providers/Local.md](providers/Local.md) |
| Minimax | `Minimax` (`14`) | OpenAI-compatible | [providers/Minimax.md](providers/Minimax.md) |
| Google Gemini (OpenAI) | `GoogleGeminiOpenAi` (`15`) | OpenAI-compatible | [providers/Google-Gemini-OpenAI.md](providers/Google-Gemini-OpenAI.md) |
| Google Vertex AI (OpenAI) | `GoogleVertexAIOpenAi` (`16`) | OpenAI-compatible | [providers/Google-Vertex-AI-OpenAI.md](providers/Google-Vertex-AI-OpenAI.md) |

---

## Parameter support matrix

| Parameter | OpenAI | Anthropic | OpenRouter | Vertex AI³ | Vertex AI OAI | Gemini | Gemini OAI | xAI | Groq | Cerebras | Mistral | Copilot | ZAI | Vercel | Minimax | Local | Custom |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| `temperature` | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ❌ | ✅ |
| `topP` | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ❌ | ✅ |
| `topK` | ❌ | ✅ | ✅ | ✅ | ❌ | ✅ | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ | ✅ |
| `frequencyPenalty` | ✅ | ❌ | ✅ | ❌ | ✅ | ❌ | ✅ | ✅ | ✅ | ❌ | ❌ | ✅ | ✅ | ✅ | ❌ | ❌ | ✅ |
| `presencePenalty` | ✅ | ❌ | ✅ | ❌ | ✅ | ❌ | ✅ | ✅ | ✅ | ❌ | ❌ | ✅ | ✅ | ✅ | ❌ | ❌ | ✅ |
| `stop` | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ❌ | ✅ |
| `seed` | ✅ | ❌ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ❌ | ❌ | ✅ |
| `responseFormat` | ✅ | ❌ | ✅ | ✅² | ⚠️¹ | ✅² | ⚠️¹ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ❌ | ❌ | ✅ |
| `reasoningEffort` | ✅ | ❌ | ❌ | ✅ | ✅ | ✅ | ✅ | ❌ | ❌ | ❌ | ❌ | ✅ | ❌ | ❌ | ❌ | ❌ | ✅ |

> ¹ Google's OpenAI-compatible endpoints (`GoogleGeminiOpenAi`,
> `GoogleVertexAIOpenAi`) only accept the full `json_schema` variant.
> The simplified `{"type": "json_object"}` form is **rejected** at
> validation time.
>
> ² Native Google endpoints (`GoogleGemini`, `GoogleVertexAI`) map
> `responseFormat` to `responseMimeType` and accept both `json_object`
> and `json_schema`. The `json_schema` variant additionally extracts
> the inner schema and sets `responseSchema` in `generationConfig`.
>
> ³ `GoogleVertexAI` (native) is **not yet implemented** — the spec is
> reserved for forward-compatibility. Use `GoogleVertexAIOpenAi` instead.

---

## Key differences at a glance

| Constraint | Providers affected |
|---|---|
| Temperature max **1.0** | Anthropic, Mistral |
| Temperature max **1.5** | Cerebras |
| `topK` max **40** | Google Gemini (native), Google Vertex AI (native) |
| `topK` **not supported** (OAI schema has no `top_k`) | GoogleGeminiOpenAi, GoogleVertexAIOpenAi |
| Stop sequences max **5** | Google (all four types) |
| Stop sequences max **8 192** | Anthropic |
| `json_object` rejected | GoogleGeminiOpenAi, GoogleVertexAIOpenAi |
| No `frequencyPenalty` / `presencePenalty` | Anthropic, GoogleGemini (native), GoogleVertexAI (native), Cerebras, Mistral, Minimax |
| `"xhigh"` reasoning | OpenAI, GitHub Copilot only |
| No typed parameters at all | Local (LLamaSharp) |
| **Not yet implemented** | GoogleVertexAI (native) — use GoogleVertexAIOpenAi |

---

## `providerParameters` escape-hatch

The `providerParameters` dictionary (`Dictionary<string, JsonElement>?`)
is a **temporary fallback** for provider-specific options that SharpClaw
does not yet model as typed fields. Keys are merged into the API request
payload **after** typed parameters, so they can override or supply
additional values.

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

The `Custom` provider type uses a permissive passthrough — all
parameters are accepted with wide ranges. This avoids blocking users
whose custom endpoint supports parameters that SharpClaw cannot
pre-verify.

### Error message format

Every validation error includes:
- The **provider name** that was violated
- The **parameter name** and the **invalid value**
- The **expected range** or list of valid values
- For unsupported parameters: a list of **providers that do support it**
