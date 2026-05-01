using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SharpClaw.Gateway.Abstractions;
using SharpClaw.Gateway.Modules.Routing;
using SharpClaw.Modules.Hosting;
using SharpClaw.Utils.Security;

namespace SharpClaw.Gateway.Modules.Hosting;

/// <summary>
/// Orchestrates the lifecycle of every gateway-side module host. Owns the
/// <see cref="ModuleEndpointDataSource"/> and the
/// <see cref="GatewayEndpointGroupCatalog"/>, and serializes
/// enable/disable/reload through a single async lock so concurrent toggle
/// storms collapse to a defined sequence.
/// </summary>
/// <remarks>
/// Phase 5b: when an entry carries a DLL path, the manager loads it
/// through a fresh collectible <see cref="ModuleLoadContext"/> per enable.
/// Reload disposes the previous host, asks it to verify ALC unload, then
/// loads the (possibly replaced) DLL into a brand-new context. Entries
/// without a DLL path (test fixtures from
/// <see cref="GatewayModuleLoader.FromExtensions"/>) keep the in-memory
/// extension forever and never trigger ALC unload.
/// </remarks>
public sealed class GatewayModuleHostManager : IAsyncDisposable
{
    private readonly GatewayModuleLoader _loader;
    private readonly GatewayEndpointGroupCatalog _catalog;
    private readonly ModuleEndpointDataSource _dataSource;
    private readonly IOptionsMonitor<GatewayModuleOptions> _options;
    private readonly IServiceProvider _services;
    private readonly ILogger<GatewayModuleHostManager> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, GatewayExternalModuleHost> _hosts
        = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string?> _loadedDllHashes
        = new(StringComparer.Ordinal);

    private bool _disposed;

    public GatewayModuleHostManager(
        GatewayModuleLoader loader,
        GatewayEndpointGroupCatalog catalog,
        ModuleEndpointDataSource dataSource,
        IOptionsMonitor<GatewayModuleOptions> options,
        IServiceProvider services,
        ILogger<GatewayModuleHostManager> logger)
    {
        _loader = loader;
        _catalog = catalog;
        _dataSource = dataSource;
        _options = options;
        _services = services;
        _logger = logger;
    }

    /// <summary>Snapshot of currently loaded module ids.</summary>
    public IReadOnlyCollection<string> LoadedModuleIds
    {
        get
        {
            lock (_hosts) return _hosts.Keys.ToArray();
        }
    }

    /// <summary>
    /// Returns the SHA-256 hash (uppercase hex) of the DLL currently
    /// loaded for <paramref name="moduleId"/>, or <c>null</c> when the
    /// module is not loaded or carries no DLL path (test fixtures).
    /// Used by the Phase 5c sync poller to decide whether a DLL on disk
    /// has been replaced since the last enable.
    /// </summary>
    public string? GetLoadedDllHash(string moduleId)
    {
        lock (_hosts)
            return _loadedDllHashes.GetValueOrDefault(moduleId);
    }

    /// <summary>
    /// Reconcile loaded hosts to <paramref name="opts"/>: enable any module
    /// flagged on, disable any module flagged off, and reload any module
    /// whose on-disk DLL hash has drifted from the loaded copy. Runs
    /// serially under the manager lock.
    /// </summary>
    public async Task ApplyDesiredStateAsync(GatewayModuleOptions opts, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(opts);
        ThrowIfDisposed();

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            foreach (var entry in _loader.AllEntries)
            {
                var moduleId = entry.ModuleId;
                var desired = opts.IsModuleEnabled(moduleId);
                var loaded = _hosts.ContainsKey(moduleId);

                if (desired && !loaded)
                {
                    EnableLocked(moduleId, opts);
                }
                else if (!desired && loaded)
                {
                    await DisableLockedAsync(moduleId, ct).ConfigureAwait(false);
                }
                else if (desired && loaded)
                {
                    // Reload first if the DLL on disk has been replaced;
                    // otherwise just reconcile the group flag set.
                    if (opts.HotReloadEnabled && DllHashChanged(entry))
                    {
                        await DisableLockedAsync(moduleId, ct).ConfigureAwait(false);
                        EnableLocked(moduleId, opts);
                    }
                    else
                    {
                        ReconcileGroupsLocked(moduleId, opts, ct);
                    }
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ModuleHostChange> EnableAsync(string moduleId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleId);
        ThrowIfDisposed();

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { return EnableLocked(moduleId, _options.CurrentValue); }
        finally { _gate.Release(); }
    }

    public async Task<ModuleHostChange> DisableAsync(string moduleId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleId);
        ThrowIfDisposed();

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { return await DisableLockedAsync(moduleId, ct).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    public async Task<ModuleHostChange> ReloadAsync(string moduleId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleId);
        ThrowIfDisposed();

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var disabled = await DisableLockedAsync(moduleId, ct).ConfigureAwait(false);
            var enabled = EnableLocked(moduleId, _options.CurrentValue);

            if (enabled is ModuleHostChange.Enabled)
                return ModuleHostChange.Reloaded;
            if (disabled is ModuleHostChange.Disabled)
                return ModuleHostChange.Disabled;
            return enabled;
        }
        finally
        {
            _gate.Release();
        }
    }

    private ModuleHostChange EnableLocked(string moduleId, GatewayModuleOptions opts)
    {
        if (_hosts.ContainsKey(moduleId))
            return ModuleHostChange.NoChange;

        var entry = _loader.GetEntry(moduleId);
        if (entry is null)
        {
            _logger.LogWarning(
                "Gateway module enable requested for unknown id {ModuleId}.",
                PathGuard.SanitizeForLog(moduleId));
            return ModuleHostChange.NotFound;
        }

        if (!opts.IsModuleEnabled(moduleId))
        {
            _logger.LogDebug(
                "Skipping enable for {ModuleId}: module flag is off.",
                PathGuard.SanitizeForLog(moduleId));
            return ModuleHostChange.NoChange;
        }

        // Resolve the extension instance. Three cases:
        //   1. Test entry (no DllPath): reuse the cached in-memory extension,
        //      no ALC ownership.
        //   2. Hot-reload off (default): reuse the cached extension that the
        //      loader created during DiscoverBundled. The host does not own
        //      a collectible ALC and cannot unload the DLL, but the gateway
        //      doesn't need it to: enable/disable just toggles routes.
        //   3. Hot-reload on: load a fresh copy of the DLL into a private
        //      collectible ModuleLoadContext so the next disable can unload
        //      it and the operator can replace the DLL on disk.
        IGatewayModuleExtension extension;
        ModuleLoadContext? loadContext = null;
        string? dllHash = null;

        try
        {
            if (entry.DllPath is { } dllPath && opts.HotReloadEnabled)
            {
                var (ctx, ext) = GatewayModuleLoader.LoadFromDisk(dllPath, _logger);
                loadContext = ctx;
                extension = ext;
                if (extension.ModuleId != moduleId)
                {
                    _logger.LogError(
                        "Gateway module DLL {Dll} produced unexpected id {Actual}; expected {Expected}.",
                        PathGuard.SanitizeForLog(dllPath),
                        PathGuard.SanitizeForLog(extension.ModuleId),
                        PathGuard.SanitizeForLog(moduleId));
                    ctx.Unload();
                    return ModuleHostChange.Failed;
                }
                dllHash = GatewayModuleLoader.ComputeDllHash(dllPath);
            }
            else if (entry.Extension is { } cached)
            {
                extension = cached;
                if (entry.DllPath is { } path)
                    dllHash = GatewayModuleLoader.ComputeDllHash(path);
            }
            else
            {
                _logger.LogError(
                    "Gateway module {ModuleId} has neither a cached extension nor a DLL path.",
                    PathGuard.SanitizeForLog(moduleId));
                return ModuleHostChange.Failed;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Gateway module {ModuleId} threw while resolving its extension instance.",
                PathGuard.SanitizeForLog(moduleId));
            return ModuleHostChange.Failed;
        }

        GatewayExternalModuleHost? host;
        try
        {
            host = GatewayExternalModuleHost.TryBuild(
                extension, _services, _catalog, opts, _logger, loadContext);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Gateway module {ModuleId} threw during host build.",
                PathGuard.SanitizeForLog(moduleId));
            // The fresh ALC, if any, is now orphaned; tear it down.
            try { loadContext?.Unload(); } catch { /* best effort */ }
            return ModuleHostChange.Failed;
        }

        if (host is null)
        {
            try { loadContext?.Unload(); } catch { /* best effort */ }
            return ModuleHostChange.NoChange;
        }

        _hosts[moduleId] = host;
        _loadedDllHashes[moduleId] = dllHash;
        _dataSource.SetModule(moduleId, host.EndpointSources);
        return ModuleHostChange.Enabled;
    }

    private async Task<ModuleHostChange> DisableLockedAsync(string moduleId, CancellationToken ct)
    {
        if (!_hosts.TryGetValue(moduleId, out var host))
            return ModuleHostChange.NoChange;

        // Catalog first → gate starts answering 404 for new requests.
        foreach (var groupId in host.RegisteredGroups)
            _catalog.Unregister(moduleId, groupId);

        // Data source second → route table rebuild drops endpoints.
        _dataSource.RemoveModule(moduleId);

        // Drain third → wait for in-flight requests already routed.
        var drainTimeout = TimeSpan.FromSeconds(Math.Max(1, _options.CurrentValue.DrainTimeoutSeconds));
        await host.DrainAsync(drainTimeout, ct).ConfigureAwait(false);

        // Dispose fourth → release host references and unload ALC if any.
        await host.DisposeAsync().ConfigureAwait(false);

        _hosts.Remove(moduleId);
        _loadedDllHashes.Remove(moduleId);

        // Best-effort GC verification for collectible hosts. Failure is
        // logged inside VerifyUnloaded; we do not block disable on it.
        if (host.IsCollectible)
            _ = host.VerifyUnloaded();

        _logger.LogInformation(
            "Gateway module {ModuleId} disabled; {GroupCount} group(s) unmapped.",
            PathGuard.SanitizeForLog(moduleId),
            host.RegisteredGroups.Count);
        return ModuleHostChange.Disabled;
    }

    private void ReconcileGroupsLocked(string moduleId, GatewayModuleOptions opts, CancellationToken ct)
    {
        var entry = _loader.GetEntry(moduleId);
        if (entry?.Extension is null)
            return;

        var host = _hosts[moduleId];
        var current = host.RegisteredGroups.ToHashSet(StringComparer.Ordinal);
        var desired = entry.Extension.GetEndpointGroups()
            .Where(g => opts.IsGroupEnabled(moduleId, g.GroupId))
            .Select(g => g.GroupId)
            .ToHashSet(StringComparer.Ordinal);

        if (current.SetEquals(desired))
            return;

        // Group set changed: cheapest correct path is a full disable/enable.
        // Run inline (we are already under the lock).
        DisableLockedAsync(moduleId, ct).GetAwaiter().GetResult();
        EnableLocked(moduleId, opts);
    }

    private bool DllHashChanged(GatewayModuleLoader.ModuleEntry entry)
    {
        if (entry.DllPath is null) return false;

        var current = GatewayModuleLoader.ComputeDllHash(entry.DllPath);
        var loaded = _loadedDllHashes.GetValueOrDefault(entry.ModuleId);

        // current==null means the file was deleted; treat as changed so we
        // tear down the stale host. The next enable attempt will fail
        // cleanly because LoadFromDisk will throw.
        return !string.Equals(current, loaded, StringComparison.Ordinal);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(GatewayModuleHostManager));
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (var (moduleId, host) in _hosts)
            {
                foreach (var groupId in host.RegisteredGroups)
                    _catalog.Unregister(moduleId, groupId);
                _dataSource.RemoveModule(moduleId);
                await host.DisposeAsync().ConfigureAwait(false);
            }
            _hosts.Clear();
            _loadedDllHashes.Clear();
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }
}
