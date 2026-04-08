using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SharpClaw.Contracts.Modules;

namespace SharpClaw.Application.Core.Modules;

/// <summary>
/// Hosts a single external module loaded via a collectible <see cref="ModuleLoadContext"/>.
/// Each host owns a per-module <see cref="ServiceProvider"/>, an in-flight execution
/// counter, and the unload lifecycle.
/// </summary>
public sealed class ExternalModuleHost : IAsyncDisposable
{
    private readonly ModuleLoadContext _loadContext;
    private readonly ServiceProvider _serviceProvider;
    private readonly WeakReference _contextRef;
    private readonly TaskCompletionSource _drainTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _inFlightCount;
    private volatile bool _draining;

    public ISharpClawModule Module { get; }
    public string SourceDirectory { get; }

    private ExternalModuleHost(
        ModuleLoadContext loadContext,
        ISharpClawModule module,
        ServiceProvider serviceProvider,
        string sourceDirectory)
    {
        _loadContext = loadContext;
        _serviceProvider = serviceProvider;
        _contextRef = new WeakReference(loadContext);
        Module = module;
        SourceDirectory = sourceDirectory;
    }

    /// <summary>
    /// Loads an external module from <paramref name="moduleDir"/> using a collectible ALC
    /// and builds an isolated <see cref="ServiceProvider"/> with forwarded host services.
    /// </summary>
    public static ExternalModuleHost Load(
        string moduleDir,
        ModuleManifest manifest,
        IServiceProvider hostServices,
        ILoggerFactory loggerFactory)
    {
        var dllPath = Path.Combine(moduleDir, manifest.EntryAssembly);
        if (!File.Exists(dllPath))
            throw new FileNotFoundException(
                $"Entry assembly '{manifest.EntryAssembly}' not found in '{moduleDir}'.", dllPath);

        var context = new ModuleLoadContext(dllPath);
        var assembly = context.LoadFromAssemblyPath(Path.GetFullPath(dllPath));

        var moduleType = assembly.GetTypes()
            .FirstOrDefault(t => t.IsAssignableTo(typeof(ISharpClawModule)) && !t.IsAbstract)
            ?? throw new InvalidOperationException(
                $"No ISharpClawModule implementation found in '{Path.GetFileName(dllPath)}'.");

        var module = (ISharpClawModule)Activator.CreateInstance(moduleType)!;

        if (module.Id != manifest.Id)
            throw new InvalidOperationException(
                $"Module class Id '{module.Id}' does not match manifest id '{manifest.Id}'.");

        if (module.ToolPrefix != manifest.ToolPrefix)
            throw new InvalidOperationException(
                $"Module class ToolPrefix '{module.ToolPrefix}' does not match manifest toolPrefix '{manifest.ToolPrefix}'.");

        // Build per-module DI container with forwarded host services.
        var services = new ServiceCollection();
        services.AddSingleton(loggerFactory);
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));

        var config = hostServices.GetService<IConfiguration>();
        if (config is not null) services.AddSingleton(config);

        var httpFactory = hostServices.GetService<IHttpClientFactory>();
        if (httpFactory is not null) services.AddSingleton(httpFactory);

        module.ConfigureServices(services);
        var moduleProvider = services.BuildServiceProvider();

        return new ExternalModuleHost(context, module, moduleProvider, moduleDir);
    }

    /// <summary>Creates a scoped <see cref="IServiceProvider"/> from the per-module container.</summary>
    public IServiceScope CreateScope() => _serviceProvider.CreateScope();

    /// <summary>Root per-module service provider (for InitializeAsync / SeedDataAsync).</summary>
    public IServiceProvider Services => _serviceProvider;

    /// <summary>
    /// Atomically increments the in-flight counter. Returns <c>false</c> if
    /// the host is draining (about to unload).
    /// </summary>
    public bool TryAcquireExecution()
    {
        while (true)
        {
            if (_draining) return false;

            var current = Volatile.Read(ref _inFlightCount);
            if (Interlocked.CompareExchange(ref _inFlightCount, current + 1, current) == current)
            {
                // Double-check after increment closes the race window.
                if (_draining) { ReleaseExecution(); return false; }
                return true;
            }
        }
    }

    /// <summary>Decrements the in-flight counter and signals the drain gate when it reaches zero.</summary>
    public void ReleaseExecution()
    {
        var remaining = Interlocked.Decrement(ref _inFlightCount);
        if (remaining == 0 && _draining)
            _drainTcs.TrySetResult();
    }

    /// <summary>
    /// Sets the draining flag and waits for all in-flight executions to complete
    /// within the specified <paramref name="timeout"/>.
    /// </summary>
    public async Task DrainAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        _draining = true;
        Thread.MemoryBarrier();

        if (Volatile.Read(ref _inFlightCount) == 0)
        {
            _drainTcs.TrySetResult();
            return;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        await _drainTcs.Task.WaitAsync(cts.Token);
    }

    /// <summary>Disposes the per-module service provider and triggers ALC unload.</summary>
    public async ValueTask DisposeAsync()
    {
        await _serviceProvider.DisposeAsync();
        _loadContext.Unload();
    }

    /// <summary>
    /// Best-effort verification that the collectible ALC was garbage-collected.
    /// Returns <c>true</c> if the context is no longer alive.
    /// </summary>
    public bool VerifyUnloaded(int maxAttempts = 10)
    {
        for (var i = 0; i < maxAttempts; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            if (!_contextRef.IsAlive) return true;
            Thread.Sleep(100);
        }

        return false;
    }
}
