# LlamaSharp

| | |
|---|---|
| **`ProviderKey`** | `llamasharp` |
| **Client class** | `LocalInferenceApiClient` (dedicated) |
| **Endpoint** | Local provider inside the LlamaSharp module sidecar |
| **Auth** | None |
| **Protocol** | LLamaSharp llama.cpp inference inside the provider sidecar |
| **Tool calling** | ✅ GBNF grammar-constrained |
| **API docs** | https://scisharp.github.io/LLamaSharp/ |

---

## Completion parameters

| Parameter | Supported | Range / values |
|---|---|---|
| `temperature` | ✅ | `0.0` – `2.0` |
| `topP` | ✅ | `0.0` – `1.0` |
| `topK` | ✅ | `1` – `128` |
| `frequencyPenalty` | ✅ | `-2.0` – `2.0` |
| `presencePenalty` | ✅ | `-2.0` – `2.0` |
| `stop` | ✅ | Up to **16** sequences (unioned with the model's native EOS/EOT strings) |
| `seed` | ✅ | Any integer |
| `responseFormat` | ✅ | `json_object` and `json_schema` both supported via GBNF |
| `reasoningEffort` | ⚠️ | Informational only — surfaced to the model via the chat header; llama.cpp has no reasoning-budget knob |
| `toolChoice` | ✅ | `auto`, `none`, `required`, `{"type":"function","function":{"name":"…"}}` |
| `parallelToolCalls` | ✅ | Defaults to `true` |

All sampling parameters are applied through a shared
`DefaultSamplingPipeline`. Parameters left unset use the llama.cpp
defaults.

---

## Tool calling

Tool calling uses **GBNF grammar-constrained generation**. Every
tool-capable turn is forced to produce a canonical envelope:

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

The grammar guarantees structural validity — `mode` is always one of
the two literal values, `calls` is a JSON array, and `args` is a JSON
object. It does **not** constrain tool names to the known set; unknown
or no-op names are filtered by the envelope parser.

`toolChoice` specialises the grammar: `none` forbids `tool_calls`,
`required` forbids `message`, and a named choice pins the call's
`name` field to a literal.

Reliability of the *semantic* choice (picking the right tool, emitting
correct args) varies by model family and quantization level. The
grammar enforces structure, not correctness — evaluate empirically on
your target model before relying on it.

Streaming is supported: tool-call argument fragments are emitted as
`ChatToolCallDelta` chunks as soon as they are produced.

---

## JSON Schema → GBNF

`responseFormat: json_schema` is lowered to a schema-specific GBNF via
`LlamaSharpJsonSchemaConverter`. The OpenAI strict-mode subset is
covered: `type`, `properties`, `required`, `additionalProperties`,
`items`, `enum`, `const`, `anyOf`/`oneOf`, simple `allOf`, local
`$ref`/`$defs`. String `format` keywords (`uuid`, `email`, `date`,
`date-time`, `time`, `ipv4`, `uri`, `uri-reference`, `hostname`) and a
safe subset of regex `pattern`s (literals, character classes,
quantifiers, alternation, groups, anchors) compile into dedicated
grammar fragments. Unsupported features fall back to the generic JSON
grammar with a logged warning.

When both tools and `responseFormat` are passed, the tool-envelope
grammar wins unconditionally — this matches OpenAI's behaviour.

---

## Vision / multimodal

Multimodal inference is supported when a GGUF model has a paired
**mmproj / CLIP projector**:

```
# CLI
model mmproj <model-id> /path/to/model-mmproj.gguf

# REST
PUT /models/local/{id}/mmproj
{ "mmprojPath": "/path/to/model-mmproj.gguf" }
```

Up to `Local__MaxImagesPerTurn` images (default 8) from the current
turn are staged into the projector via the MTMD API; excess images are
dropped. Pass `"none"` as the projector path to clear it.

---

## Configuration keys (`Local:*`)

Bind via `.env` (`Local__Key=...`) or `appsettings.json`. All keys are
optional.

| Key | Default | Purpose |
|---|---|---|
| `GpuLayerCount` | `-1` | Layers offloaded to GPU. `-1` = all, `0` = CPU-only. Falls back to CPU when no GPU is available. |
| `ContextSize` | `8192` | Context window in tokens. |
| `KeepLoaded` | `true` | If `false`, idle unpinned models are unloaded after `IdleCooldownMinutes`. |
| `IdleCooldownMinutes` | `5` | Idle time before an unpinned model is unloaded. |
| `ModelsDirectory` | `<BaseDirectory>/Models` | GGUF download/cache folder. Point at an external SSD to keep models off the install volume. |
| `HuggingFaceToken` | *(unset)* | Personal access token for gated repos and higher anonymous rate limits. |
| `MaxImagesPerTurn` | `8` | Cap on images staged into the multimodal projector per turn. |

---

## Notes

- Inference runs inside the LlamaSharp module sidecar rather than the Core API
  process, so `providerParameters` does not apply.
- The native backend (CUDA / Vulkan / CPU) is chosen at startup and is
  sticky once any LLama API is touched. A startup log line records the
  selection.
- The GGUF-embedded chat template is applied automatically, so any
  family (ChatML, Llama, Mistral, Phi, Gemma, etc.) formats correctly
  without manual template configuration.
- The provider key changed from `"Local"` to `"llamasharp"` in the
  alpha release. Stored records and API clients using `"Local"` must be
  updated.
- `reasoningEffort` has no mechanical effect on llama.cpp; the value is
  surfaced to the model via the default chat header and the
  `{{reasoning-effort}}` custom-template tag so the model can react to
  the user's intent.

→ [Back to overview](../Provider-Parameters.md)
