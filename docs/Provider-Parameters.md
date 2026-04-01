# Provider Completion Parameters Reference

This document is the authoritative reference for which completion
parameters each provider supports, what their valid ranges are, how
they are mapped to wire format, and how validation works.

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

## Parameter support matrix

| Parameter | OpenAI | Anthropic | OpenRouter | Google Vertex AI | Google Gemini | xAI (Grok) | Groq | Cerebras | Mistral | GitHub Copilot | ZAI | Vercel AI | Minimax | Local | Custom |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| `temperature` | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ❌ | ✅ |
| `topP` | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ❌ | ✅ |
| `topK` | ❌ | ✅ | ✅ | ✅ | ✅ | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ | ✅ |
| `frequencyPenalty` | ✅ | ❌ | ✅ | ✅ | ✅ | ✅ | ✅ | ❌ | ❌ | ✅ | ✅ | ✅ | ❌ | ❌ | ✅ |
| `presencePenalty` | ✅ | ❌ | ✅ | ✅ | ✅ | ✅ | ✅ | ❌ | ❌ | ✅ | ✅ | ✅ | ❌ | ❌ | ✅ |
| `stop` | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ❌ | ✅ |
| `seed` | ✅ | ❌ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ❌ | ❌ | ✅ |
| `responseFormat` | ✅ | ❌ | ✅ | ⚠️¹ | ⚠️¹ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ❌ | ❌ | ✅ |
| `reasoningEffort` | ✅ | ❌ | ❌ | ✅ | ✅ | ❌ | ❌ | ❌ | ❌ | ✅ | ❌ | ❌ | ❌ | ❌ | ✅ |

> ¹ Google Gemini / Vertex AI: only the full `json_schema` variant is
> supported through Google's OpenAI-compatible endpoint. The simplified
> `{"type": "json_object"}` form is **not** supported and will be rejected
> at validation time. See [Google-specific notes](#google-vertex-ai--gemini).

---

## Valid ranges

### temperature

| Provider | Min | Max |
|---|---|---|
| OpenAI | 0.0 | 2.0 |
| Anthropic | 0.0 | **1.0** |
| OpenRouter | 0.0 | 2.0 |
| Google Vertex AI | 0.0 | 2.0 |
| Google Gemini | 0.0 | 2.0 |
| xAI (Grok) | 0.0 | 2.0 |
| Groq | 0.0 | 2.0 |
| Cerebras | 0.0 | **1.5** |
| Mistral | 0.0 | **1.0** |
| GitHub Copilot | 0.0 | 2.0 |
| ZAI | 0.0 | 2.0 |
| Vercel AI Gateway | 0.0 | 2.0 |
| Minimax | 0.0 | 2.0 |

> **Note:** Anthropic and Mistral cap temperature at 1.0. Cerebras caps
> at 1.5. All others accept up to 2.0. Lower values produce more
> deterministic output; higher values increase randomness.

### topP

All supporting providers accept `0.0`–`1.0`.

### topK

| Provider | Min | Max | Notes |
|---|---|---|---|
| Anthropic | 1 | ∞ | No documented ceiling |
| Google Vertex AI | 1 | 40 | |
| Google Gemini | 1 | 40 | |
| OpenRouter | 1 | ∞ | Passthrough to underlying model |

### frequencyPenalty / presencePenalty

All supporting providers accept `-2.0`–`2.0`.

### stop (stop sequences)

| Provider | Max sequences | Wire name |
|---|---|---|
| OpenAI | 4 | `stop` |
| Anthropic | 8 192 | `stop_sequences` |
| OpenRouter | 4 | `stop` |
| Google Vertex AI | 5 | `stop` |
| Google Gemini | 5 | `stop` |
| xAI (Grok) | 4 | `stop` |
| Groq | 4 | `stop` |
| Cerebras | 4 | `stop` |
| Mistral | 4 | `stop` |
| GitHub Copilot | 4 | `stop` |
| ZAI | 4 | `stop` |
| Vercel AI Gateway | 4 | `stop` |
| Minimax | 4 | `stop` |

### seed

All supporting providers accept any integer. Used for deterministic
sampling — two requests with the same seed and parameters should
produce identical output (best-effort, not guaranteed by all providers).

### reasoningEffort

Valid values vary by provider:

| Provider | Valid values |
|---|---|
| OpenAI | `"none"`, `"minimal"`, `"low"`, `"medium"`, `"high"`, `"xhigh"` |
| Google Gemini | `"none"`², `"minimal"`, `"low"`, `"medium"`, `"high"` |
| Google Vertex AI | `"none"`², `"minimal"`, `"low"`, `"medium"`, `"high"` |
| GitHub Copilot | `"none"`, `"minimal"`, `"low"`, `"medium"`, `"high"`, `"xhigh"` |

> ² `"none"` disables thinking on Gemini 2.5 models only. Reasoning
> cannot be turned off for Gemini 2.5 Pro or Gemini 3 models.

On OpenAI, `reasoningEffort` is supported on o-series and gpt-5 models
via the Responses API. Mapped to `reasoning.effort` in the Responses API
request body, or `reasoning_effort` at the top level in Chat Completions.

On Google, `reasoning_effort` is mapped to the model's internal thinking
level:

| `reasoning_effort` | Gemini 3.1 Pro | Gemini 3.1 Flash-Lite | Gemini 3 Flash | Gemini 2.5 |
|---|---|---|---|---|
| `minimal` | `low` | `minimal` | `minimal` | budget 1 024 |
| `low` | `low` | `low` | `low` | budget 1 024 |
| `medium` | `medium` | `medium` | `medium` | budget 8 192 |
| `high` | `high` | `high` | `high` | budget 24 576 |

If no `reasoningEffort` is set, Google uses each model's default thinking
level or budget.

---

## Wire format mapping

SharpClaw normalises typed completion parameters into each provider's
expected wire format. The table below shows how each parameter is sent.

### OpenAI (Chat Completions)

```json
{
  "temperature": 0.7,
  "top_p": 0.9,
  "frequency_penalty": 0.5,
  "presence_penalty": 0.3,
  "stop": ["\\n", "END"],
  "seed": 42,
  "response_format": { "type": "json_object" }
}
```

### OpenAI (Responses API)

The Responses API uses the same field names for most parameters.
`reasoningEffort` is mapped to a nested `reasoning` object:

```json
{
  "temperature": 0.7,
  "top_p": 0.9,
  "reasoning": { "effort": "medium" }
}
```

> `responseFormat` is **not** used on the Responses API path.

### Anthropic

Anthropic uses different field names for two parameters:

| SharpClaw field | Anthropic wire name |
|---|---|
| `topK` | `top_k` |
| `stop` | `stop_sequences` |

```json
{
  "temperature": 0.7,
  "top_p": 0.9,
  "top_k": 50,
  "stop_sequences": ["\\n", "END"]
}
```

Parameters not supported by Anthropic (`frequencyPenalty`,
`presencePenalty`, `seed`, `responseFormat`, `reasoningEffort`) are
**not sent** — they are omitted from the payload entirely. SharpClaw
validates at agent create/update time to prevent setting them.

### Local inference (LLamaSharp)

Local inference does not use any typed completion parameters. Inference
settings are controlled by the loaded model configuration. Setting any
typed parameter on a Local agent will be rejected by validation.

### OpenAI-compatible providers

All other providers (OpenRouter, Google Vertex AI, Google Gemini, xAI,
Groq, Cerebras, Mistral, GitHub Copilot, ZAI, Vercel AI Gateway,
Minimax) use the same wire format as OpenAI Chat Completions.
`reasoningEffort` is sent as `reasoning_effort` at the top level for
providers that support it (Google Gemini, Google Vertex AI).
Unsupported parameters are omitted from the payload.

---

## Google Gemini / Vertex AI parameter translation

Google providers route through OpenAI-compatible endpoints. SharpClaw
automatically translates native Gemini parameter names in the
`providerParameters` escape-hatch dictionary before sending the request.

This translation is handled by
[`GoogleParameterTranslator`](../SharpClaw.Application.Core/Clients/GoogleParameterTranslator.cs).

### Translation rules

| Native Gemini parameter | Behaviour |
|---|---|
| `"generation_config": { ... }` | Unwrapped — inner keys promoted to top level |
| `"response_mime_type"` | **Rejected** — throws `NotSupportedException` with alternatives |

### `generation_config` unwrapping

The native Gemini API wraps parameters inside a `generation_config`
object. SharpClaw extracts the inner keys and promotes them to the top
level. If the same key exists both inside `generation_config` and at
the top level, the **top-level value takes precedence**.

### `response_mime_type` is not supported

Google's OpenAI-compatible endpoint does not support the simplified
`response_format: {"type": "json_object"}` form — their documentation
only shows the full `json_schema` variant via the SDK's `parse()` API.
Since `response_mime_type` is a native Gemini parameter (not a valid
OpenAI field), it cannot be passed through either.

If `response_mime_type` is present — either at the top level or inside
`generation_config` — SharpClaw throws a `NotSupportedException` with
a clear error message listing the alternatives:

1. Use the typed `responseFormat` completion parameter with a full
   `json_schema` definition
   (`response_format: {"type": "json_schema", ...}`).
2. Instruct the model to respond in JSON via the system prompt.
3. Remove `response_mime_type` and use only supported parameters.

### Examples

`generation_config` unwrapping with supported parameters:

```json
{
  "generation_config": {
    "temperature": 1,
    "top_k": 20
  }
}
```

This is unwrapped to `temperature: 1` and `top_k: 20` at the top level.

The following will produce an error:

```json
{ "response_mime_type": "application/json" }
```

```json
{
  "generation_config": {
    "temperature": 1,
    "response_mime_type": "application/json"
  }
}
```

Both throw a `NotSupportedException` explaining that
`response_mime_type` is not supported through Google's OpenAI-compatible
endpoint and listing alternatives.

---

## `responseFormat` values (OpenAI / OpenAI-compatible)

| Value | Description |
|---|---|
| `{ "type": "text" }` | Plain text output (default) |
| `{ "type": "json_object" }` | Forces valid JSON output (⚠️ not supported by Google — see below) |
| `{ "type": "json_schema", "json_schema": { "name": "...", "schema": { ... } } }` | Structured output with a strict JSON schema |

> ⚠️ When using `"type": "json_schema"` you **must** include the
> `json_schema` object with a `name` and `schema` — omitting it causes
> the provider to return a 400 error.
>
> ⚠️ Google Gemini and Google Vertex AI **do not** support
> `{"type": "json_object"}`. Use the full `json_schema` variant or
> instruct the model via system prompt. SharpClaw catches this at
> validation time.

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

---

## Provider-specific notes

### OpenAI

- Uses the Responses API (`/v1/responses`) by default for all models
  except legacy GPT-3.5 and GPT-4 families, which fall back to Chat
  Completions (`/v1/chat/completions`).
- `reasoningEffort` is only meaningful on o-series and gpt-5 models.
- `seed` is deprecated in the OpenAI API but still accepted.
- `responseFormat` is only sent on the Chat Completions path.
- `topK` is not supported.

### Anthropic

- Uses a dedicated client, not the OpenAI-compatible path.
- `stop` is sent as `stop_sequences`.
- `topK` is sent as `top_k` with no upper bound.
- Does **not** support: `frequencyPenalty`, `presencePenalty`, `seed`,
  `responseFormat`, `reasoningEffort`.

### Mistral

- Temperature is capped at 1.0 (not 2.0).
- Does **not** support: `topK`, `frequencyPenalty`, `presencePenalty`.

### Cerebras

- Temperature is capped at 1.5.
- Does **not** support: `topK`, `frequencyPenalty`, `presencePenalty`.

### Google (Vertex AI / Gemini)

- `topK` maximum is 40.
- Maximum stop sequences is 5 (not 4).
- `responseFormat`: only the full `json_schema` variant is supported
  through Google's OpenAI-compatible endpoint. The simplified
  `{"type": "json_object"}` form is **not** supported — SharpClaw
  rejects it at validation time with an actionable error message
  before the request reaches Google's servers.
- `reasoningEffort`: supported via `reasoning_effort` in Chat
  Completions. Google maps values to internal thinking levels (see
  [reasoningEffort](#reasoningeffort)). Valid values: `"none"` (2.5
  models only), `"minimal"`, `"low"`, `"medium"`, `"high"`. The
  `"xhigh"` value is **not** supported by Google.
- `response_mime_type` in `providerParameters` is **rejected** with a
  `NotSupportedException` — it cannot be translated to any format that
  Google's OpenAI compatibility layer accepts.
- Native Gemini parameters in `providerParameters` are auto-translated
  (see [translation rules](#google-gemini--vertex-ai-parameter-translation)).

### Minimax

- Does **not** support: `topK`, `frequencyPenalty`, `presencePenalty`,
  `seed`, `responseFormat`.

### Local (LLamaSharp)

- No typed parameters are supported. All are controlled by the loaded
  model configuration.

### Custom

- Permissive passthrough. All parameters accepted with wide ranges.
  SharpClaw cannot validate constraints for unknown endpoints.
