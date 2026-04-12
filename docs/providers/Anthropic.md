# Anthropic

| | |
|---|---|
| **`ProviderType`** | `Anthropic` (`1`) |
| **Client class** | `AnthropicApiClient` (dedicated, not OpenAI-compatible) |
| **Endpoint** | `https://api.anthropic.com/v1` |
| **Auth** | `x-api-key: {apiKey}` + `anthropic-version: 2023-06-01` |
| **Protocol** | Anthropic Messages API |
| **Tool calling** | ✅ Native |
| **API docs** | https://docs.anthropic.com/en/api/messages |

---

## Completion parameters

| Parameter | Supported | Range / values |
|---|---|---|
| `temperature` | ✅ | `0.0` – **`1.0`** |
| `topP` | ✅ | `0.0` – `1.0` |
| `topK` | ✅ | `1` – ∞ (no documented ceiling) |
| `frequencyPenalty` | ❌ | — |
| `presencePenalty` | ❌ | — |
| `stop` | ✅ | Up to **8 192** sequences |
| `seed` | ❌ | — |
| `responseFormat` | ❌ | — |
| `reasoningEffort` | ❌ | — |

---

## Wire format

Anthropic uses different field names for two parameters:

| SharpClaw field | Anthropic wire name |
|---|---|
| `topK` | `top_k` |
| `stop` | `stop_sequences` |

```json
{
  "temperature": 0.7,
  "top_p": 0.9,
  "top_k": 50,
  "stop_sequences": ["\n", "END"]
}
```

Unsupported parameters (`frequencyPenalty`, `presencePenalty`, `seed`,
`responseFormat`, `reasoningEffort`) are **omitted** from the payload
entirely. SharpClaw validates at agent create/update time and rejects
them before the request is sent.

---

## Notes

- Temperature is capped at **1.0** (not 2.0 like most OpenAI-compatible
  providers). Values above 1.0 are rejected at validation time.
- `topK` has no documented upper bound — Anthropic accepts any positive
  integer.
- `stop_sequences` supports up to 8 192 entries, far more than any other
  provider.
- This is a dedicated client — it does **not** extend
  `OpenAiCompatibleApiClient`.

---

## `providerParameters` examples

```json
{
  "metadata": { "user_id": "abc-123" }
}
```

→ [Back to overview](../Provider-Parameters.md)
