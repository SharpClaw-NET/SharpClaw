using System.Text.Json;
using SharpClaw.Contracts.Enums;

namespace SharpClaw.Application.Core.Clients;

public sealed class GoogleGeminiApiClient : OpenAiCompatibleApiClient
{
    protected override string ApiEndpoint => "https://generativelanguage.googleapis.com/v1beta/openai";
    public override ProviderType ProviderType => ProviderType.GoogleGemini;

    /// <inheritdoc />
    protected override Dictionary<string, JsonElement>? TranslateProviderParameters(
        Dictionary<string, JsonElement>? providerParameters)
        => GoogleParameterTranslator.Translate(providerParameters);
}
