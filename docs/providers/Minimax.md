# Minimax

| | |
|---|---|
| **`ProviderType`** | `Minimax` (`14`) |
| **Client class** | `MinimaxApiClient` → `OpenAiCompatibleApiClient` |
| **Endpoint** | `https://api.minimaxi.com/v1` |
| **Auth** | `Authorization: Bearer {apiKey}` |
| **Protocol** | OpenAI Chat Completions (compatible) |
| **Tool calling** | ✅ Native |
| **API docs** | https://platform.minimaxi.com/document |

---

## Completion parameters

| Parameter | Supported | Range / values |
|---|---|---|
| `temperature` | ✅ | `0.0` – `2.0` |
| `topP` | ✅ | `0.0` – `1.0` |
| `topK` | ❌ | — |
| `frequencyPenalty` | ❌ | — |
| `presencePenalty` | ❌ | — |
| `stop` | ✅ | Up to **4** sequences |
| `seed` | ❌ | — |
| `responseFormat` | ❌ | — |
| `reasoningEffort` | ❌ | — |

---

## Wire format

Standard OpenAI Chat Completions format (only supported fields are
sent):

```json
{
  "temperature": 0.7,
  "top_p": 0.9,
  "stop": ["\n", "END"]
}
```

---

## Notes

- Does **not** support `topK`, `frequencyPenalty`, `presencePenalty`,
  `seed`, `responseFormat`, or `reasoningEffort`.
- This is the most restricted OpenAI-compatible provider in terms of
  typed parameter support.
- No provider-specific parameter translation.

→ [Back to overview](../Provider-Parameters.md)
