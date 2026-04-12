# Google Vertex AI (native)

> ⚠️ **Not yet implemented.** This provider type is a stub that reserves
> the `GoogleVertexAI` enum value for the future native Vertex AI
> `generateContent` protocol. All client methods throw
> `NotSupportedException`. Use
> [`GoogleVertexAIOpenAi`](Google-Vertex-AI-OpenAI.md) instead.

| | |
|---|---|
| **`ProviderType`** | `GoogleVertexAI` (`3`) |
| **Client class** | `GoogleVertexAIApiClient` (stub) |
| **Endpoint** | `https://{LOCATION}-aiplatform.googleapis.com/v1/projects/{PROJECT}/locations/{LOCATION}/publishers/google/models/{MODEL}:generateContent` |
| **Auth** | TBD (OAuth 2.0 / service account expected) |
| **Protocol** | Native Vertex AI `generateContent` / `streamGenerateContent` |
| **Tool calling** | TBD |
| **API docs** | https://cloud.google.com/vertex-ai/generative-ai/docs |

---

## Status

The native Vertex AI protocol uses the same `generateContent` request
schema as the native Gemini API ([`GoogleGemini`](Google-Gemini.md)),
but routes through Vertex AI project endpoints with GCP authentication.
The parameter spec mirrors `GoogleGemini` (native) for
forward-compatibility:

| Parameter | Supported | Range / values |
|---|---|---|
| `temperature` | ✅ | `0.0` – `2.0` |
| `topP` | ✅ | `0.0` – `1.0` |
| `topK` | ✅ | `1` – **`40`** |
| `frequencyPenalty` | ❌ | — (not available on native endpoint) |
| `presencePenalty` | ❌ | — (not available on native endpoint) |
| `stop` | ✅ | Up to **5** sequences |
| `seed` | ✅ | Any integer |
| `responseFormat` | ✅ | Mapped to `responseMimeType` (same as `GoogleGemini`) |
| `reasoningEffort` | ✅ | `"none"`, `"minimal"`, `"low"`, `"medium"`, `"high"` |

---

## What to use now

Use [`GoogleVertexAIOpenAi`](Google-Vertex-AI-OpenAI.md) (`16`) for
production workloads. It routes through Vertex AI's OpenAI-compatible
endpoint and supports all standard OpenAI Chat Completions parameters.

If you need native Gemini parameters (`responseMimeType`,
`safetySettings`, `thinkingConfig`) and do not require Vertex AI
project-scoped routing, use [`GoogleGemini`](Google-Gemini.md) (`4`)
which is fully implemented.

---

## See also

- [`GoogleVertexAIOpenAi`](Google-Vertex-AI-OpenAI.md) — Vertex AI via
  the OpenAI-compatible endpoint (implemented, recommended)
- [`GoogleGemini`](Google-Gemini.md) — native Gemini API (implemented)
- [`GoogleGeminiOpenAi`](Google-Gemini-OpenAI.md) — Gemini via the
  OpenAI-compatible endpoint

→ [Back to overview](../Provider-Parameters.md)
