using SharpClaw.Contracts.Providers;

namespace SharpClaw.Application.Core.Clients;

/// <summary>
/// Resolves provider API clients by dispatching to the registered set
/// of <see cref="IProviderPlugin"/> instances. Plugins are contributed
/// by built-in registrations in Core today (Phase 3) and by
/// per-protocol modules in later phases.
/// </summary>
public sealed class ProviderApiClientFactory
{
    private readonly Dictionary<string, IProviderPlugin> _plugins;

    public ProviderApiClientFactory(IEnumerable<IProviderPlugin> plugins)
    {
        _plugins = plugins.ToDictionary(p => p.ProviderKey, StringComparer.Ordinal);
    }

    /// <summary>Indicates whether a plugin is registered for the given provider key.</summary>
    public bool IsAvailable(string providerKey) => _plugins.ContainsKey(providerKey);

    /// <summary>Enumerates the plugin metadata for every registered provider key.</summary>
    public IEnumerable<IProviderPlugin> Plugins => _plugins.Values;

    /// <summary>
    /// Returns the API client for the given provider key. For providers
    /// whose plugin sets <see cref="IProviderPlugin.RequiresEndpoint"/>
    /// (e.g. <see cref="WellKnownProviderKeys.Custom"/>), supply the
    /// <paramref name="apiEndpoint"/> stored on the provider record.
    /// </summary>
    /// <exception cref="ProviderUnavailableException">
    /// Thrown when no plugin is registered for the requested key,
    /// typically because the owning provider module is disabled.
    /// </exception>
    public IProviderApiClient GetClient(string providerKey, string? apiEndpoint = null)
    {
        if (!_plugins.TryGetValue(providerKey, out var plugin))
            throw new ProviderUnavailableException(providerKey);

        return plugin.CreateClient(apiEndpoint);
    }

    /// <summary>
    /// Returns the registered plugin for the given provider key, or
    /// <see langword="null"/> when no plugin is currently registered.
    /// </summary>
    public IProviderPlugin? GetPlugin(string providerKey)
        => _plugins.TryGetValue(providerKey, out var plugin) ? plugin : null;
}
