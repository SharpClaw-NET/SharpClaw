using SharpClaw.Contracts.Providers;
using SharpClaw.Providers.Common;

namespace SharpClaw.Application.Core.Clients;

/// <summary>
/// Client for Zhipu AI (Z.AI / GLM). OpenAI-compatible API.
/// </summary>
public sealed class ZAIApiClient : OpenAiCompatibleApiClient
{
    protected override string ApiEndpoint => "https://open.bigmodel.cn/api/paas/v4";
    public override string ProviderKey => WellKnownProviderKeys.ZAI;
}
