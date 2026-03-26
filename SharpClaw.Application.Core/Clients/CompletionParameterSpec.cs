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

    // ── Reasoning effort ─────────────────────────────────────────
    public bool SupportsReasoningEffort { get; init; }
    public string[] ValidReasoningEffortValues { get; init; } = ["none", "minimal", "low", "medium", "high", "xhigh"];

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
        // Google Vertex AI  (OpenAI-compatible endpoint)
        // https://cloud.google.com/vertex-ai/generative-ai/docs
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
            SupportsReasoningEffort = false,
        },

        // ─────────────────────────────────────────────────────────
        // Google Gemini  (OpenAI-compatible endpoint)
        // https://ai.google.dev/api/rest
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
            SupportsReasoningEffort = false,
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
        // Local inference  (LLamaSharp — no parameter mapping)
        // ─────────────────────────────────────────────────────────
        [ProviderType.Local] = new()
        {
            ProviderName = "Local (LLamaSharp)",
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
    };
}
