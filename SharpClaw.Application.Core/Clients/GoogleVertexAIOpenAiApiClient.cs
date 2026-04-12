using System.Text.Json;
using SharpClaw.Contracts.Enums;

namespace SharpClaw.Application.Core.Clients;

/// <summary>
/// Google Vertex AI via the OpenAI-compatible endpoint.
/// Uses the same <see cref="GoogleParameterTranslator"/> as
/// <see cref="GoogleGeminiOpenAiApiClient"/> for <c>generation_config</c>
/// unwrapping.
/// </summary>
public sealed class GoogleVertexAIOpenAiApiClient : OpenAiCompatibleApiClient
{
    protected override string ApiEndpoint => "https://us-central1-aiplatform.googleapis.com/v1beta1/openai";
    public override ProviderType ProviderType => ProviderType.GoogleVertexAIOpenAi;

    /// <inheritdoc />
    protected override Dictionary<string, JsonElement>? TranslateProviderParameters(
        Dictionary<string, JsonElement>? providerParameters)
        => GoogleParameterTranslator.Translate(providerParameters);
}
