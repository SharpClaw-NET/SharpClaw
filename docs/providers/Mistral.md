# Mistral

| | |
|---|---|
| **`ProviderType`** | `Mistral` (`10`) |
| **Client class** | `MistralApiClient` → `OpenAiCompatibleApiClient` |
| **Endpoint** | `https://api.mistral.ai/v1` |
| **Auth** | `Authorization: Bearer {apiKey}` |
| **Protocol** | OpenAI Chat Completions (compatible) |
| **Tool calling** | ✅ Native |
| **API docs** | https://docs.mistral.ai/api/ |

---

## Completion parameters

| Parameter | Supported | Range / values |
|---|---|---|
| `temperature` | ✅ | `0.0` – **`1.0`** |
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

- Temperature is capped at **1.0** (not 2.0). Values above 1.0 are
  rejected at validation time.
- Does **not** support `topK`, `frequencyPenalty`, or `presencePenalty`.
- No `reasoningEffort` support.
- No provider-specific parameter translation.

→ [Back to overview](../Provider-Parameters.md)
