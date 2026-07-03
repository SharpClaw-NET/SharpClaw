using System.Text.Json;
using SharpClaw.Contracts.Providers;
using SharpClaw.Providers.Common;

namespace SharpClaw.Modules.Providers.OpenAICompatible.Clients;

/// <summary>
/// Google Vertex AI via the OpenAI-compatible endpoint.
/// Uses the same <see cref="GoogleParameterTranslator"/> as
/// <see cref="GoogleGeminiOpenAiApiClient"/> for <c>generation_config</c>
/// unwrapping.
/// </summary>
public sealed class GoogleVertexAIOpenAiApiClient(
    string apiKey = "",
    HttpClient? httpClient = null)
    : OpenAiCompatibleApiClient(apiKey, httpClient)
{
    protected override string ApiEndpoint => "https://us-central1-aiplatform.googleapis.com/v1beta1/openai";
    public override string ProviderKey => "google-vertex-ai-openai";

    /// <inheritdoc />
    protected override Dictionary<string, JsonElement>? TranslateProviderParameters(
        Dictionary<string, JsonElement>? providerParameters)
        => GoogleParameterTranslator.Translate(providerParameters);
}
