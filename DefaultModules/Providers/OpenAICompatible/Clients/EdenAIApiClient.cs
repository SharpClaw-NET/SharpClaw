using SharpClaw.Providers.Common;

namespace SharpClaw.Modules.Providers.OpenAICompatible.Clients;

public sealed class EdenAIApiClient(
    string apiKey = "",
    HttpClient? httpClient = null)
    : OpenAiCompatibleApiClient(apiKey, httpClient)
{
    protected override string ApiEndpoint => "https://api.edenai.run/v3";
    public override string ProviderKey => "eden-ai";
}
