# Google Gemini (OpenAI-compatible)

| | |
|---|---|
| **`ProviderType`** | `GoogleGeminiOpenAi` (`15`) |
| **Client class** | `GoogleGeminiOpenAiApiClient` → `OpenAiCompatibleApiClient` |
| **Endpoint** | `https://generativelanguage.googleapis.com/v1beta/openai` |
| **Auth** | `Authorization: Bearer {apiKey}` |
| **Protocol** | OpenAI Chat Completions (compatible) |
| **Tool calling** | ✅ Native |
| **API docs** | https://ai.google.dev/gemini-api/docs/openai |

---

## Overview

This provider type uses Google's OpenAI-compatible wrapper for Gemini
models. Parameters follow the standard OpenAI schema.

If you need native Gemini parameters like `responseMimeType`,
`safetySettings`, or `thinkingConfig`, use
[`GoogleGemini`](Google-Gemini.md) (native) instead.

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

Standard OpenAI Chat Completions format:

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

If you pass a `generation_config` wrapper in `providerParameters`, inner
keys are automatically promoted to the top level:

```json
// You send:
{ "generation_config": { "candidate_count": 2 } }

// Wire result:
{ "candidate_count": 2, ... }
```

This is handled by `GoogleParameterTranslator`. Top-level keys set
directly by the user take precedence over unwrapped keys.

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

SharpClaw catches this at **validation time** before the request reaches
Google's servers.

---

## `reasoningEffort` mapping

See the [Google Vertex AI (OpenAI)](Google-Vertex-AI-OpenAI.md#reasoningeffort-mapping)
doc for the full mapping table — the same values and behaviour apply.
`"xhigh"` is **not** supported.

---

## Notes

- Native Gemini parameters like `responseMimeType` are **not valid**
  on the OpenAI-compatible endpoint and will be silently ignored.
- Uses the same `GoogleParameterTranslator` as `GoogleVertexAIOpenAi`.
- See also: [`GoogleGemini`](Google-Gemini.md) for native parameter
  support, [`GoogleVertexAIOpenAi`](Google-Vertex-AI-OpenAI.md) for
  Vertex AI.

→ [Back to overview](../Provider-Parameters.md)
