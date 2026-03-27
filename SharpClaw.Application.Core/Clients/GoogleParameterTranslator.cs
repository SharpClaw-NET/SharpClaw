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
    ///     <c>response_mime_type</c> — removed.  Google's OpenAI compatibility
    ///     endpoint does not support the simplified
    ///     <c>response_format: { "type": "json_object" }</c> form; it only
    ///     supports the full <c>json_schema</c> variant via the SDK's
    ///     <c>parse()</c> method.  Keeping <c>response_mime_type</c> in the
    ///     payload would also be rejected (not an OpenAI field).
    ///   </item>
    /// </list>
    /// </summary>
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

        // Phase 2: Remove response_mime_type — it's a native Gemini parameter
        // with no working equivalent in Google's OpenAI compatibility layer.
        // The simplified response_format: {"type":"json_object"} is not
        // supported by Google's /openai/chat/completions endpoint (their docs
        // only show the full json_schema variant via the parse() API).
        translated.Remove("response_mime_type");

        return translated;
    }
}
