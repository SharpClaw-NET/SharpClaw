namespace SharpClaw.Services;

/// <summary>Closes startup publication and owns resource factories independently of UI navigation.</summary>
internal sealed class ClientStartupLifetime
{
    private readonly Lock _gate = new();
    private readonly List<object> _owned = [];
    private readonly ClientShutdownCoordinator _shutdown = new();
    private BundledBackendMutationCoordinator? _mutations;
    private TaskCompletionSource? _creationsSettled;
    private int _creations;
    private bool _closed;

    internal bool IsClosed { get { lock (_gate) return _closed; } }

    internal void SetMutationOwner(BundledBackendMutationCoordinator mutations)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            _mutations = mutations;
        }
    }

    internal void Publish(Action publication)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            publication();
        }
    }

    internal bool TryPublishOwned<T>(T resource, Action<T> publication) where T : class
    {
        lock (_gate)
        {
            if (_closed) return false;
            _owned.Add(resource);
            publication(resource);
            return true;
        }
    }

    internal T CreateOwned<T>(Func<T> factory) where T : class
    {
        BeginCreation();
        try
        {
            var resource = factory();
            lock (_gate)
            {
                _owned.Add(resource);
                if (_closed && resource is ModulePackageStore modules) modules.CloseAdmission();
            }
            return resource;
        }
        finally { EndCreation(); }
    }

    internal T? GetOwned<T>() where T : class
    {
        lock (_gate) return _owned.OfType<T>().LastOrDefault();
    }

    internal Task CloseAsync(ClientActionDispatcher? actions, Func<Task> cleanup)
    {
        lock (_gate)
        {
            _closed = true;
            _owned.OfType<ModulePackageStore>().LastOrDefault()?.CloseAdmission();
            var creations = _creationsSettled?.Task ?? Task.CompletedTask;
            // This is the private factory-count signal, completed only after
            // adoption. Its retained cleanup join has no UI navigation dependency.
#pragma warning disable VSTHRD003
            return _shutdown.ShutdownAsync(_mutations, actions,
                () => CleanupAfterCreationsAsync(creations, cleanup));
#pragma warning restore VSTHRD003
        }
    }

    private void BeginCreation()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            if (_creations++ == 0)
                _creationsSettled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    private void EndCreation()
    {
        lock (_gate)
            if (--_creations == 0) _creationsSettled!.TrySetResult();
    }

    private static async Task CleanupAfterCreationsAsync(Task creations, Func<Task> cleanup)
    {
        // This task is the private factory count's completion signal. Every admitted
        // factory adopts its resource before signalling; none require UI navigation.
#pragma warning disable VSTHRD003
        await creations.ConfigureAwait(false);
#pragma warning restore VSTHRD003
        await cleanup().ConfigureAwait(false);
    }
}
