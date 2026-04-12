using System.Text.Json;

namespace SharpClaw.Application.Core.Clients;

/// <summary>
/// Structural translator for Google providers that route through the
/// OpenAI-compatible endpoint (<see cref="GoogleGeminiOpenAiApiClient"/>,
/// <see cref="GoogleVertexAIOpenAiApiClient"/>).
/// <para>
/// The only translation is <c>generation_config</c> unwrapping: inner keys
/// are promoted to the top level so they become valid OpenAI-compatible
/// fields. No semantic translation is performed — unknown keys are left
/// as-is and will be silently ignored by the endpoint.
/// </para>
/// </summary>
internal static class GoogleParameterTranslator
{
    /// <summary>
    /// Unwraps <c>generation_config</c> contents to top level.
    /// All other keys are passed through unchanged.
    /// </summary>
    internal static Dictionary<string, JsonElement>? Translate(
        Dictionary<string, JsonElement>? providerParameters)
    {
        if (providerParameters is null || providerParameters.Count == 0)
            return providerParameters;

        if (!providerParameters.ContainsKey("generation_config"))
            return providerParameters;

        var translated = new Dictionary<string, JsonElement>(providerParameters);

        if (translated.Remove("generation_config", out var configElement) &&
            configElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in configElement.EnumerateObject())
            {
                // Top-level keys set directly by the user take precedence.
                translated.TryAdd(prop.Name, prop.Value.Clone());
            }
        }

        return translated;
    }
}
