using Microsoft.Extensions.DependencyInjection;
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

    private IProviderPlugin? GetActivePlugin(string providerKey)
    {
        if (_plugins.TryGetValue(providerKey, out var plugin) && IsActive(plugin))
            return plugin;

        if (_registry is null)
            return null;

        foreach (var host in _registry.GetRuntimeHosts())
        {
            foreach (var externalPlugin in host.Services.GetServices<IProviderPlugin>())
            {
                if (externalPlugin.ProviderKey == providerKey && IsActive(externalPlugin))
                    return externalPlugin;
            }
        }

        return null;
    }

    private IEnumerable<IProviderPlugin> GetExternalPlugins()
    {
        if (_registry is null)
            yield break;

        foreach (var host in _registry.GetRuntimeHosts())
        {
            foreach (var plugin in host.Services.GetServices<IProviderPlugin>())
            {
                if (IsActive(plugin))
                    yield return plugin;
            }
        }
    }

    /// <summary>Indicates whether a plugin is registered for the given provider key.</summary>
    public bool IsAvailable(string providerKey)
        => GetActivePlugin(providerKey) is not null;

    /// <summary>Enumerates the plugin metadata for every registered, currently-active provider key.</summary>
    public IEnumerable<IProviderPlugin> Plugins => _plugins.Values
        .Concat(GetExternalPlugins())
        .Where(IsActive)
        .GroupBy(p => p.ProviderKey, StringComparer.Ordinal)
        .Select(g => g.First());

    /// <summary>
    /// Returns the API client for the given provider key. For providers
    /// whose plugin sets <see cref="IProviderPlugin.RequiresEndpoint"/>
    /// (e.g. Custom or Ollama), supply the
    /// <paramref name="apiEndpoint"/> stored on the provider record.
    /// </summary>
    /// <exception cref="ProviderUnavailableException">
    /// Thrown when no plugin is registered for the requested key,
    /// typically because the owning provider module is disabled.
    /// </exception>
    public IProviderApiClient GetClient(string providerKey, string? apiEndpoint = null)
    {
        var plugin = GetActivePlugin(providerKey);
        if (plugin is null)
            throw new ProviderUnavailableException(providerKey);

        return plugin.CreateClient(apiEndpoint);
    }

    /// <summary>
    /// Returns the registered plugin for the given provider key, or
    /// <see langword="null"/> when no plugin is currently registered or
    /// when its owning module is disabled.
    /// </summary>
    public IProviderPlugin? GetPlugin(string providerKey)
        => GetActivePlugin(providerKey);

    /// <summary>
    /// Resolves the <see cref="ICompletionParameterSpec"/> declared by the
    /// active plugin for the given provider key. Falls back to
    /// <see cref="ICompletionParameterSpec.Passthrough"/> when no plugin is
    /// registered (e.g. unknown providers, or providers whose owning module
    /// is currently disabled). Provider-shape ownership lives entirely on
    /// plugins; <c>SharpClaw.Application.Core</c> no longer maintains its
    /// own provider spec table.
    /// </summary>
    public ICompletionParameterSpec GetParameterSpec(string providerKey)
        => GetPlugin(providerKey)?.ParameterSpec ?? ICompletionParameterSpec.Passthrough;
}
