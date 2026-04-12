# Custom (OpenAI-compatible)

| | |
|---|---|
| **`ProviderType`** | `Custom` (`12`) |
| **Client class** | `CustomOpenAiCompatibleApiClient` → `OpenAiCompatibleApiClient` |
| **Endpoint** | User-configured per provider instance |
| **Auth** | `Authorization: Bearer {apiKey}` |
| **Protocol** | OpenAI Chat Completions (compatible) |
| **Tool calling** | ✅ Native |

---

## Completion parameters

| Parameter | Supported | Range / values |
|---|---|---|
| `temperature` | ✅ | `0.0` – `2.0` |
| `topP` | ✅ | `0.0` – `1.0` |
| `topK` | ✅ | `1` – ∞ |
| `frequencyPenalty` | ✅ | `-2.0` – `2.0` |
| `presencePenalty` | ✅ | `-2.0` – `2.0` |
| `stop` | ✅ | Up to **16** sequences |
| `seed` | ✅ | Any integer |
| `responseFormat` | ✅ | `json_object` and `json_schema` |
| `reasoningEffort` | ✅ | `"none"`, `"minimal"`, `"low"`, `"medium"`, `"high"`, `"xhigh"` |

**All parameters are accepted** with permissive ranges. SharpClaw cannot
validate constraints for unknown endpoints — the actual support depends
entirely on your endpoint.

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
  "response_format": { "type": "json_object" },
  "reasoning_effort": "medium"
}
```

---

## Notes

- The endpoint URL is configured per provider instance, not baked into
  the client. This lets you point to any OpenAI-compatible server
  (vLLM, Ollama, llama.cpp, text-generation-inference, etc.).
- Validation is intentionally permissive — if the endpoint rejects a
  parameter, you'll get the upstream error.
- No provider-specific parameter translation.
- `providerParameters` can supply any additional key-value pairs your
  endpoint supports.

→ [Back to overview](../Provider-Parameters.md)
