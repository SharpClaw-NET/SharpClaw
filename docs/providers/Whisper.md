# Whisper (Local)

| | |
|---|---|
| **`ProviderType`** | `Whisper` (`17`) |
| **Client class** | `LocalTranscriptionClient` |
| **Endpoint** | In-process (no HTTP) |
| **Auth** | None |
| **Protocol** | Whisper.net in-process inference |
| **Chat** | ❌ Transcription only |
| **API docs** | https://github.com/sandrohanea/whisper.net |

All completion parameters ❌ — Whisper is not a chat provider; the completion parameter system does not apply.

## Registering a model

Download and register a Whisper GGUF via CLI:

```
model download <url> --provider Whisper
```

Or via REST:

```http
POST /models/local/download
{ "url": "<url>", "providerType": "Whisper" }
```

Community GGUF weights: `https://huggingface.co/ggerganov/whisper.cpp`. `small` or `medium` is a practical starting point.

## Notes

- GGUF Whisper weights and GGUF LLaMA weights use the same file container but different internal formats — they are not interchangeable with the current `Whisper.net` and `LlamaSharp` runtimes. Unified speech+chat models exist (e.g. GPT-4o, AudioPaLM) but are not yet distributed as local GGUF files compatible with both runtimes simultaneously.
- A single GGUF file can be registered under both LlamaSharp and Whisper if the weights support both uses. Call `model download <url>` twice with different `--provider` values.
- `--gpu-layers` has no effect for Whisper models. Whisper.net does not expose a layer-count API at the `WhisperFactory` level.
- `ProviderType.Whisper` providers never appear in the chat model selector. They appear only in transcription job configuration.
- Registered Whisper models appear in `model local list` alongside LlamaSharp models.

→ [Back to overview](../Provider-Parameters.md)
