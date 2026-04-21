using System.Text.Json;
using SharpClaw.Application.Core.LocalInference;

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
    /// On <see cref="ProviderType.GoogleGemini"/> (native), this is mapped to
    /// <c>responseMimeType</c> inside <c>generationConfig</c>.
    /// On <see cref="ProviderType.GoogleGeminiOpenAi"/> and
    /// <see cref="ProviderType.GoogleVertexAIOpenAi"/> (OpenAI-compat), only the full
    /// <c>json_schema</c> variant is supported; the simplified
    /// <c>{"type": "json_object"}</c> form is rejected —
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

    /// <summary>
    /// Tool-selection policy for this turn. Mirrors OpenAI's
    /// <c>tool_choice</c>: <see cref="ToolChoiceMode.Auto"/> (default
    /// when <see langword="null"/>), <see cref="ToolChoiceMode.None"/>,
    /// <see cref="ToolChoiceMode.Required"/>, or
    /// <see cref="ToolChoiceMode.Named"/> with a function name.
    /// <para>
    /// LlamaSharp enforces this by compiling a tailored GBNF grammar
    /// (<see cref="LlamaSharpToolGrammar"/>); OpenAI-compatible
    /// providers forward it on the wire.
    /// </para>
    /// </summary>
    public ToolChoice? ToolChoice { get; init; }

    /// <summary>
    /// When set to <see langword="false"/>, the provider must emit at
    /// most one tool call per turn. Defaults to the provider's own
    /// default (<see langword="true"/> on OpenAI and LlamaSharp).
    /// </summary>
    public bool? ParallelToolCalls { get; init; }

    /// <summary>
    /// When <see langword="true"/>, tool-call arguments must conform to
    /// each tool's <see cref="ChatToolDefinition.ParametersSchema"/>.
    /// LlamaSharp enforces this by composing per-tool GBNF fragments
    /// derived from the schemas (<see cref="LlamaSharpJsonSchemaConverter"/>).
    /// OpenAI maps to <c>strict: true</c> on each tool. Providers that
    /// support only permissive tool calling ignore this field. Defaults
    /// to the provider's own default — LlamaSharp opts-in by default.
    /// </summary>
    public bool? StrictTools { get; init; }

    /// <summary>
    /// Optional conversation/thread correlation id used by providers
    /// that support cross-turn KV-cache reuse (currently LlamaSharp).
    /// When set, the provider may reuse a cached inference session
    /// keyed on (model, thread) to skip re-prefilling the conversation
    /// prefix. Other providers ignore this field.
    /// </summary>
    public Guid? ThreadId { get; init; }

    /// <summary>Returns <see langword="true"/> when all fields are null.</summary>
    public bool IsEmpty =>
        Temperature is null && TopP is null && TopK is null &&
        FrequencyPenalty is null && PresencePenalty is null &&
        Stop is null && Seed is null && ResponseFormat is null &&
        ReasoningEffort is null &&
        ToolChoice is null && ParallelToolCalls is null &&
        StrictTools is null && ThreadId is null;
}
