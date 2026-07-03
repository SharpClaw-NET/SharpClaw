using SharpClaw.Contracts.Providers;
using SharpClaw.Providers.Common;

namespace SharpClaw.Modules.Providers.OpenAICompatible.Clients;

public sealed class OpenRouterApiClient(
    string apiKey = "",
    HttpClient? httpClient = null)
    : OpenAiCompatibleApiClient(apiKey, httpClient)
{
    protected override string ApiEndpoint => "https://openrouter.ai/api/v1";
    public override string ProviderKey => "openrouter";
}
