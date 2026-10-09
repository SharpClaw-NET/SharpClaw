using Microsoft.Extensions.Configuration;
using SharpClaw.Contracts.Providers;
using SharpClaw.Runtime.BLL.Kernel;

namespace SharpClaw.Runtime.Host;

internal sealed class RuntimeProviderClientFactory : IRuntimeProviderClientFactory
{
    public IProviderApiClient Create(
        IConfiguration configuration,
        IReadOnlyList<IProviderPlugin> plugins,
        string providerKey)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(plugins);
        if (string.IsNullOrWhiteSpace(providerKey))
            throw new InvalidOperationException(RuntimeProviderSetup.RequiredErrorMessage);
        var plugin = plugins.FirstOrDefault(value =>
            string.Equals(value.ProviderKey, providerKey, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                providerKey == "unconfigured" && string.IsNullOrWhiteSpace(
                    configuration["Provider:Key"] ?? configuration["Providers:Default"])
                    ? RuntimeProviderSetup.RequiredErrorMessage
                    : $"No enabled provider registration registered provider '{providerKey}'.");
        var configuredDefault = configuration["Provider:Key"]
            ?? configuration["Providers:Default"];
        var isDefault = string.Equals(
            configuredDefault,
            providerKey,
            StringComparison.OrdinalIgnoreCase);
        var endpoint = configuration[$"Providers:{providerKey}:Endpoint"]
            ?? (isDefault ? configuration["Provider:Endpoint"] : null);
        var credential = configuration[$"Providers:{providerKey}:ApiKey"]
            ?? (isDefault ? configuration["Provider:ApiKey"] : null)
            ?? string.Empty;
        var options = new ProviderClientOptions(endpoint);
        if (plugin.RequiresEndpoint && string.IsNullOrWhiteSpace(endpoint))
            throw new InvalidOperationException(RuntimeProviderSetup.RequiredErrorMessage);
        if (!plugin.RequiresApiKey)
            return plugin.CreateClient(options);

        if (string.IsNullOrWhiteSpace(credential))
        {
            throw new InvalidOperationException(
                RuntimeProviderSetup.RequiredErrorMessage);
        }

        if (plugin is not IProviderCredentialBoundPlugin credentialBound)
        {
            throw new InvalidOperationException(
                $"Provider '{plugin.ProviderKey}' requires credentials, but its plugin does not support host-side credential binding.");
        }

        return credentialBound.CreateClient(options, credential);
    }
}
