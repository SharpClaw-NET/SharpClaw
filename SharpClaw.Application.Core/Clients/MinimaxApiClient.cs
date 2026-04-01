using SharpClaw.Contracts.Enums;

namespace SharpClaw.Application.Core.Clients;

public sealed class MinimaxApiClient : OpenAiCompatibleApiClient
{
    protected override string ApiEndpoint => "https://api.minimaxi.com/v1";
    public override ProviderType ProviderType => ProviderType.Minimax;
}
