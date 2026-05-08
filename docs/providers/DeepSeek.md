# DeepSeek

| | |
|---|---|
| **`ProviderType`** | `DeepSeek` (`deepseek`) |
| **Client class** | `DeepSeekApiClient` → `OpenAiCompatibleApiClient` |
| **Endpoint** | `https://api.deepseek.com` |
| **Auth** | `Authorization: Bearer {apiKey}` |
| **Protocol** | OpenAI Chat Completions (compatible) |
| **Tool calling** | ✅ Native |
| **API docs** | https://api-docs.deepseek.com/ |

---

## Completion parameters

| Parameter | Supported | Range / values |
|---|---|---|
| `temperature` | ✅ | `0.0` – `2.0` |
| `topP` | ✅ | `0.0` – `1.0` |
| `topK` | ❌ | — |
| `frequencyPenalty` | ❌ | — |
| `presencePenalty` | ❌ | — |
| `stop` | ✅ | Up to **16** sequences |
| `seed` | ❌ | — |
| `responseFormat` | ✅ | `json_object` only |
| `reasoningEffort` | ✅ | `"low"`, `"medium"`, `"high"`, `"xhigh"`, `"max"` |

---

## Wire format

Standard OpenAI Chat Completions format with DeepSeek thinking mode
disabled by default:

```json
{
  "thinking": { "type": "disabled" },
  "temperature": 0.7,
  "top_p": 0.9,
  "stop": ["\n", "END"],
  "response_format": { "type": "json_object" }
}
```

---

## Notes

- DeepSeek uses `POST https://api.deepseek.com/chat/completions` and
  `GET https://api.deepseek.com/models`.
- SharpClaw sends `thinking: { "type": "disabled" }` by default because
  DeepSeek enables thinking upstream.
- `reasoningEffort` enables thinking mode and maps to top-level
  `reasoning_effort`.
- In thinking mode, SharpClaw preserves DeepSeek `reasoning_content` as
  hidden provider metadata and replays it across tool-call turns.
- `frequency_penalty` and `presence_penalty` are deprecated/no-op fields
  in the DeepSeek API.
- Strict tool schemas require DeepSeek's beta endpoint, so
  `strictTools` is not supported yet.
- Current primary model ids are `deepseek-v4-flash` and
  `deepseek-v4-pro`. Legacy aliases `deepseek-chat` and
  `deepseek-reasoner` are documented as deprecated after
  **2026-07-24**.

→ [Back to overview](../Provider-Parameters.md)
