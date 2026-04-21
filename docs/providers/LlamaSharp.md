# LlamaSharp

| | |
|---|---|
| **`ProviderType`** | `LlamaSharp` (`13`) |
| **Client class** | `LocalInferenceApiClient` (dedicated) |
| **Endpoint** | In-process (no HTTP) |
| **Auth** | None |
| **Protocol** | LLamaSharp in-process inference |
| **Tool calling** | ✅ (GBNF grammar-constrained) |

---

## Completion parameters

| Parameter | Supported | Range | Maps to |
|---|---|---|---|
| `temperature` | ✅ | 0.0 – 2.0 | `DefaultSamplingPipeline.Temperature` |
| `topP` | ✅ | 0.0 – 1.0 | `DefaultSamplingPipeline.TopP` |
| `topK` | ✅ | 1 – 128 | `DefaultSamplingPipeline.TopK` |
| `frequencyPenalty` | ✅ | -2.0 – 2.0 | `DefaultSamplingPipeline.FrequencyPenalty` |
| `presencePenalty` | ✅ | -2.0 – 2.0 | `DefaultSamplingPipeline.PresencePenalty` |
| `stop` | ❌ | — | Stop sequences are model-driven via `BuildAntiPrompts`; the typed field is ignored |
| `seed` | ❌ | — | `int` vs `uint` mismatch — llama.cpp seed is `uint`; not mapped |
| `responseFormat` | ❌ | — | Grammar enforcement handles structured output natively |
| `reasoningEffort` | ❌ | — | Not applicable |

All five supported parameters are applied through a shared
`BuildSamplingPipeline` helper that constructs a
`DefaultSamplingPipeline` (LLamaSharp). When a parameter is not set,
the corresponding llama.cpp default is used — there is no server-side
override.

---

## Tool calling

Tool calling uses **GBNF grammar-constrained generation** via LLamaSharp's
`DefaultSamplingPipeline.Grammar`. Every tool-capable turn is constrained
to produce a canonical JSON envelope:

```json
{ "mode": "message", "text": "prose response", "calls": [] }
```

```json
{
  "mode": "tool_calls",
  "text": "",
  "calls": [{ "id": "call_abc123", "name": "tool_name", "args": { "key": "value" } }]
}
```

The grammar guarantees structural validity of the envelope — `mode` is
always one of the two literal values, `calls` is always a JSON array, and
`args` is always a JSON object (never an escaped JSON string). It does
**not** constrain tool names to the known set; unknown or no-op names
(`"none"`, `"null"`, `"no_tool"`, etc.) are filtered by the envelope
parser before results are returned to `ChatService`.

### Tool calling reliability

The grammar enforces **output structure**, not **semantic correctness**.
A model can still:

- Emit an incorrect tool name that is not in the provided tool list.
- Emit empty or wrong `args` values for the chosen tool.
- Produce a valid-but-useless call (e.g. `"none"`) that is filtered out.

Tool calling reliability varies significantly by model family and
quantization level. Higher-quantization models (Q2, Q3) are more likely to
produce semantically incorrect tool invocations even when the grammar
sampler enforces structural validity. Evaluate tool calling empirically on
your target model and quantization before relying on it in production flows.

On heavily-quantized models that defeat the grammar sampler entirely, the
envelope parser falls back gracefully and returns an empty result with a
debug-category warning (`SharpClaw.CLI`).

---

## Vision / multimodal

Multimodal inference (LLaVA-style image input) is supported when a
GGUF model has a paired **mmproj / CLIP projector file**.

Register the projector path against a local model:

```
# CLI
model mmproj <model-id> /path/to/model-mmproj.gguf

# REST
PUT /models/local/{id}/mmproj
{ "mmprojPath": "/path/to/model-mmproj.gguf" }
```

Pass `"none"` as the path to clear a previously registered projector.

When a projector is loaded and a chat request includes an image (via
`imageBase64` on a message), the inference path switches automatically
from `StatelessExecutor` to `InteractiveExecutor` with the MTMD API.
Models without a registered projector are unaffected.

> ℹ️ The underlying API is `LLama.MtmdWeights` (LLamaSharp 0.26+,
> MTMD API). The older `LLavaWeights` / LLaVA API is not used.

---

## Notes

- Inference runs in-process via LLamaSharp — no HTTP requests.
- GPU layer count is configured via `.env` (`Local__GpuLayerCount`,
  default `-1` = all layers).
- `providerParameters` is **not** applicable — there is no outgoing HTTP
  request to inject parameters into.
- The GGUF chat template embedded in the model file is applied
  automatically via `PromptTemplateTransformer`, so any model family
  (ChatML, Llama, Mistral, Phi, Gemma, etc.) formats prompts correctly
  without manual template configuration.
- The `ProviderType` wire name changed from `"Local"` to `"LlamaSharp"`
  in the alpha release. Any stored records or API clients using `"Local"`
  must be updated.

→ [Back to overview](../Provider-Parameters.md)
