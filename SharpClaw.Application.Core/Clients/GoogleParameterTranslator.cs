using System.Text.Json;

namespace SharpClaw.Application.Core.Clients;

/// <summary>
/// Translates native Google Gemini / Vertex AI provider parameters to
/// their OpenAI-compatible equivalents so they work through the
/// <c>/openai</c> compatibility endpoints.
/// </summary>
internal static class GoogleParameterTranslator
{
    /// <summary>
    /// Translates known Gemini-native parameters to OpenAI-compatible form.
    /// <list type="bullet">
    ///   <item>
    ///     <c>generation_config: { ... }</c> — unwrapped: inner keys are
    ///     promoted to the top level (existing top-level keys take precedence).
    ///   </item>
    ///   <item>
    ///     <c>response_mime_type</c> — rejected with an informative error.
    ///     Google's OpenAI compatibility endpoint does not support the
    ///     simplified <c>response_format: { "type": "json_object" }</c>
    ///     form, and <c>response_mime_type</c> is not a valid OpenAI field.
    ///   </item>
    /// </list>
    /// </summary>
    /// <exception cref="NotSupportedException">
    /// Thrown when <c>response_mime_type</c> is present (directly or inside
    /// <c>generation_config</c>).
    /// </exception>
    internal static Dictionary<string, JsonElement>? Translate(
        Dictionary<string, JsonElement>? providerParameters)
    {
        if (providerParameters is null || providerParameters.Count == 0)
            return providerParameters;

        var needsUnwrap = providerParameters.ContainsKey("generation_config");
        var needsMimeTranslation = providerParameters.ContainsKey("response_mime_type");

        if (!needsUnwrap && !needsMimeTranslation)
            return providerParameters;

        var translated = new Dictionary<string, JsonElement>(providerParameters);

        // Phase 1: Unwrap generation_config contents to top level.
        if (needsUnwrap &&
            translated.Remove("generation_config", out var configElement) &&
            configElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in configElement.EnumerateObject())
            {
                // Top-level keys set directly by the user take precedence.
                translated.TryAdd(prop.Name, prop.Value.Clone());
            }
        }

        // Phase 2: Reject response_mime_type — it's a native Gemini parameter
        // with no working equivalent in Google's OpenAI compatibility layer.
        // The simplified response_format {"type":"json_object"} is not
        // supported by Google's /openai/chat/completions endpoint (their docs
        // only show the full json_schema variant via the SDK's parse() API),
        // and response_mime_type itself is not a valid OpenAI field.
        if (translated.ContainsKey("response_mime_type"))
        {
            throw new NotSupportedException(
                "'response_mime_type' is not supported through Google's OpenAI-compatible endpoint. " +
                "Google's compatibility layer does not accept the simplified " +
                "response_format: {\"type\": \"json_object\"} form, and 'response_mime_type' " +
                "is a native Gemini parameter that is not a valid OpenAI field. " +
                "Alternatives: (1) Use the typed 'responseFormat' completion parameter with a full " +
                "json_schema definition instead (response_format: {\"type\": \"json_schema\", ...}). " +
                "(2) Instruct the model to respond in JSON via the system prompt. " +
                "(3) Remove 'response_mime_type' from providerParameters and use only " +
                "parameters supported by Google's OpenAI compatibility endpoint " +
                "(temperature, top_p, top_k, frequency_penalty, presence_penalty, stop, seed, reasoning_effort).");
        }

        return translated;
    }
}
