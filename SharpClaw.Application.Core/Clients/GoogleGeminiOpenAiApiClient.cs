using System.Text.Json;
using SharpClaw.Contracts.Enums;

namespace SharpClaw.Application.Core.Clients;

/// <summary>
/// Google Gemini via the OpenAI-compatible endpoint.
/// Provider parameters follow the OpenAI schema and are passed through as-is.
/// </summary>
public sealed class GoogleGeminiOpenAiApiClient : OpenAiCompatibleApiClient
{
    protected override string ApiEndpoint => "https://generativelanguage.googleapis.com/v1beta/openai";
    public override ProviderType ProviderType => ProviderType.GoogleGeminiOpenAi;

    /// <inheritdoc />
    protected override Dictionary<string, JsonElement>? TranslateProviderParameters(
        Dictionary<string, JsonElement>? providerParameters)
        => GoogleParameterTranslator.Translate(providerParameters);
}
