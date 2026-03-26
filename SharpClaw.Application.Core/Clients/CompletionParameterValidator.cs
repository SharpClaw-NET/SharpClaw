using SharpClaw.Contracts.Enums;

namespace SharpClaw.Application.Core.Clients;

/// <summary>
/// Validates <see cref="CompletionParameters"/> against the
/// <see cref="CompletionParameterSpec"/> for a specific provider.
/// <para>
/// Produces clear, actionable error messages that tell the developer
/// exactly what went wrong, what the valid range is, and which provider
/// constraint was violated.
/// </para>
/// </summary>
public static class CompletionParameterValidator
{
    /// <summary>
    /// Validates the completion parameters for the given provider.
    /// Returns an empty list when everything is valid.
    /// </summary>
    public static List<string> Validate(
        CompletionParameters? parameters,
        ProviderType providerType)
    {
        if (parameters is null || parameters.IsEmpty)
            return [];

        var spec = CompletionParameterSpec.For(providerType);
        var errors = new List<string>();

        // ── Temperature ──────────────────────────────────────────
        if (parameters.Temperature is { } temp)
        {
            if (!spec.SupportsTemperature)
                errors.Add(
                    $"'{spec.ProviderName}' does not support the 'temperature' parameter. " +
                    "Local inference uses model-native settings — remove this parameter " +
                    "or switch to a hosted provider (OpenAI, Anthropic, Mistral, etc.).");
            else if (temp < spec.TemperatureMin || temp > spec.TemperatureMax)
                errors.Add(
                    $"Invalid temperature value {temp} for '{spec.ProviderName}'. " +
                    $"Expected range: {spec.TemperatureMin:F1}–{spec.TemperatureMax:F1}.");
        }

        // ── Top-P ────────────────────────────────────────────────
        if (parameters.TopP is { } topP)
        {
            if (!spec.SupportsTopP)
                errors.Add(
                    $"'{spec.ProviderName}' does not support the 'topP' parameter. " +
                    "Local inference uses model-native settings — remove this parameter " +
                    "or switch to a hosted provider (OpenAI, Anthropic, Mistral, etc.).");
            else if (topP < spec.TopPMin || topP > spec.TopPMax)
                errors.Add(
                    $"Invalid topP value {topP} for '{spec.ProviderName}'. " +
                    $"Expected range: {spec.TopPMin:F1}–{spec.TopPMax:F1}.");
        }

        // ── Top-K ────────────────────────────────────────────────
        if (parameters.TopK is { } topK)
        {
            if (!spec.SupportsTopK)
                errors.Add(
                    $"'{spec.ProviderName}' does not support the 'topK' parameter. " +
                    "Remove it or switch to a provider that supports top-K sampling " +
                    "(Anthropic, Google Gemini, Google Vertex AI, OpenRouter).");
            else if (topK < spec.TopKMin)
                errors.Add(
                    $"Invalid topK value {topK} for '{spec.ProviderName}'. " +
                    $"Minimum value is {spec.TopKMin}.");
            else if (spec.TopKMax < int.MaxValue && topK > spec.TopKMax)
                errors.Add(
                    $"Invalid topK value {topK} for '{spec.ProviderName}'. " +
                    $"Maximum value is {spec.TopKMax}.");
        }

        // ── Frequency penalty ────────────────────────────────────
        if (parameters.FrequencyPenalty is { } freqPen)
        {
            if (!spec.SupportsFrequencyPenalty)
                errors.Add(
                    $"'{spec.ProviderName}' does not support the 'frequencyPenalty' parameter. " +
                    "Remove it or switch to a provider that supports it " +
                    "(OpenAI, OpenRouter, xAI, Groq).");
            else if (freqPen < spec.FrequencyPenaltyMin || freqPen > spec.FrequencyPenaltyMax)
                errors.Add(
                    $"Invalid frequencyPenalty value {freqPen} for '{spec.ProviderName}'. " +
                    $"Expected range: {spec.FrequencyPenaltyMin:F1}–{spec.FrequencyPenaltyMax:F1}.");
        }

        // ── Presence penalty ─────────────────────────────────────
        if (parameters.PresencePenalty is { } presPen)
        {
            if (!spec.SupportsPresencePenalty)
                errors.Add(
                    $"'{spec.ProviderName}' does not support the 'presencePenalty' parameter. " +
                    "Remove it or switch to a provider that supports it " +
                    "(OpenAI, OpenRouter, xAI, Groq).");
            else if (presPen < spec.PresencePenaltyMin || presPen > spec.PresencePenaltyMax)
                errors.Add(
                    $"Invalid presencePenalty value {presPen} for '{spec.ProviderName}'. " +
                    $"Expected range: {spec.PresencePenaltyMin:F1}–{spec.PresencePenaltyMax:F1}.");
        }

        // ── Stop sequences ───────────────────────────────────────
        if (parameters.Stop is { Length: > 0 } stop)
        {
            if (!spec.SupportsStop)
                errors.Add(
                    $"'{spec.ProviderName}' does not support the 'stop' parameter. " +
                    "Remove it or switch to a provider that supports stop sequences.");
            else
            {
                if (stop.Length > spec.MaxStopSequences)
                    errors.Add(
                        $"Too many stop sequences ({stop.Length}) for '{spec.ProviderName}'. " +
                        $"Maximum allowed: {spec.MaxStopSequences}.");

                for (var i = 0; i < stop.Length; i++)
                {
                    if (string.IsNullOrWhiteSpace(stop[i]))
                        errors.Add(
                            $"Stop sequence at index {i} is empty or whitespace. " +
                            "Each stop sequence must contain at least one visible character.");
                }
            }
        }

        // ── Seed ─────────────────────────────────────────────────
        if (parameters.Seed is not null && !spec.SupportsSeed)
            errors.Add(
                $"'{spec.ProviderName}' does not support the 'seed' parameter. " +
                "Remove it or switch to a provider that supports deterministic " +
                "sampling (OpenAI, Mistral, Groq, xAI).");

        // ── Response format ──────────────────────────────────────
        if (parameters.ResponseFormat is not null && !spec.SupportsResponseFormat)
            errors.Add(
                $"'{spec.ProviderName}' does not support the 'responseFormat' parameter. " +
                "Remove it or switch to a provider that supports structured output " +
                "(OpenAI, Mistral, Groq, xAI, OpenRouter).");

        // ── Reasoning effort ─────────────────────────────────────
        if (parameters.ReasoningEffort is { } effort)
        {
            if (!spec.SupportsReasoningEffort)
                errors.Add(
                    $"'{spec.ProviderName}' does not support the 'reasoningEffort' parameter. " +
                    "This parameter is available on OpenAI reasoning and gpt-5 models only.");
            else if (!spec.ValidReasoningEffortValues.Contains(effort, StringComparer.OrdinalIgnoreCase))
                errors.Add(
                    $"Invalid reasoningEffort value '{effort}' for '{spec.ProviderName}'. " +
                    $"Valid values: {string.Join(", ", spec.ValidReasoningEffortValues.Select(v => $"'{v}'"))}.");
        }

        return errors;
    }

    /// <summary>
    /// Validates and throws <see cref="CompletionParameterValidationException"/>
    /// if any errors are found.  Use this for fail-fast paths.
    /// </summary>
    public static void ValidateOrThrow(
        CompletionParameters? parameters,
        ProviderType providerType)
    {
        var errors = Validate(parameters, providerType);
        if (errors.Count > 0)
            throw new CompletionParameterValidationException(providerType, errors);
    }
}

/// <summary>
/// Thrown when one or more completion parameters are invalid for the
/// target provider.  Contains the full list of structured error messages.
/// </summary>
public sealed class CompletionParameterValidationException : ArgumentException
{
    public ProviderType ProviderType { get; }
    public IReadOnlyList<string> ValidationErrors { get; }

    public CompletionParameterValidationException(
        ProviderType providerType,
        IReadOnlyList<string> errors)
        : base(FormatMessage(providerType, errors))
    {
        ProviderType = providerType;
        ValidationErrors = errors;
    }

    private static string FormatMessage(ProviderType providerType, IReadOnlyList<string> errors)
    {
        if (errors.Count == 1)
            return $"Invalid completion parameter for {providerType}: {errors[0]}";

        return $"Invalid completion parameters for {providerType}:\n" +
               string.Join("\n", errors.Select(e => $"  • {e}"));
    }
}
