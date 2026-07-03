using SharpClaw.Contracts.Providers;
using SharpClaw.Providers.Common;

namespace SharpClaw.Modules.Providers.OpenAICompatible.Clients;

/// <summary>
/// An OpenAI-compatible client whose endpoint is configured per-provider
/// instance rather than being baked into the type.
/// </summary>
public sealed class CustomOpenAiCompatibleApiClient(
    string apiEndpoint,
    string apiKey = "",
    HttpClient? httpClient = null) : OpenAiCompatibleApiClient(apiKey, httpClient)
{
    protected override string ApiEndpoint { get; } = apiEndpoint.TrimEnd('/');
    public override string ProviderKey => "custom";
}
