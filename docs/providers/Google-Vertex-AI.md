# Google Vertex AI (native)

| | |
|---|---|
| **`ProviderType`** | `GoogleVertexAI` (`3`) |
| **Client class** | `GoogleVertexAIApiClient` (dedicated native client) |
| **Endpoint** | `https://aiplatform.googleapis.com/v1/{MODEL_RESOURCE}:generateContent` |
| **Auth** | `Authorization: Bearer {accessToken}` |
| **Protocol** | Native Vertex AI `generateContent` / `streamGenerateContent` |
| **Tool calling** | ✅ Native |
| **API docs** | https://cloud.google.com/vertex-ai/generative-ai/docs |

---

## Overview

This provider type calls Vertex AI's native Gemini `generateContent`
API directly. It is not the OpenAI-compatible Vertex endpoint.

For raw model IDs such as `gemini-2.5-flash`, configure the provider
endpoint as a project/location root:

```text
https://{LOCATION}-aiplatform.googleapis.com/v1/projects/{PROJECT}/locations/{LOCATION}
```

The generic REST root is also valid for fully qualified model resources:

```text
https://aiplatform.googleapis.com/v1
```

You may also use a fully qualified model name as the SharpClaw model
name:

```text
projects/{PROJECT}/locations/{LOCATION}/publishers/google/models/{MODEL}
```

The provider API key field must contain a Google Cloud OAuth access
token. The token may be stored either as the raw token or as
`Bearer {token}`.

---

## Completion parameters

| Parameter | Supported | Range / values |
|---|---|---|
| `temperature` | ✅ | `0.0` – `2.0` |
| `topP` | ✅ | `0.0` – `1.0` |
| `topK` | ✅ | Model-dependent integer |
| `frequencyPenalty` | ✅ | `-2.0` – `2.0` |
| `presencePenalty` | ✅ | `-2.0` – `2.0` |
| `stop` | ✅ | Up to **5** sequences |
| `seed` | ✅ | Any integer |
| `responseFormat` | ✅ | Mapped to `responseMimeType` (see below) |
| `reasoningEffort` | ✅ | `"none"`, `"minimal"`, `"low"`, `"medium"`, `"high"` |
| `toolChoice` | ✅ | `auto`, `none`, `required`, named function |

---

## Wire format

The native client builds a Vertex AI Gemini-schema request body.
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
    "presencePenalty": 0.1,
    "frequencyPenalty": 0.1,
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
  ],
  "toolConfig": {
    "functionCallingConfig": {
      "mode": "ANY"
    }
  }
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
extracted and set as `generationConfig.responseSchema`.

---

## `reasoningEffort` mapping

Reasoning effort is mapped to `thinkingConfig.thinkingBudget` inside
`generationConfig`:

| `reasoningEffort` | `thinkingBudget` (tokens) |
|---|---|
| `"none"` | `0` |
| `"minimal"` | `1 024` |
| `"low"` | `1 024` |
| `"medium"` | `8 192` |
| `"high"` | `24 576` |

For models that prefer `thinkingConfig.thinkingLevel`, use
`providerParameters`.

---

## Tool choice mapping

When native tools are present, SharpClaw maps `toolChoice` to
`toolConfig.functionCallingConfig`:

| `toolChoice` | `functionCallingConfig.mode` |
|---|---|
| `auto` / omitted | omitted (provider default) |
| `none` | `NONE` |
| `required` | `ANY` |
| named function | `ANY` with `allowedFunctionNames` |

---

## Model listing

The native client supports listing project-scoped Vertex models when
the provider endpoint includes project and location:

```text
GET /v1/projects/{PROJECT}/locations/{LOCATION}/models
```

Publisher Gemini model IDs are not listed by this endpoint; add those
models manually or use fully qualified model resource names.

---

## Streaming

Streaming uses the native `streamGenerateContent` endpoint with
`alt=sse`:

```text
POST /v1/projects/{PROJECT}/locations/{LOCATION}/publishers/google/models/{MODEL}:streamGenerateContent?alt=sse
```

SSE events contain `data: ` lines with the same
`GenerateContentResponse` JSON structure. Token usage is extracted from
the final chunk's `usageMetadata`.

---

## `providerParameters` examples

Provider parameters are merged additively into the native Vertex AI
request. Known `GenerationConfig` fields are merged into
`generationConfig`; all other known fields remain top-level native
request fields.

Top-level `generationConfig` and `generation_config` objects are merged
into `generationConfig`. Snake_case generation config keys are
normalized to Vertex REST lowerCamelCase, so this:

```json
{
  "response_mime_type": "application/json"
}
```

is sent as:

```json
{
  "generationConfig": {
    "responseMimeType": "application/json"
  }
}
```

Vertex-specific generation config fields are supported:

```json
{
  "generation_config": {
    "response_mime_type": "application/json",
    "audio_timestamp": true,
    "routing_config": {
      "autoMode": {
        "modelRoutingPreference": "BALANCED"
      }
    }
  }
}
```

Top-level native fields can be supplied in lowerCamelCase or snake_case:

```json
{
  "labels": {
    "workload": "sharpclaw"
  },
  "model_armor_config": {
    "someOption": true
  }
}
```

> **Note:** If a `providerParameters` key conflicts with a key already
> set by the client (e.g. `contents`, `systemInstruction`, `tools`), the
> client's value takes precedence and the user-supplied key is skipped.
> The same precedence applies inside `generationConfig`: typed
> SharpClaw fields such as `responseFormat`, `temperature`, and `topP`
> win over provider parameter values.

---

## Differences from `GoogleVertexAIOpenAi`

| | `GoogleVertexAI` (native) | `GoogleVertexAIOpenAi` (OAI-compat) |
|---|---|---|
| Endpoint | `generateContent` | `/v1beta1/openai/chat/completions` |
| Parameter schema | Vertex AI native | OpenAI-compatible |
| Auth | Bearer access token | Bearer access token |
| `topK` | ✅ | ❌ |
| `responseMimeType` | ✅ (via `responseFormat` or `providerParameters`) | ❌ |
| `safetySettings` | ✅ | ❌ |
| `labels` | ✅ | ❌ |
| `modelArmorConfig` | ✅ | ❌ |
| `json_object` response format | ✅ | ❌ (only `json_schema`) |

→ [Back to overview](../Provider-Parameters.md)
