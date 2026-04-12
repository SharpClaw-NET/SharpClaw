# Cerebras

| | |
|---|---|
| **`ProviderType`** | `Cerebras` (`9`) |
| **Client class** | `CerebrasApiClient` → `OpenAiCompatibleApiClient` |
| **Endpoint** | `https://api.cerebras.ai/v1` |
| **Auth** | `Authorization: Bearer {apiKey}` |
| **Protocol** | OpenAI Chat Completions (compatible) |
| **Tool calling** | ✅ Native |
| **API docs** | https://inference-docs.cerebras.ai/api-reference |

---

## Completion parameters

| Parameter | Supported | Range / values |
|---|---|---|
| `temperature` | ✅ | `0.0` – **`1.5`** |
| `topP` | ✅ | `0.0` – `1.0` |
| `topK` | ❌ | — |
| `frequencyPenalty` | ❌ | — |
| `presencePenalty` | ❌ | — |
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
  "stop": ["\n", "END"],
  "seed": 42,
  "response_format": { "type": "json_object" }
}
```

---

## Notes

- Temperature is capped at **1.5** (not 2.0).
- Does **not** support `topK`, `frequencyPenalty`, or `presencePenalty`.
- No `reasoningEffort` support.
- No provider-specific parameter translation.

→ [Back to overview](../Provider-Parameters.md)
