using System.Reflection;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using SharpClaw.Runtime.BLL.Modules.Foreign;
using SharpClaw.Contracts.Entities.Core;
using SharpClaw.Contracts.Entities.Core.Access;
using SharpClaw.Contracts.Entities.Core.Clearance;
using SharpClaw.Contracts.Entities.Core.Context;
using SharpClaw.Contracts.Entities.Core.Messages;
using SharpClaw.Contracts.Modules;
using SharpClaw.Contracts.Permissions;
using SharpClaw.Contracts.Persistence;
using SharpClaw.Contracts.Tasks;
using SharpClaw.Runtime.INF.Persistence;
using SharpClaw.Runtime.INF.Persistence.Modules;
using SharpClaw.ModuleHost.InProcess;
using SharpClaw.Shared.Security;
using SharpClaw.Core.Modules;
using SharpClaw.Contracts.Modules.Foreign;

namespace SharpClaw.Runtime.BLL.Modules;

/// <summary>
/// Hosts a .NET module inside the parent process while keeping module DI
/// separate from the root host container.
/// </summary>
public sealed class InProcessModuleHost : IModuleRuntimeHost
{
    private readonly ModuleLoadContext _loadContext;
    private readonly ServiceProvider _serviceProvider;
    private readonly WeakReference _loadContextReference;
    private readonly TaskCompletionSource _drainTcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private int _inFlightCount;
    private volatile bool _draining;

    private InProcessModuleHost(
        ModuleLoadContext loadContext,
        ISharpClawCoreModule module,
        ServiceProvider serviceProvider,
        string sourceDirectory)
    {
        _loadContext = loadContext;
        _loadContextReference = new WeakReference(loadContext);
        Module = module;
        _serviceProvider = serviceProvider;
        SourceDirectory = sourceDirectory;
    }

    public ISharpClawCoreModule Module { get; }
    public string SourceDirectory { get; }
    public IServiceProvider Services => _serviceProvider;

    public static InProcessModuleHost Load(
        string moduleDirectory,
        string entryAssemblyDirectory,
        ModuleManifest manifest,
        ModuleManifestRuntimeInfo runtimeInfo,
        IServiceProvider hostServices)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(entryAssemblyDirectory);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(runtimeInfo);
        ArgumentNullException.ThrowIfNull(hostServices);

        if (!runtimeInfo.IsDotNet)
        {
            throw new ArgumentException(
                $"In-process hosting only supports .NET modules. Module '{manifest.Id}' declares runtime '{runtimeInfo.Runtime}'.",
                nameof(runtimeInfo));
        }

        runtimeInfo.EnsureDotNetEntryAssembly(manifest);

        var canonicalModuleDirectory = Path.GetFullPath(moduleDirectory);
        var canonicalAssemblyDirectory = Path.GetFullPath(entryAssemblyDirectory);
        var dllPath = PathGuard.EnsureContainedIn(
            Path.Combine(canonicalAssemblyDirectory, manifest.EntryAssembly),
            canonicalAssemblyDirectory);

        if (!File.Exists(dllPath))
            throw new FileNotFoundException(
                $"Entry assembly '{manifest.EntryAssembly}' not found for module '{manifest.Id}'.",
                dllPath);

        var loadContext = new ModuleLoadContext(dllPath);
        var assembly = loadContext.LoadFromAssemblyPath(Path.GetFullPath(dllPath));
        var module = DotNetModuleAssemblyLoader.CreateModuleInstance(
            assembly,
            manifest,
            runtimeInfo,
            dllPath);

        if (!string.Equals(module.Id, manifest.Id, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Module class Id '{module.Id}' does not match manifest id '{manifest.Id}'.");
        }

        if (!string.Equals(module.ToolPrefix, manifest.ToolPrefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Module class ToolPrefix '{module.ToolPrefix}' does not match manifest toolPrefix '{manifest.ToolPrefix}'.");
        }

        var services = BuildServices(module, assembly, hostServices);
        return new InProcessModuleHost(loadContext, module, services, canonicalModuleDirectory);
    }

    public IServiceScope CreateScope() => _serviceProvider.CreateScope();

    public bool TryAcquireExecution()
    {
        while (true)
        {
            if (_draining) return false;

            var current = Volatile.Read(ref _inFlightCount);
            if (Interlocked.CompareExchange(ref _inFlightCount, current + 1, current) == current)
            {
                if (_draining)
                {
                    ReleaseExecution();
                    return false;
                }

                return true;
            }
        }
    }

    public void ReleaseExecution()
    {
        var remaining = Interlocked.Decrement(ref _inFlightCount);
        if (remaining == 0 && _draining)
            _drainTcs.TrySetResult();
    }

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

    public async ValueTask DisposeAsync()
    {
        await _serviceProvider.DisposeAsync();
        _loadContext.Unload();
    }

    public async Task<bool> VerifyUnloadedAsync(
        int maxAttempts = 10,
        TimeSpan? delay = null,
        CancellationToken ct = default)
    {
        var step = delay ?? TimeSpan.FromMilliseconds(100);
        for (var i = 0; i < maxAttempts; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            if (!_loadContextReference.IsAlive)
                return true;

            await Task.Delay(step, ct);
        }

        return false;
    }

    private static ServiceProvider BuildServices(
        ISharpClawCoreModule module,
        Assembly moduleAssembly,
        IServiceProvider hostServices)
    {
        var services = new ServiceCollection();

        if (hostServices.GetService<ILoggerFactory>() is { } loggerFactory)
        {
            services.AddSingleton(loggerFactory);
            services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
        }
        else
        {
            services.AddLogging();
        }

        if (hostServices.GetService<IHttpClientFactory>() is { } httpClientFactory)
            services.AddSingleton(httpClientFactory);
        else
            services.AddHttpClient();

        services.TryAddSingleton(TimeProvider.System);
        ForwardHostSingleton<IConfiguration>(hostServices, services);

        module.ConfigureServices(services);
        RegisterTaskOperationDescriptorProviders(services, moduleAssembly);
        RegisterHostCapabilities(services, module.Id, hostServices);

        return services.BuildServiceProvider();
    }

    private static void RegisterHostCapabilities(
        IServiceCollection services,
        string moduleId,
        IServiceProvider hostServices)
    {
        RemoveProtectedHostCapabilityRegistrations(services);

        var hostScopeFactory = hostServices.GetRequiredService<IServiceScopeFactory>();
        services.AddScoped(_ => new InProcessHostCapabilityScope(hostScopeFactory));
        services.AddScoped<ModuleExecutionContext>(_ => new ModuleExecutionContext { ModuleId = moduleId });

        services.AddScoped<IModuleConfigStore>(sp =>
        {
            var db = sp.GetRequiredService<InProcessHostCapabilityScope>()
                .Resolve<SharpClawDbContext>();
            return new ModuleConfigStore(db, moduleId);
        });

        services.AddScoped<IModuleStorageGateway>(sp =>
            new ModuleScopedStorageGateway(
                sp.GetRequiredService<InProcessHostCapabilityScope>()
                    .Resolve<IModuleStorageGateway>(),
                moduleId));

        services.AddScoped<IModuleDbContextFactory>(sp =>
            new ModuleScopedDbContextFactory(
                sp.GetRequiredService<InProcessHostCapabilityScope>()
                    .Resolve<IModuleDbContextFactory>(),
                sp.GetRequiredService<InProcessHostCapabilityScope>()
                    .Resolve<RuntimeModuleDbContextRegistry>(),
                moduleId));

        services.AddScoped<ISharpClawDataContext>(sp =>
            new ModuleReadOnlySharpClawDataContext(
                sp.GetRequiredService<InProcessHostCapabilityScope>()
                    .Resolve<SharpClawDbContext>()));

        ForwardHostScoped<IAgentJobController>(services);
        ForwardHostScoped<IAgentJobReader>(services);
        ForwardHostScoped<IAgentJobCostTracker>(services);
        ForwardHostScoped<IAgentManager>(services);
        ForwardHostScoped<IContainerProvisioner>(services);
        ForwardHostScoped<IConversationSteering>(services);
        ForwardHostScoped<ICoreEntityIdProvider>(services);
        ForwardHostScoped<IModelInfoProvider>(services);
        ForwardHostScoped<IInProcessModuleSecretReader>(services);
        ForwardHostScoped<IModelRegistrar>(services);
        ForwardHostScoped<IModuleInfoProvider>(services);
        ForwardHostScoped<IModuleLifecycleManager>(services);
        ForwardHostScoped<IForeignModuleProtocolContractResolver>(services);
        ForwardHostScoped<IThreadResolver>(services);
        ForwardHostScoped<IGlobalFlagEvaluator>(services);
        ForwardHostScoped<ITaskAuthoring>(services);
        ForwardHostScoped<ITaskInstanceLauncher>(services);
        ForwardHostScoped<IHostAgentBridge>(services);
        ForwardHostScoped<IHostQueueMetrics>(services);
        ForwardHostScoped<ICliIdResolver>(services);
        ForwardHostScoped<ISharpClawEventSinkRegistry>(services);
    }

    private static void RemoveProtectedHostCapabilityRegistrations(IServiceCollection services)
    {
        services.RemoveAll<ModuleExecutionContext>();
        services.RemoveAll<IModuleConfigStore>();
        services.RemoveAll<IModuleStorageGateway>();
        services.RemoveAll<IModuleDbContextFactory>();
        services.RemoveAll<ISharpClawDataContext>();
        services.RemoveAll<IAgentJobController>();
        services.RemoveAll<IAgentJobReader>();
        services.RemoveAll<IAgentJobCostTracker>();
        services.RemoveAll<IAgentManager>();
        services.RemoveAll<IContainerProvisioner>();
        services.RemoveAll<IConversationSteering>();
        services.RemoveAll<ICoreEntityIdProvider>();
        services.RemoveAll<IModelInfoProvider>();
        services.RemoveAll<IInProcessModuleSecretReader>();
        services.RemoveAll<IModelRegistrar>();
        services.RemoveAll<IModuleInfoProvider>();
        services.RemoveAll<IModuleLifecycleManager>();
        services.RemoveAll<IForeignModuleProtocolContractResolver>();
        services.RemoveAll<IThreadResolver>();
        services.RemoveAll<IGlobalFlagEvaluator>();
        services.RemoveAll<ITaskAuthoring>();
        services.RemoveAll<ITaskInstanceLauncher>();
        services.RemoveAll<IHostAgentBridge>();
        services.RemoveAll<IHostQueueMetrics>();
        services.RemoveAll<ICliIdResolver>();
        services.RemoveAll<ISharpClawEventSinkRegistry>();
    }

    private static void RegisterTaskOperationDescriptorProviders(
        IServiceCollection services,
        Assembly assembly)
    {
        foreach (var providerType in assembly.GetTypes()
                     .Where(type => !type.IsAbstract
                                    && !type.IsInterface
                                    && typeof(ITaskOperationDescriptorProvider).IsAssignableFrom(type)
                                    && type.GetConstructor(Type.EmptyTypes) is not null))
        {
            services.AddSingleton(typeof(ITaskOperationDescriptorProvider), providerType);
        }
    }

    private static void ForwardHostSingleton<T>(
        IServiceProvider hostServices,
        IServiceCollection services)
        where T : class
    {
        if (hostServices.GetService<T>() is { } instance)
            services.AddSingleton(instance);
    }

    private static void ForwardHostScoped<T>(IServiceCollection services)
        where T : class
    {
        services.AddScoped<T>(sp =>
            sp.GetRequiredService<InProcessHostCapabilityScope>().Resolve<T>());
    }

    private sealed class InProcessHostCapabilityScope(IServiceScopeFactory hostScopeFactory) : IDisposable
    {
        private readonly IServiceScope _hostScope = hostScopeFactory.CreateScope();

        public T Resolve<T>()
            where T : notnull =>
            _hostScope.ServiceProvider.GetRequiredService<T>();

        public void Dispose() => _hostScope.Dispose();
    }

    private sealed class ModuleScopedStorageGateway(
        IModuleStorageGateway inner,
        string moduleId) : IModuleStorageGateway
    {
        public IReadOnlyList<ModuleStorageContractDescriptor> ListContracts() =>
            [.. inner.ListContracts()
                .Where(contract => string.Equals(contract.ModuleId, moduleId, StringComparison.Ordinal))];

        public Task<JsonElement> InvokeAsync(
            string requestedModuleId,
            string storageName,
            string operation,
            JsonElement parameters,
            CancellationToken ct = default)
        {
            if (!string.Equals(requestedModuleId, moduleId, StringComparison.Ordinal))
            {
                throw new UnauthorizedAccessException(
                    $"Module '{moduleId}' cannot access storage owned by module '{requestedModuleId}'.");
            }

            return inner.InvokeAsync(requestedModuleId, storageName, operation, parameters, ct);
        }
    }

    private sealed class ModuleScopedDbContextFactory(
        IModuleDbContextFactory inner,
        RuntimeModuleDbContextRegistry registry,
        string moduleId) : IModuleDbContextFactory
    {
        public object CreateDbContext(Type dbContextType)
        {
            var registration = registry.GetRegistration(dbContextType)
                ?? throw new InvalidOperationException(
                    $"Module DbContext '{dbContextType.FullName}' is not registered.");

            if (!string.Equals(registration.ModuleId, moduleId, StringComparison.Ordinal))
            {
                throw new UnauthorizedAccessException(
                    $"Module '{moduleId}' cannot create DbContext '{dbContextType.FullName}' owned by module '{registration.ModuleId}'.");
            }

            return inner.CreateDbContext(dbContextType);
        }
    }

    private sealed class ModuleReadOnlySharpClawDataContext(
        SharpClawDbContext db) : ISharpClawDataContext
    {
        public IQueryable<AgentDB> Agents => db.Agents.AsNoTracking();
        public IQueryable<ChannelDB> Channels => db.Channels.AsNoTracking();
        public IQueryable<ChannelContextDB> AgentContexts => db.AgentContexts.AsNoTracking();
        public IQueryable<ChatThreadDB> ChatThreads => db.ChatThreads.AsNoTracking();
        public IQueryable<ChatMessageDB> ChatMessages => db.ChatMessages.AsNoTracking();
        public IQueryable<PermissionSetDB> PermissionSets => db.PermissionSets.AsNoTracking();
        public IQueryable<GlobalFlagDB> GlobalFlags => db.GlobalFlags.AsNoTracking();
        public IQueryable<RoleDB> Roles => db.Roles.AsNoTracking();
    }
}
