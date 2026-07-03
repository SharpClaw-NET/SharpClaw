using SharpClaw.Contracts.Providers;
using SharpClaw.Providers.Common;

namespace SharpClaw.Modules.Providers.OpenAICompatible.Clients;

public sealed class CerebrasApiClient(
    string apiKey = "",
    HttpClient? httpClient = null)
    : OpenAiCompatibleApiClient(apiKey, httpClient)
{
    protected override string ApiEndpoint => "https://api.cerebras.ai/v1";
    public override string ProviderKey => "cerebras";
}
