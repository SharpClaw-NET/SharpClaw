using SharpClaw.Contracts.Providers;
using SharpClaw.Providers.Common;

namespace SharpClaw.Application.Core.Clients;

public sealed class CerebrasApiClient : OpenAiCompatibleApiClient
{
    protected override string ApiEndpoint => "https://api.cerebras.ai/v1";
    public override string ProviderKey => WellKnownProviderKeys.Cerebras;
}
