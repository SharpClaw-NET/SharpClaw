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
| `stop` | ✅ | up to 16 | Unioned into `InferenceParams.AntiPrompts` alongside the model's native EOS/EOT strings and the built-in `CommonStopSequences` set |
| `seed` | ✅ | `int` | `DefaultSamplingPipeline.Seed` — reinterpreted as `uint` via `unchecked((uint)seed)` (bijective two's-complement wraparound) |
| `responseFormat` | ✅ | `json_object` / `json_schema` | `json_object` attaches the generic JSON GBNF grammar to `DefaultSamplingPipeline.Grammar`. `json_schema` is converted by `LlamaSharpJsonSchemaConverter` into a schema-specific GBNF covering the OpenAI strict-mode subset (`type`, `properties`, `required`, `additionalProperties`, `items`, `enum`, `const`, `anyOf`/`oneOf`, simple `allOf`, local `$ref`/`$defs`). Schema features outside that matrix degrade to the generic JSON grammar with a logged warning. **Tool calling takes precedence** — when tools are active the tool-envelope grammar wins and `response_format` is ignored with a debug-category log line. |
| `reasoningEffort` | ⚠️ | `low`/`medium`/`high`/`xhigh` | **Informational only.** llama.cpp has no reasoning-budget knob; the value is surfaced to the model via the chat-header `reasoning-effort` notice (default header) and the `{{reasoning-effort}}` tag (custom templates) so the model can react to the user's intent, but nothing in the sampling pipeline mechanically enforces it. |

All seven sampling parameters are applied through a shared
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

## Configuration keys (`Local:*`)

All keys are optional. Bind via `.env` (`Local__Key=...`) or
`appsettings.json` (`"Local": { "Key": "..." }`).

| Key | Default | Purpose |
|---|---|---|
| `GpuLayerCount` | `-1` | Layers offloaded to GPU. `-1` = all, `0` = CPU only. Falls back to CPU automatically when no GPU is available. Bound at startup in `Program.cs` onto `LocalInferenceProcessManager.DefaultGpuLayerCount`. |
| `ContextSize` | `8192` | Context window in tokens. Bound onto `LocalInferenceProcessManager.DefaultContextSize`. |
| `KeepLoaded` | `true` | If `true`, models stay resident after first use. If `false`, idle unpinned models are unloaded after `IdleCooldownMinutes`. |
| `IdleCooldownMinutes` | `5` | Idle time before an unpinned model is unloaded. Only applies when `KeepLoaded=false`. |
| `ModelsDirectory` | `<AppContext.BaseDirectory>/Models` | Root folder used by `ModelDownloadManager` for GGUF downloads and resume. Set to point at a shared/external SSD to keep models off the install volume. Created on first use. |
| `HuggingFaceToken` | *(unset)* | Personal access token sent as `Authorization: Bearer <token>` by `HuggingFaceUrlResolver`. Required for gated/private repos and substantially raises Hugging Face anonymous rate limits (`HTTP 429` with `Retry-After`). |

> ℹ️ The native LLamaSharp backend (CUDA / Vulkan / CPU) is configured
> *before* the host is built in `Program.cs` — it is sticky once any
> LLama API is touched. A startup log line records the chosen
> backend; see the top of `Program.cs` if you need to override the
> preference order.

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
- **`response_format` vs tool calling precedence:** when a request passes
  both a `responseFormat` and tool definitions, the tool-envelope grammar
  built by `LlamaSharpToolGrammar` wins unconditionally. The
  `responseFormat` grammar is skipped and a debug-category warning is
  logged to `SharpClaw.CLI`. This mirrors how OpenAI behaves in practice
  (function calling overrides `response_format`) and keeps the streaming
  envelope parser in `ChatCompletionWithToolsAsync` /
  `StreamChatCompletionWithToolsAsync` sound, because those paths depend
  on the `{"mode":"…","text":"…","calls":[…]}` schema.
- **`reasoningEffort` visibility:** the value is injected as a
  `reasoning-effort: {level} (informational; this model has no
  mechanical reasoning-effort control)` segment in the default chat
  header, and exposed as the `{{reasoning-effort}}` tag in custom header
  templates. Both paths go through the shared `ChatHeaderNotices`
  helper so the emitted string is identical. Users on providers that
  consume `reasoningEffort` on the wire (OpenAI, GitHub Copilot, Gemini,
  etc.) do not see the notice — the tag renders to an empty string
  there.

→ [Back to overview](../Provider-Parameters.md)
