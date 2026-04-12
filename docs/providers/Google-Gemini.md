# Google Gemini (native)

| | |
|---|---|
| **`ProviderType`** | `GoogleGemini` (`4`) |
| **Client class** | `GoogleGeminiApiClient` (dedicated native client) |
| **Endpoint** | `https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent` |
| **Auth** | `x-goog-api-key: {apiKey}` |
| **Protocol** | Native Gemini `generateContent` / `streamGenerateContent` |
| **Tool calling** | ✅ Native |
| **API docs** | https://ai.google.dev/gemini-api/docs |

---

## Overview

This provider type calls Google's native Gemini API directly — **not**
the OpenAI-compatible wrapper. Provider parameters follow the Gemini
schema and are passed through as-is.

If you prefer to use OpenAI-compatible parameter names, use
[`GoogleGeminiOpenAi`](Google-Gemini-OpenAI.md) instead.

---

## Completion parameters

| Parameter | Supported | Range / values |
|---|---|---|
| `temperature` | ✅ | `0.0` – `2.0` |
| `topP` | ✅ | `0.0` – `1.0` |
| `topK` | ✅ | `1` – **`40`** |
| `frequencyPenalty` | ❌ | — (not available on native endpoint) |
| `presencePenalty` | ❌ | — (not available on native endpoint) |
| `stop` | ✅ | Up to **5** sequences |
| `seed` | ✅ | Any integer |
| `responseFormat` | ✅ | Mapped to `responseMimeType` (see below) |
| `reasoningEffort` | ✅ | `"none"`, `"minimal"`, `"low"`, `"medium"`, `"high"` |

---

## Wire format

The native client builds a Gemini-schema request body.
`CompletionParameters` are mapped into `generationConfig`:

```json
{
  "contents": [
    { "role": "user", "parts": [{ "text": "Hello" }] }
  ],
  "systemInstruction": {
    "parts": [{ "text": "You are a helpful assistant." }]
  },
  "generationConfig": {
    "temperature": 0.7,
    "topP": 0.9,
    "topK": 40,
    "maxOutputTokens": 1024,
    "stopSequences": ["\n"],
    "seed": 42,
    "responseMimeType": "application/json",
    "thinkingConfig": {
      "thinkingBudget": 8192
    }
  },
  "tools": [
    {
      "functionDeclarations": [
        {
          "name": "get_weather",
          "description": "Get the current weather",
          "parameters": { "type": "object", "properties": { ... } }
        }
      ]
    }
  ]
}
```

---

## `responseFormat` mapping

The typed `responseFormat` field is mapped to `responseMimeType` inside
`generationConfig`:

| `responseFormat` value | `responseMimeType` | `responseSchema` |
|---|---|---|
| `"application/json"` (string) | `"application/json"` | — |
| `{ "type": "json_object" }` | `"application/json"` | — |
| `{ "type": "json_schema", "json_schema": { "schema": {…} } }` | `"application/json"` | Extracted from `json_schema.schema` |
| `{ "type": "text" }` | `"text/plain"` | — |
| Other / missing | `"text/plain"` | — |

When the `json_schema` variant is used, the inner `schema` object is
extracted and set as `generationConfig.responseSchema` so that the
native API can enforce structured output. The schema is passed through
as-is — no format translation is performed.

Unlike the OpenAI-compatible Google endpoints, the native endpoint
**accepts** `json_object` — it is translated to the equivalent
`responseMimeType` value.

---

## `reasoningEffort` mapping

Reasoning effort is mapped to `thinkingConfig.thinkingBudget` inside
`generationConfig`. Values are aligned with Google's documented mapping:

| `reasoningEffort` | `thinkingBudget` (tokens) |
|---|---|
| `"none"` | `0` |
| `"minimal"` | `1 024` |
| `"low"` | `1 024` |
| `"medium"` | `8 192` |
| `"high"` | `24 576` |

`"none"` disables thinking on Gemini 2.5 models only. Reasoning cannot
be turned off for Gemini 2.5 Pro or Gemini 3+ models. `"xhigh"` is
**not** supported — it defaults to `"medium"` (8 192 tokens).

> For Gemini 3.x models that prefer the `thinkingLevel` string enum
> over `thinkingBudget`, use `providerParameters` to pass
> `thinkingConfig.thinkingLevel` directly.

---

## Model listing

The native client supports model listing via:

```
GET /v1beta/models?key={apiKey}
```

Model names are returned with a `models/` prefix that is automatically
stripped (e.g. `models/gemini-2.5-flash` → `gemini-2.5-flash`).

---

## Streaming

Streaming uses the `streamGenerateContent` endpoint with `alt=sse`:

```
POST /v1beta/models/{model}:streamGenerateContent?alt=sse
```

SSE events contain `data: ` lines with the same
`GenerateContentResponse` JSON structure. Token usage is extracted from
the final chunk's `usageMetadata`.

---

## `providerParameters` examples

Provider parameters are merged additively into the top-level request
body. They can supply any native Gemini field:

```json
{
  "safetySettings": [
    {
      "category": "HARM_CATEGORY_DANGEROUS_CONTENT",
      "threshold": "BLOCK_ONLY_HIGH"
    }
  ]
}
```

```json
{
  "generationConfig": {
    "responseSchema": {
      "type": "object",
      "properties": {
        "name": { "type": "string" },
        "age": { "type": "integer" }
      }
    }
  }
}
```

> **Note:** If a `providerParameters` key conflicts with a key already
> set by the client (e.g. `contents`, `systemInstruction`, `tools`), the
> client's value takes precedence and the user-supplied key is skipped.

---

## Differences from `GoogleGeminiOpenAi`

| | `GoogleGemini` (native) | `GoogleGeminiOpenAi` (OAI-compat) |
|---|---|---|
| Endpoint | `generateContent` | `/v1beta/openai/chat/completions` |
| Parameter schema | Gemini native | OpenAI-compatible |
| `frequencyPenalty` | ❌ | ✅ |
| `presencePenalty` | ❌ | ✅ |
| `responseMimeType` | ✅ (via `responseFormat` or `providerParameters`) | ❌ |
| `safetySettings` | ✅ | ❌ |
| `json_object` response format | ✅ | ❌ (only `json_schema`) |

→ [Back to overview](../Provider-Parameters.md)
