using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Primitives;

namespace SharpClaw.Gateway.Modules.Routing;

/// <summary>
/// Mutable <see cref="EndpointDataSource"/> that owns the gateway's
/// module-contributed endpoints. The route matcher subscribes to its change
/// token and rebuilds the route table whenever
/// <see cref="SetModule"/> or <see cref="RemoveModule"/> changes the
/// composite endpoint snapshot.
/// </summary>
/// <remarks>
/// Endpoints are partitioned by <c>moduleId</c> so a single module can be
/// enabled, disabled, or reloaded without rebuilding entries belonging to
/// other modules. Reads are lock-free; writes serialize on an internal
/// monitor and rebuild the snapshot list under the lock before publishing
/// it with a <see cref="Volatile.Write{T}"/>.
/// </remarks>
public sealed class ModuleEndpointDataSource : EndpointDataSource
{
    private readonly object _gate = new();
    private readonly Dictionary<string, IReadOnlyList<EndpointDataSource>> _byModule
        = new(StringComparer.Ordinal);

    // Snapshot of the *child sources* — not their materialized endpoints.
    // Materialization is deferred until callers (the route matcher) actually
    // read Endpoints, mirroring the lazy semantics of app.MapGroup so handler
    // metadata inference (e.g., body-parameter binding) only runs once the
    // app is wired up, not at SetModule time.
    private IReadOnlyList<EndpointDataSource> _sourcesSnapshot = Array.Empty<EndpointDataSource>();
    private CancellationTokenSource _cts = new();

    public override IReadOnlyList<Endpoint> Endpoints
    {
        get
        {
            var sources = Volatile.Read(ref _sourcesSnapshot);
            if (sources.Count == 0)
                return Array.Empty<Endpoint>();
            return sources.SelectMany(s => s.Endpoints).ToArray();
        }
    }

    public override IChangeToken GetChangeToken()
        => new CancellationChangeToken(Volatile.Read(ref _cts).Token);

    /// <summary>
    /// Replace the endpoints contributed by <paramref name="moduleId"/> with
    /// those exposed by <paramref name="sources"/>. Pass an empty collection
    /// to leave the module registered with no endpoints.
    /// </summary>
    public void SetModule(string moduleId, IReadOnlyList<EndpointDataSource> sources)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleId);
        ArgumentNullException.ThrowIfNull(sources);

        lock (_gate)
        {
            _byModule[moduleId] = sources;
            RebuildSnapshotLocked();
        }
    }

    /// <summary>
    /// Remove every endpoint contributed by <paramref name="moduleId"/>. A
    /// no-op when the module is unknown.
    /// </summary>
    public void RemoveModule(string moduleId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleId);

        lock (_gate)
        {
            if (!_byModule.Remove(moduleId))
                return;
            RebuildSnapshotLocked();
        }
    }

    /// <summary>Diagnostic accessor: snapshot of endpoints for a single module.</summary>
    public IReadOnlyList<Endpoint> GetEndpointsFor(string moduleId)
    {
        IReadOnlyList<EndpointDataSource>? sources;
        lock (_gate)
        {
            if (!_byModule.TryGetValue(moduleId, out sources))
                return Array.Empty<Endpoint>();
        }
        return sources.SelectMany(s => s.Endpoints).ToArray();
    }

    private void RebuildSnapshotLocked()
    {
        var nextSources = _byModule.Values
            .SelectMany(sources => sources)
            .ToArray();

        var oldCts = Interlocked.Exchange(ref _cts, new CancellationTokenSource());
        Volatile.Write(ref _sourcesSnapshot, nextSources);

        try { oldCts.Cancel(); }
        finally { oldCts.Dispose(); }
    }
}
