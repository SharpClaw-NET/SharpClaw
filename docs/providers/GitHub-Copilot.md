# GitHub Copilot

| | |
|---|---|
| **`ProviderType`** | `GitHubCopilot` (`11`) |
| **Client class** | `GitHubCopilotApiClient` → `OpenAiCompatibleApiClient` |
| **Endpoint** | GitHub Models API (dynamic) |
| **Auth** | Device code OAuth flow |
| **Protocol** | OpenAI Chat Completions (compatible) |
| **Tool calling** | ✅ Native |

---

## Completion parameters

| Parameter | Supported | Range / values |
|---|---|---|
| `temperature` | ✅ | `0.0` – `2.0` |
| `topP` | ✅ | `0.0` – `1.0` |
| `topK` | ❌ | — |
| `frequencyPenalty` | ✅ | `-2.0` – `2.0` |
| `presencePenalty` | ✅ | `-2.0` – `2.0` |
| `stop` | ✅ | Up to **4** sequences |
| `seed` | ✅ | Any integer |
| `responseFormat` | ✅ | `json_object` and `json_schema` |
| `reasoningEffort` | ✅ | `"none"`, `"minimal"`, `"low"`, `"medium"`, `"high"`, `"xhigh"` |

---

## Wire format

Standard OpenAI Chat Completions format:

```json
{
  "temperature": 0.7,
  "top_p": 0.9,
  "frequency_penalty": 0.5,
  "presence_penalty": 0.3,
  "stop": ["\n", "END"],
  "seed": 42,
  "response_format": { "type": "json_object" },
  "reasoning_effort": "high"
}
```

---

## Notes

- Authentication uses a device code OAuth flow — not a simple API key.
  The client handles token acquisition and refresh automatically.
- `reasoningEffort` supports all six values including `"xhigh"`, same as
  OpenAI.
- Models are accessed through the GitHub Models API, which acts as a
  gateway to multiple model providers.
- No provider-specific parameter translation.

→ [Back to overview](../Provider-Parameters.md)
