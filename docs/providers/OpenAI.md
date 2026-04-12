# OpenAI

| | |
|---|---|
| **`ProviderType`** | `OpenAI` (`0`) |
| **Client class** | `OpenAiApiClient` → `OpenAiCompatibleApiClient` |
| **Endpoint** | `https://api.openai.com/v1` |
| **Auth** | `Authorization: Bearer {apiKey}` |
| **Protocol** | OpenAI Chat Completions + Responses API |
| **Tool calling** | ✅ Native |
| **API docs** | https://platform.openai.com/docs/api-reference |

---

## API routing

SharpClaw prefers the **Responses API** (`/v1/responses`) for all models
except legacy GPT-3.5 and GPT-4 families, which fall back to **Chat
Completions** (`/v1/chat/completions`).

The routing is automatic — the client inspects the model name and
chooses the correct path. No configuration is needed.

---

## Completion parameters

| Parameter | Supported | Range / values |
|---|---|---|
| `temperature` | ✅ | `0.0` – `2.0` |
| `topP` | ✅ | `0.0` – `1.0` |
| `topK` | ❌ | — |
| `frequencyPenalty` | ✅ | `-2.0` – `2.0` |
| `presencePenalty` | ✅ | `-2.0` – `2.0` |
| `stop` | ✅ | Up to **4** sequences |
| `seed` | ✅ | Any integer (deprecated upstream but still accepted) |
| `responseFormat` | ✅ | Chat Completions path only. `json_object` and `json_schema` both supported. |
| `reasoningEffort` | ✅ | `"none"`, `"minimal"`, `"low"`, `"medium"`, `"high"`, `"xhigh"` |

---

## Wire format

### Chat Completions

```json
{
  "temperature": 0.7,
  "top_p": 0.9,
  "frequency_penalty": 0.5,
  "presence_penalty": 0.3,
  "stop": ["\n", "END"],
  "seed": 42,
  "response_format": { "type": "json_object" }
}
```

### Responses API

```json
{
  "temperature": 0.7,
  "top_p": 0.9,
  "reasoning": { "effort": "medium" }
}
```

`responseFormat` is **not** sent on the Responses API path.
`reasoningEffort` is mapped to `reasoning.effort` (Responses API) or
`reasoning_effort` at the top level (Chat Completions).

---

## Notes

- `reasoningEffort` is only meaningful on o-series and gpt-5 models.
  Setting it on other models has no effect.
- `seed` is deprecated in the OpenAI API but still accepted.
  Two requests with the same seed and parameters should produce
  identical output (best-effort, not guaranteed).
- `topK` is not available on OpenAI. Use `topP` instead.
- The Responses API does not support `responseFormat`. Use a system
  prompt or structured outputs instead.

---

## `providerParameters` examples

```json
{
  "logprobs": true,
  "top_logprobs": 5
}
```

Keys that the client already sets (`model`, `messages`, `tools`) are
never overwritten — user-supplied parameters are additive only.

→ [Back to overview](../Provider-Parameters.md)
