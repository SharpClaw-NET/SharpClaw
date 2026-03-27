using System.Text.Json;
using SharpClaw.Contracts.Enums;

namespace SharpClaw.Application.Core.Clients;

public sealed class GoogleVertexAIApiClient : OpenAiCompatibleApiClient
{
    protected override string ApiEndpoint => "https://us-central1-aiplatform.googleapis.com/v1beta1/openai";
    public override ProviderType ProviderType => ProviderType.GoogleVertexAI;

    /// <summary>
    /// Google Vertex AI's OpenAI-compatible endpoint does not support
    /// <c>parallel_tool_calls</c>; omit it from the serialized payload.
    /// </summary>
    protected override bool? ParallelToolCallsDefault => null;

    /// <inheritdoc />
    protected override Dictionary<string, JsonElement>? TranslateProviderParameters(
        Dictionary<string, JsonElement>? providerParameters)
        => GoogleParameterTranslator.Translate(providerParameters);
}
