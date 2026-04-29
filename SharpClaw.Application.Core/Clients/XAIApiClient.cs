using SharpClaw.Contracts.Providers;
using SharpClaw.Providers.Common;

namespace SharpClaw.Application.Core.Clients;

public sealed class XAIApiClient : OpenAiCompatibleApiClient
{
    protected override string ApiEndpoint => "https://api.x.ai/v1";
    public override string ProviderKey => WellKnownProviderKeys.XAI;
}
