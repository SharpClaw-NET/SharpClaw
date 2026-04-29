using SharpClaw.Application.Core.Modules;
using SharpClaw.Contracts.Providers;

namespace SharpClaw.Application.Core.Clients;

/// <summary>
/// Resolves provider API clients by dispatching to the registered set
/// of <see cref="IProviderPlugin"/> instances. Plugins are contributed
/// by per-protocol modules; the factory consults <see cref="ModuleRegistry"/>
/// on every lookup so plugins owned by a disabled module are hidden
/// from callers without needing a DI rebuild.
/// </summary>
public sealed class ProviderApiClientFactory
{
    private readonly Dictionary<string, IProviderPlugin> _plugins;
    private readonly ModuleRegistry? _registry;

    public ProviderApiClientFactory(IEnumerable<IProviderPlugin> plugins, ModuleRegistry? registry = null)
    {
        _plugins = plugins.ToDictionary(p => p.ProviderKey, StringComparer.Ordinal);
        _registry = registry;
    }

    private bool IsActive(IProviderPlugin plugin)
    {
        if (_registry is null) return true;
        var owner = plugin.OwnerModuleId;
        if (string.IsNullOrEmpty(owner)) return true;
        return _registry.GetModule(owner) is not null;
    }

    /// <summary>Indicates whether a plugin is registered for the given provider key.</summary>
    public bool IsAvailable(string providerKey)
        => _plugins.TryGetValue(providerKey, out var plugin) && IsActive(plugin);

    /// <summary>Enumerates the plugin metadata for every registered, currently-active provider key.</summary>
    public IEnumerable<IProviderPlugin> Plugins => _plugins.Values.Where(IsActive);

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
        if (!_plugins.TryGetValue(providerKey, out var plugin) || !IsActive(plugin))
            throw new ProviderUnavailableException(providerKey);

        return plugin.CreateClient(apiEndpoint);
    }

    /// <summary>
    /// Returns the registered plugin for the given provider key, or
    /// <see langword="null"/> when no plugin is currently registered or
    /// when its owning module is disabled.
    /// </summary>
    public IProviderPlugin? GetPlugin(string providerKey)
        => _plugins.TryGetValue(providerKey, out var plugin) && IsActive(plugin) ? plugin : null;
}
