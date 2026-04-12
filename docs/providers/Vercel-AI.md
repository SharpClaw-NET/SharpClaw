# Vercel AI Gateway

| | |
|---|---|
| **`ProviderType`** | `VercelAIGateway` (`6`) |
| **Client class** | `VercelAIGatewayApiClient` → `OpenAiCompatibleApiClient` |
| **Endpoint** | `https://gateway.ai.vercel.app/v1` |
| **Auth** | `Authorization: Bearer {apiKey}` |
| **Protocol** | OpenAI Chat Completions (compatible) |
| **Tool calling** | ✅ Native |

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

- Vercel AI Gateway routes to multiple providers — actual parameter
  support depends on the underlying model.
- No `topK` or `reasoningEffort` support.
- No provider-specific parameter translation.

→ [Back to overview](../Provider-Parameters.md)
