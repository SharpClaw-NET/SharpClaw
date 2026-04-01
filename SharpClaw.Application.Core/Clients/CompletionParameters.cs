using System.Text.Json;

namespace SharpClaw.Application.Core.Clients;

/// <summary>
/// First-class typed completion parameters passed from the agent entity
/// through the chat service to the provider client.  Each provider maps
/// the subset it supports into its wire payload.
/// <para>
/// The generic <c>ProviderParameters</c> dictionary (escape-hatch) is
/// still merged <em>after</em> these typed fields, so it can override or
/// supply parameters that SharpClaw does not yet model natively.
/// </para>
/// </summary>
public sealed record CompletionParameters
{
    /// <summary>
    /// Sampling temperature. Valid ranges vary by provider — see
    /// <see cref="CompletionParameterSpec"/> for per-provider constraints.
    /// </summary>
    public float? Temperature { get; init; }

    /// <summary>
    /// Nucleus sampling probability mass (0.0–1.0 on all providers that support it).
    /// </summary>
    public float? TopP { get; init; }

    /// <summary>
    /// Top-K sampling. Supported by Anthropic (1–∞) and Google (1–40).
    /// Not supported by OpenAI, Mistral, Groq, xAI, or Cerebras.
    /// </summary>
    public int? TopK { get; init; }

    /// <summary>
    /// Penalises tokens that already appeared. Supported by OpenAI, OpenRouter,
    /// Google, Groq, and xAI. Not supported by Anthropic, Mistral, or Cerebras.
    /// </summary>
    public float? FrequencyPenalty { get; init; }

    /// <summary>
    /// Penalises tokens based on presence in the text so far. Same provider
    /// support as <see cref="FrequencyPenalty"/>.
    /// </summary>
    public float? PresencePenalty { get; init; }

    /// <summary>
    /// Sequences where the model will stop generating. Maximum count varies:
    /// Anthropic allows up to 8 192; most others allow 4; Google allows 5.
    /// </summary>
    public string[]? Stop { get; init; }

    /// <summary>
    /// Deterministic sampling seed. Supported by OpenAI, Mistral, Google,
    /// Groq, xAI, OpenRouter, and Cerebras. Not supported by Anthropic.
    /// </summary>
    public int? Seed { get; init; }

    /// <summary>
    /// Structured output format passed as-is to the provider.
    /// <para>
    /// Google's OpenAI compatibility endpoint only supports the full
    /// <c>json_schema</c> variant (<c>{"type": "json_schema", …}</c>).
    /// The simplified <c>{"type": "json_object"}</c> form is rejected —
    /// see <see cref="CompletionParameterSpec.RejectsJsonObjectResponseFormat"/>.
    /// </para>
    /// See <see cref="CompletionParameterSpec"/> for per-provider support.
    /// </summary>
    public JsonElement? ResponseFormat { get; init; }

    /// <summary>
    /// Reasoning effort hint. Supported by OpenAI (o-series, gpt-5),
    /// Google Gemini, and Google Vertex AI. Valid values vary by provider:
    /// OpenAI accepts <c>"none"</c>, <c>"minimal"</c>, <c>"low"</c>,
    /// <c>"medium"</c>, <c>"high"</c>, <c>"xhigh"</c>.
    /// Google accepts <c>"none"</c> (2.5 models only), <c>"minimal"</c>,
    /// <c>"low"</c>, <c>"medium"</c>, <c>"high"</c>.
    /// Mapped to <c>reasoning_effort</c> on the Chat Completions wire
    /// format and to <c>reasoning.effort</c> on the Responses API.
    /// </summary>
    public string? ReasoningEffort { get; init; }

    /// <summary>Returns <see langword="true"/> when all fields are null.</summary>
    public bool IsEmpty =>
        Temperature is null && TopP is null && TopK is null &&
        FrequencyPenalty is null && PresencePenalty is null &&
        Stop is null && Seed is null && ResponseFormat is null &&
        ReasoningEffort is null;
}
