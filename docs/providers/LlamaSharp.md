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

Setting any typed parameter on a LlamaSharp agent will be **rejected** by
validation.

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

## Notes

- Inference runs in-process via LLamaSharp — no HTTP requests.
- GPU layer count is configured via `.env` (`Local__GpuLayerCount`,
  default `-1` = all layers).
- Model loading, quantization, and sampling settings are managed through
  the LLamaSharp configuration, not through SharpClaw's typed
  completion parameters.
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
