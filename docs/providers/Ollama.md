# Ollama

| | |
|---|---|
| **`ProviderType`** | `Ollama` (`18`) |
| **Client class** | `OllamaApiClient` (dedicated) |
| **Endpoint** | `http://localhost:11434` (configurable) |
| **Auth** | None |
| **Protocol** | Ollama HTTP API (`/api/chat`) |
| **Tool calling** | ⚠️ Model-dependent |
| **API docs** | https://github.com/ollama/ollama/blob/main/docs/api.md |

---

## Completion parameters

| Parameter | Supported | Range / values |
|---|---|---|
| `temperature` | ✅ | `0.0` – `2.0` |
| `topP` | ✅ | `0.0` – `1.0` |
| `topK` | ✅ | Any integer |
| `frequencyPenalty` | ❌ | — |
| `presencePenalty` | ❌ | — |
| `stop` | ✅ | Any sequences |
| `seed` | ✅ | Any integer |
| `responseFormat` | ❌ | — |
| `reasoningEffort` | ❌ | — |

---

## Wire format

Ollama's `/api/chat` format with `options` block:

```json
{
  "model": "llama3.1",
  "stream": true,
  "options": {
    "temperature": 0.7,
    "top_p": 0.9,
    "top_k": 40,
    "seed": 42,
    "stop": ["\n"]
  }
}
```

---

## Tool calling

Tool call support is **model-dependent**. SharpClaw sends standard tool
definitions in the request body; whether the loaded model honours them is
determined entirely by its training and quantization level.

Models with documented tool calling support include `llama3.1`,
`mistral-nemo`, and `qwen2.5`. Models without it will ignore the
definitions or produce malformed output. There is no SharpClaw-side
workaround — select a model with confirmed tool call support.

If you need reliable, structurally-enforced tool calling with a
locally-running model, use the [LlamaSharp provider](LlamaSharp.md)
instead, which applies GBNF grammar constraints regardless of model
training.

---

## Notes

- No API key is required. Leave the key field empty when creating the
  provider.
- The endpoint is configurable. If Ollama is running on a different host
  or port, set `apiEndpoint` accordingly.
- Model sync uses `GET /api/tags`. Run `provider sync <id>` (or
  `POST /providers/{id}/sync`) after pulling new models with
  `ollama pull <model>` to register them in SharpClaw.
- GPU offloading, quantization selection, and model lifecycle are managed
  entirely through Ollama — refer to the
  [Ollama documentation](https://ollama.com/docs).
- `frequencyPenalty`, `presencePenalty`, and `responseFormat` are not
  part of the Ollama options block and are silently ignored if set.

→ [Back to overview](../Provider-Parameters.md)
