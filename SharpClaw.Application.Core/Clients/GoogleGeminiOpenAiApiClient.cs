using System.Text.Json;
using SharpClaw.Contracts.Providers;
using SharpClaw.Providers.Common;

namespace SharpClaw.Application.Core.Clients;

/// <summary>
/// Google Gemini via the OpenAI-compatible endpoint.
/// Provider parameters follow the OpenAI schema and are passed through as-is.
/// </summary>
public sealed class GoogleGeminiOpenAiApiClient : OpenAiCompatibleApiClient
{
    protected override string ApiEndpoint => "https://generativelanguage.googleapis.com/v1beta/openai";
    public override string ProviderKey => WellKnownProviderKeys.GoogleGeminiOpenAi;

    /// <inheritdoc />
    protected override Dictionary<string, JsonElement>? TranslateProviderParameters(
        Dictionary<string, JsonElement>? providerParameters)
        => GoogleParameterTranslator.Translate(providerParameters);
}
