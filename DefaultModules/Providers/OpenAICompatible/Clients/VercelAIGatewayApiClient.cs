using SharpClaw.Contracts.Providers;
using SharpClaw.Providers.Common;

namespace SharpClaw.Modules.Providers.OpenAICompatible.Clients;

public sealed class VercelAIGatewayApiClient(
    string apiKey = "",
    HttpClient? httpClient = null)
    : OpenAiCompatibleApiClient(apiKey, httpClient)
{
    protected override string ApiEndpoint => "https://gateway.ai.vercel.app/v1";
    public override string ProviderKey => "vercel-ai-gateway";
}
