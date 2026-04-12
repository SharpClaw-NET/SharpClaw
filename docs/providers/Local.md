# Local (LLamaSharp)

| | |
|---|---|
| **`ProviderType`** | `Local` (`13`) |
| **Client class** | `LocalInferenceApiClient` (dedicated) |
| **Endpoint** | In-process (no HTTP) |
| **Auth** | None |
| **Protocol** | LLamaSharp in-process inference |
| **Tool calling** | ✅ |

---

## Completion parameters

| Parameter | Supported |
|---|---|
| `temperature` | ❌ |
| `topP` | ❌ |
| `topK` | ❌ |
| `frequencyPenalty` | ❌ |
| `presencePenalty` | ❌ |
| `stop` | ❌ |
| `seed` | ❌ |
| `responseFormat` | ❌ |
| `reasoningEffort` | ❌ |

**No typed completion parameters are supported.** All inference settings
are controlled by the loaded model configuration.

Setting any typed parameter on a Local agent will be **rejected** by
validation.

---

## Notes

- Inference runs in-process via LLamaSharp — no HTTP requests.
- GPU layer count is configured via `.env` (`Local__GpuLayerCount`,
  default `-1` = all layers).
- Model loading, quantization, and sampling settings are managed through
  the LLamaSharp configuration, not through SharpClaw's typed
  completion parameters.
- `providerParameters` is **not** applicable — there is no outgoing HTTP
  request to inject parameters into.

→ [Back to overview](../Provider-Parameters.md)
