using SharpClaw.Contracts.Providers;

namespace SharpClaw.Application.Core.Clients;

/// <summary>
/// An OpenAI-compatible client whose endpoint is configured per-provider
/// instance rather than being baked into the type.
/// </summary>
public sealed class CustomOpenAiCompatibleApiClient(string apiEndpoint) : OpenAiCompatibleApiClient
{
    protected override string ApiEndpoint { get; } = apiEndpoint.TrimEnd('/');
    public override string ProviderKey => WellKnownProviderKeys.Custom;
}
