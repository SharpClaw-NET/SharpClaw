# OpenRouter

| | |
|---|---|
| **`ProviderType`** | `OpenRouter` (`2`) |
| **Client class** | `OpenRouterApiClient` → `OpenAiCompatibleApiClient` |
| **Endpoint** | `https://openrouter.ai/api/v1` |
| **Auth** | `Authorization: Bearer {apiKey}` |
| **Protocol** | OpenAI Chat Completions (compatible) |
| **Tool calling** | ✅ Native |
| **API docs** | https://openrouter.ai/docs/parameters |

---

## Completion parameters

| Parameter | Supported | Range / values |
|---|---|---|
| `temperature` | ✅ | `0.0` – `2.0` |
| `topP` | ✅ | `0.0` – `1.0` |
| `topK` | ✅ | `1` – ∞ (passthrough to underlying model) |
| `frequencyPenalty` | ✅ | `-2.0` – `2.0` |
| `presencePenalty` | ✅ | `-2.0` – `2.0` |
| `stop` | ✅ | Up to **4** sequences |
| `seed` | ✅ | Any integer |
| `responseFormat` | ✅ | `json_object` and `json_schema` |
| `reasoningEffort` | ❌ | — |

---

## Wire format

Standard OpenAI Chat Completions format:

```json
{
  "temperature": 0.7,
  "top_p": 0.9,
  "top_k": 50,
  "frequency_penalty": 0.5,
  "presence_penalty": 0.3,
  "stop": ["\n", "END"],
  "seed": 42,
  "response_format": { "type": "json_object" }
}
```

---

## Notes

- OpenRouter is a multi-model gateway — parameter support varies by the
  underlying model. `topK` is passed through directly; the downstream
  provider decides whether to honour it.
- No `reasoningEffort` support through OpenRouter's API.
- Actual parameter ranges may be further constrained by the model you
  route to. SharpClaw validates against OpenRouter's documented limits;
  the underlying model may reject values that pass SharpClaw validation.

---

## `providerParameters` examples

```json
{
  "transforms": ["middle-out"],
  "route": "fallback"
}
```

→ [Back to overview](../Provider-Parameters.md)
