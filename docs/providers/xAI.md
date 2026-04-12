# xAI (Grok)

| | |
|---|---|
| **`ProviderType`** | `XAI` (`7`) |
| **Client class** | `XAIApiClient` → `OpenAiCompatibleApiClient` |
| **Endpoint** | `https://api.x.ai/v1` |
| **Auth** | `Authorization: Bearer {apiKey}` |
| **Protocol** | OpenAI Chat Completions (compatible) |
| **Tool calling** | ✅ Native |
| **API docs** | https://docs.x.ai/docs |

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
  "frequency_penalty": 0.5,
  "presence_penalty": 0.3,
  "stop": ["\n", "END"],
  "seed": 42,
  "response_format": { "type": "json_object" }
}
```

---

## Notes

- Grok models are accessed through xAI's OpenAI-compatible API.
- No `topK` or `reasoningEffort` support.
- No provider-specific parameter translation — parameters are passed
  through using the standard OpenAI-compatible format.

→ [Back to overview](../Provider-Parameters.md)
