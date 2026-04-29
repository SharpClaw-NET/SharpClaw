using SharpClaw.Contracts.Providers;
using SharpClaw.Providers.Common;

namespace SharpClaw.Application.Core.Clients;

public sealed class MinimaxApiClient : OpenAiCompatibleApiClient
{
    protected override string ApiEndpoint => "https://api.minimaxi.com/v1";
    public override string ProviderKey => WellKnownProviderKeys.Minimax;
}
