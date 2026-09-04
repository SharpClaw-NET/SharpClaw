using Microsoft.Extensions.Configuration;
using SharpClaw.Contracts.Providers;

namespace SharpClaw.Runtime.BLL.Kernel;

/// <summary>Creates a provider client from Runtime configuration and the compiled registration graph.</summary>
public interface IRuntimeProviderClientFactory
{
    IProviderApiClient Create(
        IConfiguration configuration,
        IReadOnlyList<IProviderPlugin> plugins);
}
