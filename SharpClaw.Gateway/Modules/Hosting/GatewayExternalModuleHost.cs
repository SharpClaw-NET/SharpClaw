using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using SharpClaw.Gateway.Abstractions;
using SharpClaw.Gateway.Security;
using SharpClaw.ModuleHost.InProcess;
using SharpClaw.Utils.Security;

namespace SharpClaw.Gateway.Modules.Hosting;

/// <summary>
/// Gateway-side per-module host. Builds the module's
/// <see cref="EndpointDataSource"/>s under <c>/api/modules/{ModuleId}/...</c>,
/// tracks in-flight request count for drain, and (when constructed with a
/// collectible <c>AssemblyLoadContext</c>) unloads the ALC on dispose so
/// the module DLL can be replaced on disk.
/// </summary>
/// <remarks>
/// Phase 5b: hosts that own a <see cref="ModuleLoadContext"/> implement the
/// full unload contract — <see cref="DisposeAsync"/> calls
/// <c>ModuleLoadContext.Unload()</c> and <see cref="VerifyUnloaded"/> waits
/// for the GC to collect the context. Hosts created from an in-memory
/// extension (the <see cref="GatewayModuleLoader.FromExtensions"/> test
/// path) skip the ALC step and report <c>true</c> from
/// <see cref="VerifyUnloaded"/> immediately because there is nothing to
/// unload.
/// </remarks>
public sealed class GatewayExternalModuleHost : IAsyncDisposable
{
    private readonly ILogger _logger;
    private readonly ModuleLoadContext? _loadContext;
    private readonly WeakReference? _contextRef;
    private int _inflight;
    private bool _disposed;

    private GatewayExternalModuleHost(
        IGatewayModuleExtension extension,
        IReadOnlyList<EndpointDataSource> endpointSources,
        IReadOnlyList<string> registeredGroups,
        ModuleLoadContext? loadContext,
        ILogger logger)
    {
        Extension = extension;
        EndpointSources = endpointSources;
        RegisteredGroups = registeredGroups;
        _loadContext = loadContext;
        _contextRef = loadContext is null ? null : new WeakReference(loadContext);
        _logger = logger;
    }

    public string ModuleId => Extension.ModuleId;

    public IGatewayModuleExtension Extension { get; }

    /// <summary>The endpoint data sources this host contributes to the gateway's route table.</summary>
    public IReadOnlyList<EndpointDataSource> EndpointSources { get; }

    /// <summary>Group ids successfully registered with the catalog (subset of declared groups).</summary>
    public IReadOnlyList<string> RegisteredGroups { get; }

    /// <summary>True when this host owns a collectible <see cref="ModuleLoadContext"/>.</summary>
    public bool IsCollectible => _loadContext is not null;

    public int InflightCount => Volatile.Read(ref _inflight);

    /// <summary>
    /// Build a host for <paramref name="extension"/>. Maps every group whose
    /// configuration flag is true onto a private route builder, registers
    /// each successfully-mapped group in <paramref name="catalog"/>, and
    /// returns the resulting host. On total failure (no groups registered)
    /// the catalog rolls back and <c>null</c> is returned.
    /// </summary>
    /// <param name="loadContext">
    /// Optional collectible ALC that owns <paramref name="extension"/>'s
    /// type. When non-null, the host calls <c>Unload()</c> on dispose and
    /// <see cref="VerifyUnloaded"/> waits for the GC to reclaim it. When
    /// <c>null</c>, the extension is assumed to live in the default ALC
    /// (test path) and unload is a no-op.
    /// </param>
    public static GatewayExternalModuleHost? TryBuild(
        IGatewayModuleExtension extension,
        IServiceProvider services,
        GatewayEndpointGroupCatalog catalog,
        GatewayModuleOptions options,
        ILogger logger,
        ModuleLoadContext? loadContext = null)
    {
        ArgumentNullException.ThrowIfNull(extension);
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        if (!options.IsModuleEnabled(extension.ModuleId))
            return null;

        var routeBuilder = new HostEndpointRouteBuilder(services);
        var registered = new List<string>();

        // Pre-allocate the host so the in-flight counter has a stable
        // identity for the per-route filter to capture before we publish.
        var counterBox = new InflightCounter();

        foreach (var group in extension.GetEndpointGroups())
        {
            if (!options.IsGroupEnabled(extension.ModuleId, group.GroupId))
                continue;

            var prefix = $"/api/modules/{extension.ModuleId}/{group.GroupId}";

            if (!catalog.TryRegister(extension.ModuleId, group))
            {
                logger.LogError(
                    "Gateway module group {Prefix} is already registered; skipping duplicate from {ModuleId}.",
                    prefix,
                    PathGuard.SanitizeForLog(extension.ModuleId));
                continue;
            }

            var routeGroup = routeBuilder.MapGroup(prefix);
            var policy = group.RateLimitPolicy ?? RateLimiterConfiguration.GlobalPolicy;
            routeGroup.RequireRateLimiting(policy);
            routeGroup.AddEndpointFilter(async (ctx, next) =>
            {
                counterBox.Increment();
                try { return await next(ctx); }
                finally { counterBox.Decrement(); }
            });

            var builder = new GatewayEndpointGroupBuilder(routeGroup, group.GroupId, prefix);
            try
            {
                extension.MapEndpoints(builder);
                registered.Add(group.GroupId);
                logger.LogInformation(
                    "Gateway module endpoint group registered: {Prefix} ({DisplayName}) policy={Policy}",
                    prefix,
                    group.DisplayName,
                    policy);
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "Gateway module {ModuleId} threw while mapping group {GroupId}; unregistering prefix.",
                    PathGuard.SanitizeForLog(extension.ModuleId),
                    PathGuard.SanitizeForLog(group.GroupId));
                catalog.Unregister(extension.ModuleId, group.GroupId);
            }
        }

        if (registered.Count == 0)
            return null;

        var host = new GatewayExternalModuleHost(
            extension,
            routeBuilder.DataSources.ToArray(),
            registered,
            loadContext,
            logger);
        counterBox.Bind(host);
        return host;
    }

    /// <summary>
    /// Wait for in-flight requests routed to this host's endpoints to
    /// complete, up to <paramref name="timeout"/>. Returns <c>true</c> when
    /// the host fully drained, <c>false</c> on timeout.
    /// </summary>
    public async Task<bool> DrainAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        if (timeout <= TimeSpan.Zero)
            return InflightCount == 0;

        var deadline = DateTime.UtcNow + timeout;
        while (InflightCount > 0)
        {
            if (DateTime.UtcNow >= deadline)
            {
                _logger.LogWarning(
                    "Gateway module {ModuleId} drain timed out with {Inflight} request(s) still in flight.",
                    PathGuard.SanitizeForLog(ModuleId),
                    InflightCount);
                return false;
            }

            try { await Task.Delay(25, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return InflightCount == 0; }
        }
        return true;
    }

    /// <summary>
    /// Best-effort verification that the collectible ALC was
    /// garbage-collected. Returns <c>true</c> when the context is no longer
    /// alive, or when this host does not own a collectible ALC at all
    /// (test-path hosts trivially "verify"). When the ALC stays alive
    /// after every retry — typically because a host-side delegate captured
    /// a type from the loaded module — a warning is logged and
    /// <c>false</c> is returned. The condition is a memory leak signal,
    /// not a functional bug; the new host is already serving traffic.
    /// </summary>
    public bool VerifyUnloaded(int maxAttempts = 10)
    {
        if (_contextRef is null) return true;

        for (var i = 0; i < maxAttempts; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            if (!_contextRef.IsAlive) return true;
            Thread.Sleep(100);
        }

        _logger.LogWarning(
            "Gateway module {ModuleId} ALC failed to unload after {Attempts} attempt(s); "
            + "the assembly will leak until the gateway restarts. "
            + "This usually indicates a captured delegate or static reference rooted by the host.",
            PathGuard.SanitizeForLog(ModuleId),
            maxAttempts);
        return false;
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;

        // For collectible hosts, dispose the extension if it implements
        // IAsyncDisposable / IDisposable so the loaded module gets a
        // chance to release its own resources before the ALC unloads.
        // For test-path hosts (in-memory extension), the extension is
        // shared with the loader's cache and must not be disposed here.
        if (_loadContext is not null)
        {
            switch (Extension)
            {
                case IAsyncDisposable ad:
                    return DisposeCollectibleAsync(ad);
                case IDisposable d:
                    try { d.Dispose(); }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex,
                            "Gateway module {ModuleId} threw during Dispose.",
                            PathGuard.SanitizeForLog(ModuleId));
                    }
                    break;
            }

            try { _loadContext.Unload(); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Gateway module {ModuleId} ALC.Unload threw.",
                    PathGuard.SanitizeForLog(ModuleId));
            }
        }

        return ValueTask.CompletedTask;
    }

    private async ValueTask DisposeCollectibleAsync(IAsyncDisposable extension)
    {
        try { await extension.DisposeAsync().ConfigureAwait(false); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Gateway module {ModuleId} threw during DisposeAsync.",
                PathGuard.SanitizeForLog(ModuleId));
        }

        try { _loadContext!.Unload(); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Gateway module {ModuleId} ALC.Unload threw.",
                PathGuard.SanitizeForLog(ModuleId));
        }
    }

    private sealed class InflightCounter
    {
        private GatewayExternalModuleHost? _owner;
        public void Bind(GatewayExternalModuleHost owner) => _owner = owner;

        public void Increment()
        {
            if (_owner is { } o) Interlocked.Increment(ref o._inflight);
        }

        public void Decrement()
        {
            if (_owner is { } o) Interlocked.Decrement(ref o._inflight);
        }
    }

    /// <summary>
    /// Minimal <see cref="IEndpointRouteBuilder"/> used to capture endpoint
    /// data sources mapped by a single module without polluting the global
    /// app data sources. The collected sources are later handed to
    /// <see cref="Routing.ModuleEndpointDataSource"/>.
    /// </summary>
    private sealed class HostEndpointRouteBuilder(IServiceProvider services) : IEndpointRouteBuilder
    {
        public IServiceProvider ServiceProvider { get; } = services;
        public ICollection<EndpointDataSource> DataSources { get; } = new List<EndpointDataSource>();

        public IApplicationBuilder CreateApplicationBuilder()
            => ServiceProvider.GetRequiredService<IApplicationBuilderFactory>()
                .CreateBuilder(ServiceProvider.GetRequiredService<IServer>().Features);
    }
}
