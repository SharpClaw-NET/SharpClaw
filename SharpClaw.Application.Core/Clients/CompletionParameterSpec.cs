using SharpClaw.Contracts.Enums;

namespace SharpClaw.Application.Core.Clients;

/// <summary>
/// Declares what each provider supports and the valid ranges for each
/// completion parameter.  This is the single source of truth that drives
/// both validation (<see cref="CompletionParameterValidator"/>) and the
/// generated provider parameter reference documentation.
/// </summary>
public sealed record CompletionParameterSpec
{
    /// <summary>Display-friendly provider name used in error messages.</summary>
    public required string ProviderName { get; init; }

    // ── Temperature ──────────────────────────────────────────────
    public bool SupportsTemperature { get; init; }
    public float TemperatureMin { get; init; }
    public float TemperatureMax { get; init; } = 2.0f;

    // ── Top-P ────────────────────────────────────────────────────
    public bool SupportsTopP { get; init; }
    public float TopPMin { get; init; }
    public float TopPMax { get; init; } = 1.0f;

    // ── Top-K ────────────────────────────────────────────────────
    public bool SupportsTopK { get; init; }
    public int TopKMin { get; init; } = 1;
    public int TopKMax { get; init; } = int.MaxValue;

    // ── Frequency penalty ────────────────────────────────────────
    public bool SupportsFrequencyPenalty { get; init; }
    public float FrequencyPenaltyMin { get; init; } = -2.0f;
    public float FrequencyPenaltyMax { get; init; } = 2.0f;

    // ── Presence penalty ─────────────────────────────────────────
    public bool SupportsPresencePenalty { get; init; }
    public float PresencePenaltyMin { get; init; } = -2.0f;
    public float PresencePenaltyMax { get; init; } = 2.0f;

    // ── Stop sequences ───────────────────────────────────────────
    public bool SupportsStop { get; init; }
    public int MaxStopSequences { get; init; } = 4;

    // ── Seed ─────────────────────────────────────────────────────
    public bool SupportsSeed { get; init; }

    // ── Response format ──────────────────────────────────────────
    public bool SupportsResponseFormat { get; init; }

    /// <summary>
    /// When <see langword="true"/>, the provider supports <c>response_format</c>
    /// but rejects the simplified <c>{"type": "json_object"}</c> form.
    /// Google's OpenAI compatibility endpoint only accepts the full
    /// <c>{"type": "json_schema", …}</c> variant.
    /// </summary>
    public bool RejectsJsonObjectResponseFormat { get; init; }

    /// <summary>
    /// When <see langword="true"/>, the provider supports <c>response_format</c>
    /// <em>only</em> in the simplified <c>{"type": "json_object"}</c> form.
    /// The structured <c>json_schema</c> variant is not implemented and is
    /// rejected upstream. No provider currently sets this flag —
    /// <see cref="ProviderType.LlamaSharp"/> previously did while the
    /// schema-to-GBNF converter was pending, but now supports both shapes
    /// end-to-end via <c>LlamaSharpJsonSchemaConverter</c>.
    /// </summary>
    public bool OnlyJsonObjectResponseFormat { get; init; }

    // ── Reasoning effort ─────────────────────────────────────────
    public bool SupportsReasoningEffort { get; init; }

    /// <summary>
    /// When <see langword="true"/>, <see cref="CompletionParameters.ReasoningEffort"/>
    /// is accepted by the validator but is <em>not</em> mapped into the
    /// provider wire payload. The value is surfaced only as a notice inside
    /// the chat header so the model can see the user's intent. Used by
    /// providers (like <see cref="ProviderType.LlamaSharp"/>) whose local
    /// runtime has no mechanical reasoning-effort control.
    /// </summary>
    public bool ReasoningEffortInformationalOnly { get; init; }

    public string[] ValidReasoningEffortValues { get; init; } = ["none", "minimal", "low", "medium", "high", "xhigh"];

    // ── Tool choice / parallel tool calls ────────────────────────

    /// <summary>
    /// When <see langword="true"/>, the provider honours the full
    /// <c>tool_choice</c> surface (<c>auto</c>, <c>none</c>,
    /// <c>required</c>, named function) and <c>parallel_tool_calls</c>.
    /// OpenAI-compatible providers forward the fields on the wire;
    /// LlamaSharp enforces them by compiling a tailored GBNF grammar.
    /// </summary>
    public bool SupportsToolChoice { get; init; }

    /// <summary>
    /// When <see langword="true"/>, the provider can enforce each
    /// tool's argument JSON Schema at the sampler/server level
    /// (OpenAI <c>strict: true</c>; LlamaSharp per-tool GBNF).
    /// When <see langword="false"/> the <see cref="CompletionParameters.StrictTools"/>
    /// field is accepted but is not enforced mechanically.
    /// </summary>
    public bool SupportsStrictTools { get; init; }

    // ═════════════════════════════════════════════════════════════
    // Provider catalogue
    // ═════════════════════════════════════════════════════════════

    /// <summary>
    /// Returns the constraint spec for the given <see cref="ProviderType"/>.
    /// </summary>
    public static CompletionParameterSpec For(ProviderType providerType)
        => Specs.TryGetValue(providerType, out var spec)
            ? spec
            : Passthrough;

    /// <summary>
    /// Permissive fallback for unknown/custom providers — everything is
    /// "supported" with wide ranges so that validation never blocks.
    /// </summary>
    private static readonly CompletionParameterSpec Passthrough = new()
    {
        ProviderName = "Custom / Unknown",
        SupportsTemperature = true,
        TemperatureMin = 0.0f,
        TemperatureMax = 2.0f,
        SupportsTopP = true,
        SupportsTopK = true,
        SupportsFrequencyPenalty = true,
        SupportsPresencePenalty = true,
        SupportsStop = true,
        MaxStopSequences = 16,
        SupportsSeed = true,
        SupportsResponseFormat = true,
        SupportsReasoningEffort = true,
        SupportsToolChoice = true,
    };

    private static readonly Dictionary<ProviderType, CompletionParameterSpec> Specs = new()
    {
        // ─────────────────────────────────────────────────────────
        // OpenAI  (Chat Completions + Responses API)
        // https://platform.openai.com/docs/api-reference
        // ─────────────────────────────────────────────────────────
        [ProviderType.OpenAI] = new()
        {
            ProviderName = "OpenAI",
            SupportsTemperature = true,
            TemperatureMin = 0.0f,
            TemperatureMax = 2.0f,
            SupportsTopP = true,
            TopPMin = 0.0f,
            TopPMax = 1.0f,
            SupportsTopK = false,
            SupportsFrequencyPenalty = true,
            FrequencyPenaltyMin = -2.0f,
            FrequencyPenaltyMax = 2.0f,
            SupportsPresencePenalty = true,
            PresencePenaltyMin = -2.0f,
            PresencePenaltyMax = 2.0f,
            SupportsStop = true,
            MaxStopSequences = 4,
            SupportsSeed = true,
            SupportsResponseFormat = true,   // Chat Completions only
            SupportsReasoningEffort = true,  // Responses API / o-series & gpt-5
            ValidReasoningEffortValues = ["none", "minimal", "low", "medium", "high", "xhigh"],
            SupportsToolChoice = true,
        },

        // ─────────────────────────────────────────────────────────
        // Anthropic
        // https://docs.anthropic.com/en/api/messages
        // ─────────────────────────────────────────────────────────
        [ProviderType.Anthropic] = new()
        {
            ProviderName = "Anthropic",
            SupportsTemperature = true,
            TemperatureMin = 0.0f,
            TemperatureMax = 1.0f,
            SupportsTopP = true,
            TopPMin = 0.0f,
            TopPMax = 1.0f,
            SupportsTopK = true,
            TopKMin = 1,
            TopKMax = int.MaxValue,          // no documented ceiling
            SupportsFrequencyPenalty = false,
            SupportsPresencePenalty = false,
            SupportsStop = true,
            MaxStopSequences = 8192,         // Anthropic allows many
            SupportsSeed = false,
            SupportsResponseFormat = false,
            SupportsReasoningEffort = false,
        },

        // ─────────────────────────────────────────────────────────
        // OpenRouter  (multi-model gateway, OpenAI-compatible)
        // https://openrouter.ai/docs/parameters
        // ─────────────────────────────────────────────────────────
        [ProviderType.OpenRouter] = new()
        {
            ProviderName = "OpenRouter",
            SupportsTemperature = true,
            TemperatureMin = 0.0f,
            TemperatureMax = 2.0f,
            SupportsTopP = true,
            TopPMin = 0.0f,
            TopPMax = 1.0f,
            SupportsTopK = true,
            TopKMin = 1,
            TopKMax = int.MaxValue,
            SupportsFrequencyPenalty = true,
            FrequencyPenaltyMin = -2.0f,
            FrequencyPenaltyMax = 2.0f,
            SupportsPresencePenalty = true,
            PresencePenaltyMin = -2.0f,
            PresencePenaltyMax = 2.0f,
            SupportsStop = true,
            MaxStopSequences = 4,
            SupportsSeed = true,
            SupportsResponseFormat = true,
            SupportsReasoningEffort = false,
        },

        // ─────────────────────────────────────────────────────────
        // Google Vertex AI  (native generateContent endpoint)
        // https://cloud.google.com/vertex-ai/generative-ai/docs
        // NOT YET IMPLEMENTED — stub mirrors GoogleGemini native
        // constraints for forward-compat.
        // ─────────────────────────────────────────────────────────
        [ProviderType.GoogleVertexAI] = new()
        {
            ProviderName = "Google Vertex AI",
            SupportsTemperature = true,
            TemperatureMin = 0.0f,
            TemperatureMax = 2.0f,
            SupportsTopP = true,
            TopPMin = 0.0f,
            TopPMax = 1.0f,
            SupportsTopK = true,
            TopKMin = 1,
            TopKMax = 40,
            SupportsFrequencyPenalty = false,
            SupportsPresencePenalty = false,
            SupportsStop = true,
            MaxStopSequences = 5,
            SupportsSeed = true,
            SupportsResponseFormat = true,
            SupportsReasoningEffort = true,
            ValidReasoningEffortValues = ["none", "minimal", "low", "medium", "high"],
        },

        // ─────────────────────────────────────────────────────────
        // Google Vertex AI OpenAI-compat  (/v1beta1/openai endpoint)
        // https://cloud.google.com/vertex-ai/generative-ai/docs
        // topK is NOT supported — the OpenAI-compatible schema has
        // no top_k field and it is not serialised by the base client.
        // Use providerParameters to pass topK via extra_body if needed.
        // ─────────────────────────────────────────────────────────
        [ProviderType.GoogleVertexAIOpenAi] = new()
        {
            ProviderName = "Google Vertex AI (OpenAI-compat)",
            SupportsTemperature = true,
            TemperatureMin = 0.0f,
            TemperatureMax = 2.0f,
            SupportsTopP = true,
            TopPMin = 0.0f,
            TopPMax = 1.0f,
            SupportsTopK = false,
            SupportsFrequencyPenalty = true,
            FrequencyPenaltyMin = -2.0f,
            FrequencyPenaltyMax = 2.0f,
            SupportsPresencePenalty = true,
            PresencePenaltyMin = -2.0f,
            PresencePenaltyMax = 2.0f,
            SupportsStop = true,
            MaxStopSequences = 5,
            SupportsSeed = true,
            SupportsResponseFormat = true,
            RejectsJsonObjectResponseFormat = true,
            SupportsReasoningEffort = true,
            ValidReasoningEffortValues = ["none", "minimal", "low", "medium", "high"],
        },

        // ─────────────────────────────────────────────────────────
        // Google Gemini  (native generateContent endpoint)
        // https://ai.google.dev/gemini-api/docs
        // Parameters are passed through as-is in the native Gemini
        // schema.  CompletionParameters are mapped to generationConfig
        // fields (temperature, topP, topK, stopSequences, seed,
        // responseMimeType, maxOutputTokens, thinkingConfig).
        // ─────────────────────────────────────────────────────────
        [ProviderType.GoogleGemini] = new()
        {
            ProviderName = "Google Gemini",
            SupportsTemperature = true,
            TemperatureMin = 0.0f,
            TemperatureMax = 2.0f,
            SupportsTopP = true,
            TopPMin = 0.0f,
            TopPMax = 1.0f,
            SupportsTopK = true,
            TopKMin = 1,
            TopKMax = 40,
            SupportsFrequencyPenalty = false,
            SupportsPresencePenalty = false,
            SupportsStop = true,
            MaxStopSequences = 5,
            SupportsSeed = true,
            SupportsResponseFormat = true,
            SupportsReasoningEffort = true,
            ValidReasoningEffortValues = ["none", "minimal", "low", "medium", "high"],
        },

        // ─────────────────────────────────────────────────────────
        // Google Gemini OpenAI-compat  (/v1beta/openai endpoint)
        // https://ai.google.dev/gemini-api/docs/openai
        // topK is NOT supported — the OpenAI-compatible schema has
        // no top_k field and it is not serialised by the base client.
        // Use providerParameters to pass topK via extra_body if needed.
        // ─────────────────────────────────────────────────────────
        [ProviderType.GoogleGeminiOpenAi] = new()
        {
            ProviderName = "Google Gemini (OpenAI-compat)",
            SupportsTemperature = true,
            TemperatureMin = 0.0f,
            TemperatureMax = 2.0f,
            SupportsTopP = true,
            TopPMin = 0.0f,
            TopPMax = 1.0f,
            SupportsTopK = false,
            SupportsFrequencyPenalty = true,
            FrequencyPenaltyMin = -2.0f,
            FrequencyPenaltyMax = 2.0f,
            SupportsPresencePenalty = true,
            PresencePenaltyMin = -2.0f,
            PresencePenaltyMax = 2.0f,
            SupportsStop = true,
            MaxStopSequences = 5,
            SupportsSeed = true,
            SupportsResponseFormat = true,
            RejectsJsonObjectResponseFormat = true,
            SupportsReasoningEffort = true,
            ValidReasoningEffortValues = ["none", "minimal", "low", "medium", "high"],
        },

        // ─────────────────────────────────────────────────────────
        // xAI  (Grok — OpenAI-compatible)
        // https://docs.x.ai/docs
        // ─────────────────────────────────────────────────────────
        [ProviderType.XAI] = new()
        {
            ProviderName = "xAI (Grok)",
            SupportsTemperature = true,
            TemperatureMin = 0.0f,
            TemperatureMax = 2.0f,
            SupportsTopP = true,
            TopPMin = 0.0f,
            TopPMax = 1.0f,
            SupportsTopK = false,
            SupportsFrequencyPenalty = true,
            FrequencyPenaltyMin = -2.0f,
            FrequencyPenaltyMax = 2.0f,
            SupportsPresencePenalty = true,
            PresencePenaltyMin = -2.0f,
            PresencePenaltyMax = 2.0f,
            SupportsStop = true,
            MaxStopSequences = 4,
            SupportsSeed = true,
            SupportsResponseFormat = true,
            SupportsReasoningEffort = false,
        },

        // ─────────────────────────────────────────────────────────
        // Groq  (fast inference — OpenAI-compatible)
        // https://console.groq.com/docs/api-reference
        // ─────────────────────────────────────────────────────────
        [ProviderType.Groq] = new()
        {
            ProviderName = "Groq",
            SupportsTemperature = true,
            TemperatureMin = 0.0f,
            TemperatureMax = 2.0f,
            SupportsTopP = true,
            TopPMin = 0.0f,
            TopPMax = 1.0f,
            SupportsTopK = false,
            SupportsFrequencyPenalty = true,
            FrequencyPenaltyMin = -2.0f,
            FrequencyPenaltyMax = 2.0f,
            SupportsPresencePenalty = true,
            PresencePenaltyMin = -2.0f,
            PresencePenaltyMax = 2.0f,
            SupportsStop = true,
            MaxStopSequences = 4,
            SupportsSeed = true,
            SupportsResponseFormat = true,
            SupportsReasoningEffort = false,
        },

        // ─────────────────────────────────────────────────────────
        // Cerebras  (fast inference — OpenAI-compatible)
        // https://inference-docs.cerebras.ai/api-reference
        // ─────────────────────────────────────────────────────────
        [ProviderType.Cerebras] = new()
        {
            ProviderName = "Cerebras",
            SupportsTemperature = true,
            TemperatureMin = 0.0f,
            TemperatureMax = 1.5f,
            SupportsTopP = true,
            TopPMin = 0.0f,
            TopPMax = 1.0f,
            SupportsTopK = false,
            SupportsFrequencyPenalty = false,
            SupportsPresencePenalty = false,
            SupportsStop = true,
            MaxStopSequences = 4,
            SupportsSeed = true,
            SupportsResponseFormat = true,
            SupportsReasoningEffort = false,
        },

        // ─────────────────────────────────────────────────────────
        // Mistral
        // https://docs.mistral.ai/api/
        // ─────────────────────────────────────────────────────────
        [ProviderType.Mistral] = new()
        {
            ProviderName = "Mistral",
            SupportsTemperature = true,
            TemperatureMin = 0.0f,
            TemperatureMax = 1.0f,
            SupportsTopP = true,
            TopPMin = 0.0f,
            TopPMax = 1.0f,
            SupportsTopK = false,
            SupportsFrequencyPenalty = false,
            SupportsPresencePenalty = false,
            SupportsStop = true,
            MaxStopSequences = 4,
            SupportsSeed = true,
            SupportsResponseFormat = true,
            SupportsReasoningEffort = false,
        },

        // ─────────────────────────────────────────────────────────
        // GitHub Copilot  (via GitHub Models API)
        // ─────────────────────────────────────────────────────────
        [ProviderType.GitHubCopilot] = new()
        {
            ProviderName = "GitHub Copilot",
            SupportsTemperature = true,
            TemperatureMin = 0.0f,
            TemperatureMax = 2.0f,
            SupportsTopP = true,
            TopPMin = 0.0f,
            TopPMax = 1.0f,
            SupportsTopK = false,
            SupportsFrequencyPenalty = true,
            FrequencyPenaltyMin = -2.0f,
            FrequencyPenaltyMax = 2.0f,
            SupportsPresencePenalty = true,
            PresencePenaltyMin = -2.0f,
            PresencePenaltyMax = 2.0f,
            SupportsStop = true,
            MaxStopSequences = 4,
            SupportsSeed = true,
            SupportsResponseFormat = true,
            SupportsReasoningEffort = true,
            ValidReasoningEffortValues = ["none", "minimal", "low", "medium", "high", "xhigh"],
        },

        // ─────────────────────────────────────────────────────────
        // ZAI
        // ─────────────────────────────────────────────────────────
        [ProviderType.ZAI] = new()
        {
            ProviderName = "ZAI",
            SupportsTemperature = true,
            TemperatureMin = 0.0f,
            TemperatureMax = 2.0f,
            SupportsTopP = true,
            TopPMin = 0.0f,
            TopPMax = 1.0f,
            SupportsTopK = false,
            SupportsFrequencyPenalty = true,
            FrequencyPenaltyMin = -2.0f,
            FrequencyPenaltyMax = 2.0f,
            SupportsPresencePenalty = true,
            PresencePenaltyMin = -2.0f,
            PresencePenaltyMax = 2.0f,
            SupportsStop = true,
            MaxStopSequences = 4,
            SupportsSeed = true,
            SupportsResponseFormat = true,
            SupportsReasoningEffort = false,
        },

        // ─────────────────────────────────────────────────────────
        // Vercel AI Gateway  (OpenAI-compatible)
        // ─────────────────────────────────────────────────────────
        [ProviderType.VercelAIGateway] = new()
        {
            ProviderName = "Vercel AI Gateway",
            SupportsTemperature = true,
            TemperatureMin = 0.0f,
            TemperatureMax = 2.0f,
            SupportsTopP = true,
            TopPMin = 0.0f,
            TopPMax = 1.0f,
            SupportsTopK = false,
            SupportsFrequencyPenalty = true,
            FrequencyPenaltyMin = -2.0f,
            FrequencyPenaltyMax = 2.0f,
            SupportsPresencePenalty = true,
            PresencePenaltyMin = -2.0f,
            PresencePenaltyMax = 2.0f,
            SupportsStop = true,
            MaxStopSequences = 4,
            SupportsSeed = true,
            SupportsResponseFormat = true,
            SupportsReasoningEffort = false,
        },

        // ─────────────────────────────────────────────────────────
        // Minimax  (OpenAI-compatible)
        // https://platform.minimaxi.com/document
        // ─────────────────────────────────────────────────────────
        [ProviderType.Minimax] = new()
        {
            ProviderName = "Minimax",
            SupportsTemperature = true,
            TemperatureMin = 0.0f,
            TemperatureMax = 2.0f,
            SupportsTopP = true,
            TopPMin = 0.0f,
            TopPMax = 1.0f,
            SupportsTopK = false,
            SupportsFrequencyPenalty = false,
            SupportsPresencePenalty = false,
            SupportsStop = true,
            MaxStopSequences = 4,
            SupportsSeed = false,
            SupportsResponseFormat = false,
            SupportsReasoningEffort = false,
        },

        // ─────────────────────────────────────────────────────────
        // LlamaSharp  (in-process LLM inference via DefaultSamplingPipeline)
        // Temperature, TopP, TopK, FrequencyPenalty, PresencePenalty are
        // mapped directly to DefaultSamplingPipeline properties.
        // Stop sequences are merged into BuildAntiPrompts alongside the
        // model's own EOS/EOT tokens.
        // Seed is mapped via unchecked int→uint reinterpret; Seed=0 means
        // llama.cpp picks a fresh random seed per call.
        // ResponseFormat: both {"type":"json_object"} (generic JSON GBNF)
        // and {"type":"json_schema", …} (converted via
        // LlamaSharpJsonSchemaConverter into a schema-specific GBNF) are
        // supported end-to-end. Schema features outside the converter's
        // coverage matrix degrade to the generic JSON grammar with a
        // logged warning rather than failing validation — this matches
        // hosted-OpenAI semantics for best-effort strict-mode support.
        // ReasoningEffort is not meaningful for llama.cpp — the provider
        // surfaces it as an informational notice in the chat header
        // instead of the wire payload.
        // ─────────────────────────────────────────────────────────
        [ProviderType.LlamaSharp] = new()
        {
            ProviderName = "LlamaSharp (Local)",
            SupportsTemperature = true,
            TemperatureMin = 0.0f,
            TemperatureMax = 2.0f,
            SupportsTopP = true,
            TopPMin = 0.0f,
            TopPMax = 1.0f,
            SupportsTopK = true,
            TopKMin = 1,
            TopKMax = 128,
            SupportsFrequencyPenalty = true,
            FrequencyPenaltyMin = 0.0f,
            FrequencyPenaltyMax = 1.0f,
            SupportsPresencePenalty = true,
            PresencePenaltyMin = 0.0f,
            PresencePenaltyMax = 1.0f,
            SupportsStop = true,
            MaxStopSequences = 16,
            SupportsSeed = true,
            SupportsResponseFormat = true,
            OnlyJsonObjectResponseFormat = false,
            SupportsReasoningEffort = true,
            ReasoningEffortInformationalOnly = true,
            SupportsToolChoice = true,
            SupportsStrictTools = true,
        },

        // ─────────────────────────────────────────────────────────
        // Whisper  (in-process STT — does not use completion params)
        // ─────────────────────────────────────────────────────────
        [ProviderType.Whisper] = new()
        {
            ProviderName = "Whisper (Local)",
            SupportsTemperature = false,
            SupportsTopP = false,
            SupportsTopK = false,
            SupportsFrequencyPenalty = false,
            SupportsPresencePenalty = false,
            SupportsStop = false,
            SupportsSeed = false,
            SupportsResponseFormat = false,
            SupportsReasoningEffort = false,
        },

        // ─────────────────────────────────────────────────────────
        // Ollama  (user-managed server — OpenAI-compatible)
        // https://github.com/ollama/ollama/blob/main/docs/api.md
        // ─────────────────────────────────────────────────────────
        [ProviderType.Ollama] = new()
        {
            ProviderName = "Ollama",
            SupportsTemperature = true,
            TemperatureMin = 0.0f,
            TemperatureMax = 2.0f,
            SupportsTopP = true,
            TopPMin = 0.0f,
            TopPMax = 1.0f,
            SupportsTopK = false,
            SupportsFrequencyPenalty = true,
            FrequencyPenaltyMin = -2.0f,
            FrequencyPenaltyMax = 2.0f,
            SupportsPresencePenalty = true,
            PresencePenaltyMin = -2.0f,
            PresencePenaltyMax = 2.0f,
            SupportsStop = true,
            MaxStopSequences = 4,
            SupportsSeed = true,
            SupportsResponseFormat = false,
            SupportsReasoningEffort = false,
        },
    };
}
