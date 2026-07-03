using SharpClaw.Contracts.Providers;
using SharpClaw.Providers.Common;

namespace SharpClaw.Modules.Providers.OpenAICompatible.Clients;

/// <summary>
/// Client for Zhipu AI (Z.AI / GLM). OpenAI-compatible API.
/// </summary>
public sealed class ZAIApiClient(
    string apiKey = "",
    HttpClient? httpClient = null)
    : OpenAiCompatibleApiClient(apiKey, httpClient)
{
    protected override string ApiEndpoint => "https://open.bigmodel.cn/api/paas/v4";
    public override string ProviderKey => "zai";
}
