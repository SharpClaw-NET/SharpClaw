# Google Vertex AI (OpenAI-compatible)

| | |
|---|---|
| **`ProviderType`** | `GoogleVertexAIOpenAi` (`16`) |
| **Client class** | `GoogleVertexAIOpenAiApiClient` → `OpenAiCompatibleApiClient` |
| **Endpoint** | `https://us-central1-aiplatform.googleapis.com/v1beta1/openai` |
| **Auth** | `Authorization: Bearer {apiKey}` |
| **Protocol** | OpenAI Chat Completions (compatible) via Vertex AI |
| **Tool calling** | ✅ Native |
| **API docs** | https://cloud.google.com/vertex-ai/generative-ai/docs |

---

## Overview

This provider type uses Google Vertex AI's OpenAI-compatible wrapper.
Parameters follow the standard OpenAI schema.

If you need native Vertex AI or Gemini parameters like
`responseMimeType`, `safetySettings`, or `thinkingConfig`, use
[`GoogleGemini`](Google-Gemini.md) (native, implemented) or wait for
[`GoogleVertexAI`](Google-Vertex-AI.md) (native, not yet implemented).

---

## Completion parameters

| Parameter | Supported | Range / values |
|---|---|---|
| `temperature` | ✅ | `0.0` – `2.0` |
| `topP` | ✅ | `0.0` – `1.0` |
| `topK` | ❌ | — (not available on OpenAI-compatible endpoint; use `providerParameters` with `extra_body`) |
| `frequencyPenalty` | ✅ | `-2.0` – `2.0` |
| `presencePenalty` | ✅ | `-2.0` – `2.0` |
| `stop` | ✅ | Up to **5** sequences |
| `seed` | ✅ | Any integer |
| `responseFormat` | ⚠️ | `json_schema` only — `json_object` is **rejected** |
| `reasoningEffort` | ✅ | `"none"`, `"minimal"`, `"low"`, `"medium"`, `"high"` |

---

## Wire format

Standard OpenAI Chat Completions format. `reasoningEffort` is sent as
`reasoning_effort` at the top level:

```json
{
  "temperature": 0.7,
  "top_p": 0.9,
  "frequency_penalty": 0.5,
  "presence_penalty": 0.3,
  "stop": ["\n"],
  "seed": 42,
  "response_format": {
    "type": "json_schema",
    "json_schema": { "name": "my_schema", "schema": { ... } }
  },
  "reasoning_effort": "medium"
}
```

---

## `generation_config` unwrapping

If you pass a `generation_config` wrapper in `providerParameters`, its
inner keys are automatically promoted to the top level. This is handled
by
[`GoogleParameterTranslator`](../../SharpClaw.Application.Core/Clients/GoogleParameterTranslator.cs).

```json
// You send:
{ "generation_config": { "candidate_count": 2 } }

// Wire result:
{ "candidate_count": 2, ... }
```

Top-level keys set directly by the user take precedence over keys
unwrapped from `generation_config`.

---

## `responseFormat` restriction

Google's OpenAI-compatible endpoint does **not** support the simplified
`{"type": "json_object"}` form. Only the full `json_schema` variant is
accepted:

```json
{
  "type": "json_schema",
  "json_schema": {
    "name": "my_output",
    "schema": { "type": "object", "properties": { ... } }
  }
}
```

SharpClaw catches this at **validation time** with an actionable error
message before the request ever reaches Google's servers.

---

## `reasoningEffort` mapping

Google maps reasoning effort values to internal thinking levels:

| `reasoningEffort` | Gemini 3.1 Pro | Gemini 3.1 Flash-Lite | Gemini 3 Flash | Gemini 2.5 |
|---|---|---|---|---|
| `minimal` | `low` | `minimal` | `minimal` | budget 1 024 |
| `low` | `low` | `low` | `low` | budget 1 024 |
| `medium` | `medium` | `medium` | `medium` | budget 8 192 |
| `high` | `high` | `high` | `high` | budget 24 576 |

`"none"` disables thinking on Gemini 2.5 models only. Reasoning cannot
be turned off for Gemini 2.5 Pro or Gemini 3+ models. `"xhigh"` is
**not** supported.

---

## Notes

- Uses the same `GoogleParameterTranslator` as `GoogleGeminiOpenAi`.
- If you need native Gemini parameters like `responseMimeType` or
  `safetySettings`, use the [`GoogleGemini`](Google-Gemini.md) provider
  type instead.
- See also: [`GoogleGeminiOpenAi`](Google-Gemini-OpenAI.md) for the
  non-Vertex OpenAI-compatible path.

→ [Back to overview](../Provider-Parameters.md)
