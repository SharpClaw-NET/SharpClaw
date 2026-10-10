using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.Kernel;
using SharpClaw.Contracts.Persistence;
using SharpClaw.Core.Kernel;
using SharpClaw.SidecarHost.InProcess;
using SharpClaw.SidecarHost.OutOfProcess;
using SharpClaw.Runtime.BLL.Kernel;
using SharpClaw.Runtime.BLL.Configuration;
using SharpClaw.Shared.Instances;

namespace SharpClaw.Runtime.Host;

/// <summary>Loads enabled .NET registrations whose manifest selects in-process hosting.</summary>
internal sealed class PackagedDotNetRegistrationSet : IDisposable, IAsyncDisposable
{
    private readonly List<ServiceDescriptor> _services;
    private readonly List<InProcessRegistrationHost> _inProcessHosts;
    private readonly List<SharpClawModuleSettingsPage> _frontendSettings;
    private readonly List<OutOfProcessRegistrationProxy> _sidecarRegistrations = [];
    private readonly List<PackagedSidecarProcess> _sidecarProcesses = [];
    private PackagedApplicationRegistry _application =
        PackagedApplicationRegistry.Empty;
    private AsyncServiceScope? _capabilityScope;
    private readonly Lock _disposeGate = new();
    private Task? _disposeTask;

    private PackagedDotNetRegistrationSet(
        IReadOnlyList<ServiceDescriptor> services,
        IReadOnlyList<InProcessRegistrationHost> inProcessHosts,
        IReadOnlyList<SharpClawModuleSettingsPage> frontendSettings)
    {
        _services = services.ToList();
        _inProcessHosts = inProcessHosts.ToList();
        _frontendSettings = frontendSettings.ToList();
        _application = new PackagedApplicationRegistry(_inProcessHosts, []);
        _application.ValidateFrontendSettings(_frontendSettings);
    }

    public IReadOnlyList<ServiceDescriptor> Services => _services;

    public IReadOnlyList<string> SourceIds =>
        _inProcessHosts.Select(host => host.Manifest.Id)
            .Concat(_sidecarRegistrations.Select(registration => registration.SourceId))
            .ToArray();

    internal IReadOnlyList<OutOfProcessRegistrationProxy> Sidecars => _sidecarRegistrations;

    public PackagedApplicationRegistry Application => _application;
    public IReadOnlyList<SharpClawModuleSettingsPage> FrontendSettings => _frontendSettings.AsReadOnly();

    public static PackagedDotNetRegistrationSet Load(
        string registrationsRoot,
        IConfiguration configuration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(registrationsRoot);
        return Load([registrationsRoot], configuration);
    }

    internal static PackagedDotNetRegistrationSet Load(
        IReadOnlyList<string> registrationRoots,
        IConfiguration configuration)
    {
#pragma warning disable VSTHRD002 // Preserve the existing synchronous loader contract; production loading awaits the asynchronous core below.
#pragma warning disable CA2000 // This wrapper returns the acquired registration set to its caller and disposes it if authority initialization fails.
        var registrationSet = LoadCoreAsync(registrationRoots, configuration, CancellationToken.None)
            .GetAwaiter().GetResult();
#pragma warning restore CA2000
#pragma warning restore VSTHRD002
        try
        {
            AddInProcessAuthorities(registrationSet, []);
            return registrationSet;
        }
        catch
        {
            registrationSet.Dispose();
            throw;
        }
    }

    private static async Task<PackagedDotNetRegistrationSet> LoadCoreAsync(
        IReadOnlyList<string> registrationRoots,
        IConfiguration configuration,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(registrationRoots);
        ArgumentNullException.ThrowIfNull(configuration);

        var services = new List<ServiceDescriptor>();
        var inProcessHosts = new List<InProcessRegistrationHost>();
        var frontendSettings = new List<SharpClawModuleSettingsPage>();

        try
        {
            var manifests = EnumerateManifests(registrationRoots);
            for (var index = 0; index < manifests.Count; index++)
            {
                var manifest = manifests[index];
                if (!IsEnabled(manifest, configuration))
                    continue;

                if (!manifest.RuntimeInfo.IsDotNet)
                    throw new NotSupportedException(
                        $"The registration '{manifest.Id}' declares unsupported runtime '{manifest.RuntimeInfo.Runtime}'. " +
                        "SharpClaw supports only .NET registration runtimes.");

                if (!manifest.RuntimeInfo.IsInProcessHostMode)
                    continue;

                var settings = ReadFrontendSettings(manifest);
                var registrationDirectory = Path.GetDirectoryName(manifest.ManifestPath)!;
                var host = await InProcessRegistrationHost.LoadAsync(
                    registrationDirectory, cancellationToken).ConfigureAwait(false);
                inProcessHosts.Add(host);
                services.AddRange(host.ServiceDescriptors);
                frontendSettings.AddRange(settings);
            }

            return new PackagedDotNetRegistrationSet(services, inProcessHosts, frontendSettings);
        }
        catch (Exception exception)
        {
            var failure = ExceptionDispatchInfo.Capture(exception);
            for (var index = 0; index < inProcessHosts.Count; index++)
                _ = await CaptureCleanupFailureAsync(inProcessHosts[index], failure).ConfigureAwait(false);
            throw;
        }
    }

    public static Task<PackagedDotNetRegistrationSet> LoadProductionAsync(
        string registrationsRoot,
        IConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(registrationsRoot);
        return LoadProductionAsync([registrationsRoot], configuration, cancellationToken);
    }

    internal static async Task<PackagedDotNetRegistrationSet> LoadProductionAsync(
        IReadOnlyList<string> registrationRoots,
        IConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(registrationRoots);
        if (registrationRoots.Count == 0 || registrationRoots.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("At least one registration root is required.", nameof(registrationRoots));
        ArgumentNullException.ThrowIfNull(configuration);

        var registrationSet = await LoadCoreAsync(
            registrationRoots, configuration, cancellationToken).ConfigureAwait(false);
        var pending = new List<PendingSidecar>();
        try
        {
            await DiscoverSidecarsAsync(registrationSet, registrationRoots,
                configuration, pending, cancellationToken).ConfigureAwait(false);

            await AuthorizeSidecarsAsync(registrationSet, pending, cancellationToken).ConfigureAwait(false);

            AddExternalContractExports(
                registrationSet._services,
                pending.Select(item => new ExternalContractExportSource(
                    item.Manifest.Id,
                    item.Manifest.Manifest.Exports ?? [])).ToArray());

            registrationSet._application = new PackagedApplicationRegistry(
                registrationSet._inProcessHosts,
                registrationSet._sidecarRegistrations);
            registrationSet._application.ValidateFrontendSettings(registrationSet._frontendSettings);

            return registrationSet;
        }
        catch (Exception exception)
        {
            var failure = ExceptionDispatchInfo.Capture(exception);
            for (var index = 0; index < pending.Count; index++)
                _ = await CaptureCleanupFailureAsync(pending[index].Discovery, failure).ConfigureAwait(false);
            _ = await CaptureCleanupFailureAsync(registrationSet, failure).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task DiscoverSidecarsAsync(
        PackagedDotNetRegistrationSet registrationSet,
        IReadOnlyList<string> registrationRoots,
        IConfiguration configuration,
        List<PendingSidecar> pending,
        CancellationToken cancellationToken)
    {
        foreach (var manifest in EnumerateManifests(registrationRoots)
                     .Where(item => IsEnabled(item, configuration) && item.RuntimeInfo.IsSidecarHostMode))
        {
            if (!manifest.RuntimeInfo.IsDotNet)
            {
                throw new NotSupportedException(
                    $"The registration '{manifest.Id}' declares unsupported runtime " +
                    $"'{manifest.RuntimeInfo.Runtime}'. SharpClaw supports only .NET registration runtimes.");
            }

            manifest.RuntimeInfo.EnsureDotNetEntryAssembly(manifest.Manifest);
            _ = ReadFrontendSettings(manifest);
#pragma warning disable CA2000 // The registration set adopts every started process immediately and disposes it if discovery or later authorization fails.
            var process = await PackagedSidecarProcess.StartAsync(
                manifest,
                configuration,
                cancellationToken).ConfigureAwait(false);
#pragma warning restore CA2000
            try
            {
                registrationSet._sidecarProcesses.Add(process);
                var discovery = await OutOfProcessRegistrationClient.DiscoverAsync(
                    process.ControlAddress,
                    process.ControlToken,
                    cancellationToken).ConfigureAwait(false);
                pending.Add(new PendingSidecar(manifest, process, discovery));
            }
            catch
            {
                await process.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
    }

    private static async Task AuthorizeSidecarsAsync(
        PackagedDotNetRegistrationSet registrationSet,
        List<PendingSidecar> pending,
        CancellationToken cancellationToken)
    {
        var inProcessDiscoveries = registrationSet._inProcessHosts
            .Select(host => new PendingInProcess(
                host,
                CompiledBehaviorAuthority.Describe(
                    host.Graph,
                    OutOfProcessSidecarHostProtocol.Version,
                    1,
                    DateTimeOffset.UtcNow.AddMinutes(1))))
            .ToArray();
        var discoveries = inProcessDiscoveries
            .Select(item => item.Discovery)
            .Concat(pending.Select(item => item.Discovery.Discovery))
            .ToArray();

        foreach (var item in inProcessDiscoveries)
        {
            var hostCatalog = CreateHostCatalog(item.Discovery, discoveries);
            var authority = CompiledBehaviorAuthority.Create(
                item.Host.Graph,
                item.Discovery,
                hostCatalog);
            registrationSet._services.Add(ServiceDescriptor.Singleton<IExternalBehaviorAuthority>(authority));
        }

        for (var index = 0; index < pending.Count; index++)
        {
            var item = pending[index];
            var hostCatalog = CreateHostCatalog(item.Discovery.Discovery, discoveries);
            var client = await item.Discovery.AuthorizeAsync(hostCatalog, cancellationToken).ConfigureAwait(false);
            var proxy = new OutOfProcessRegistrationProxy(
                item.Manifest.Manifest.Id,
                item.Manifest.Manifest.DisplayName,
                item.Manifest.Manifest.ToolPrefix,
                client);
            registrationSet._services.AddRange(proxy.GetServiceDescriptors());
            registrationSet._sidecarRegistrations.Add(proxy);
            registrationSet._frontendSettings.AddRange(ReadFrontendSettings(item.Manifest));
        }
    }

    public async Task ConnectCapabilitiesAsync(
        IServiceProvider services,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        foreach (ref readonly var host in CollectionsMarshal.AsSpan(_inProcessHosts))
            host.Bind(services);
        if (_sidecarRegistrations.Count == 0)
            return;
        if (_capabilityScope is not null)
            throw new InvalidOperationException("The sidecar capability graph is already connected.");

        var scope = services.CreateAsyncScope();
        try
        {
            var storage = scope.ServiceProvider.GetRequiredService<IScopedStorageGateway>();
            var adapter = services.GetRequiredService<RuntimeKernelAdapter>();
            var dispatcher = services.GetRequiredService<IActionDispatcher>();
            var registry = services.GetRequiredService<KernelExternalAuthoritySessionRegistry>();
            if (!ReferenceEquals(dispatcher, adapter.ActionDispatcher))
                throw new InvalidOperationException("The sidecar graph did not resolve the Runtime dispatcher.");

            var actionDescriptors = CreateActionDescriptorCatalog(_sidecarRegistrations);
            var crossSidecarEntries = new OutOfProcessCrossSidecarActionEntryCatalog();
            foreach (var registration in _sidecarRegistrations.Where(item => item.Client.Application.ActionEntries.Count > 0))
                crossSidecarEntries.Add(registration.Client);

            for (var index = 0; index < _sidecarRegistrations.Count; index++)
            {
                var client = _sidecarRegistrations[index].Client;
                var snapshot = CreateActionSnapshot(client, adapter.Graph.ActionSnapshot.ContractHash);
                await client.ConnectCapabilitiesAsync(
                    new OutOfProcessCapabilityHostOptions(
                        storage,
                        dispatcher,
                        client.CreateCapabilityGrant(),
                        client.StorageContracts.Select(item => item.StorageName),
                        actionDescriptors,
                        snapshot,
                        new OutOfProcessHostActionEntryContextRegistry(),
                        registry,
                        crossSidecarEntries),
                    cancellationToken).ConfigureAwait(false);
            }

            _capabilityScope = scope;
        }
        catch
        {
            await scope.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public void Dispose()
    {
#pragma warning disable VSTHRD002 // IDisposable is a synchronous compatibility boundary; asynchronous production owners use DisposeAsync.
        DisposeAsync().AsTask().GetAwaiter().GetResult();
#pragma warning restore VSTHRD002
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeGate)
        {
            _disposeTask ??= DisposeCoreAsync();
            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        ExceptionDispatchInfo? failure = null;
        foreach (var registration in _sidecarRegistrations.AsEnumerable().Reverse())
            failure = await CaptureCleanupFailureAsync(registration, failure).ConfigureAwait(false);
        if (_capabilityScope is { } scope)
            failure = await CaptureCleanupFailureAsync(scope, failure).ConfigureAwait(false);
        foreach (var process in _sidecarProcesses.AsEnumerable().Reverse())
            failure = await CaptureCleanupFailureAsync(process, failure).ConfigureAwait(false);
        foreach (var host in _inProcessHosts.AsEnumerable().Reverse())
            failure = await CaptureCleanupFailureAsync(host, failure).ConfigureAwait(false);
        ClearRegistrations();
        failure?.Throw();
    }

    private static async ValueTask<ExceptionDispatchInfo?> CaptureCleanupFailureAsync(
        IAsyncDisposable resource, ExceptionDispatchInfo? failure)
    {
        try
        {
            await resource.DisposeAsync().ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Cleanup must settle every owned resource; callers retain and rethrow the first failure after the remaining resources settle.
        catch (Exception exception)
        {
            failure ??= ExceptionDispatchInfo.Capture(exception);
        }
#pragma warning restore CA1031
        return failure;
    }

    private void ClearRegistrations()
    {
        _services.Clear();
        _inProcessHosts.Clear();
        _frontendSettings.Clear();
        _sidecarRegistrations.Clear();
        _sidecarProcesses.Clear();
        _application = PackagedApplicationRegistry.Empty;
        _capabilityScope = null;

    }

    private static List<PackagedRegistrationManifest> EnumerateManifests(
        IReadOnlyList<string> registrationRoots)
    {
        var registrationIds = new HashSet<string>(StringComparer.Ordinal);
        var manifests = new List<PackagedRegistrationManifest>();
        foreach (var (root, path) in registrationRoots
                     .Where(value => !string.IsNullOrWhiteSpace(value))
                     .Select(Path.GetFullPath)
                     .Where(Directory.Exists)
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .SelectMany(root => Directory.EnumerateFiles(
                         root,
                         "package.json",
                         SearchOption.AllDirectories).Select(path => (root, path)))
                     .OrderBy(item => item.path, StringComparer.OrdinalIgnoreCase))
        {
            var manifest = ReadManifest(root, path);
            if (!registrationIds.Add(manifest.Id))
                throw new InvalidOperationException(
                    $"The registration id '{manifest.Id}' is declared more than once.");
            manifests.Add(manifest);
        }

        return manifests;
    }

    internal static void AddExternalContractExports(
        ICollection<ServiceDescriptor> services,
        IReadOnlyList<ExternalContractExportSource> registrations)
    {
        var bindings = services
            .Where(descriptor => descriptor.ServiceType == typeof(ServiceContractBinding))
            .Select(descriptor => descriptor.ImplementationInstance as ServiceContractBinding)
            .Where(binding => binding is not null)
            .Cast<ServiceContractBinding>()
            .ToArray();
        var localExports = bindings
            .Where(binding => binding.IsExport)
            .ToDictionary(binding => binding.ContractName, StringComparer.Ordinal);
        var externalExports = registrations
            .SelectMany(registration =>
                registration.Exports
                    .Select(export => (registration.SourceId, Export: export)))
            .ToArray();

        ValidateExternalExports(externalExports);

        foreach (var (sourceId, export) in externalExports)
        {
            if (localExports.ContainsKey(export.ContractName))
            {
                throw new InvalidOperationException(
                    $"Contract '{export.ContractName}' has both local and external providers.");
            }

            var serviceType = ResolveExportServiceType(bindings, export);
            if (serviceType is null)
                continue;

            services.Add(ServiceDescriptor.Singleton<ServiceContractBinding>(
                new ServiceContractBinding(
                    sourceId,
                    serviceType,
                    export.ContractName,
                    SchemaVersion: 1,
                    MaxBytes: 65_536,
                    IsExport: true,
                    Optional: false)));
        }
    }

    private static Type? ResolveExportServiceType(
        ServiceContractBinding[] bindings, PackageContractReference export)
    {
        var requiredTypes = bindings
            .Where(binding => !binding.IsExport && string.Equals(
                binding.ContractName,
                export.ContractName,
                StringComparison.Ordinal))
            .Select(binding => binding.ServiceType)
            .Distinct()
            .ToArray();
        if (requiredTypes.Length == 0)
            return null;
        if (requiredTypes.Length != 1)
        {
            throw new InvalidOperationException(
                $"Contract '{export.ContractName}' has incompatible local requirements.");
        }

        var serviceType = requiredTypes[0];
        if (!string.Equals(export.ServiceType, serviceType.FullName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"External contract '{export.ContractName}' does not match its local service type.");
        }
        return serviceType;
    }

    private static void ValidateExternalExports(
        (string SourceId, PackageContractReference Export)[] externalExports)
    {
        foreach (var (_, export) in externalExports)
        {
#pragma warning disable MA0015 // The exception identifies the manifest's invalid contract-name property, preserving the existing error contract.
            ArgumentException.ThrowIfNullOrWhiteSpace(export.ContractName);
#pragma warning restore MA0015
            if (string.IsNullOrWhiteSpace(export.ServiceType))
            {
                throw new InvalidOperationException(
                    $"External contract '{export.ContractName}' must declare a service type.");
            }
        }

        var duplicate = externalExports
            .GroupBy(item => item.Export.ContractName, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Skip(1).Any());
        if (duplicate is not null)
        {
            throw new InvalidOperationException(
                $"Contract '{duplicate.Key}' has more than one external provider.");
        }
    }

    internal sealed record ExternalContractExportSource(
        string SourceId,
        IReadOnlyList<PackageContractReference> Exports);

    private static void AddInProcessAuthorities(
        PackagedDotNetRegistrationSet registrationSet,
        IReadOnlyList<SidecarDiscoveryEnvelope> otherDiscoveries)
    {
        var pending = registrationSet._inProcessHosts
            .Select(host => new PendingInProcess(
                host,
                CompiledBehaviorAuthority.Describe(
                    host.Graph,
                    OutOfProcessSidecarHostProtocol.Version,
                    1,
                    DateTimeOffset.UtcNow.AddMinutes(1))))
            .ToArray();
        var discoveries = pending
            .Select(item => item.Discovery)
            .Concat(otherDiscoveries)
            .ToArray();
        foreach (var item in pending)
        {
            var authority = CompiledBehaviorAuthority.Create(
                item.Host.Graph,
                item.Discovery,
                CreateHostCatalog(item.Discovery, discoveries));
            registrationSet._services.Add(ServiceDescriptor.Singleton<IExternalBehaviorAuthority>(authority));
        }
    }

    private static SidecarHostDescriptorCatalog CreateHostCatalog(
        SidecarDiscoveryEnvelope current,
        IReadOnlyList<SidecarDiscoveryEnvelope> registrations)
    {
        var actions = KernelActionCatalog.Descriptors
            .Select(item => new SidecarHostActionDescriptor(
                item.Key,
                item.Version,
                item.Category,
                item.InputSchema,
                item.ResultSchema,
                item.Capabilities,
                item.ContainsSensitiveData,
                ContractVersionRange.Exact(1)))
            .ToDictionary(item => item.ActionKey);

        var events = KernelActionLifecycleEvents.Descriptors
            .Select(item => new SidecarHostEventDescriptor(
                item.Key,
                item.Version,
                item.Category,
                KernelSchemaIdentity.EventPayload(item, typeof(KernelActionLifecycleEvent)),
                item.Capabilities,
                item.ContainsSensitiveData,
                item.ProtocolVersionRange))
            .ToDictionary(item => item.EventKey);

        AddForeignActions(actions, current, registrations);
        AddForeignEvents(events, current, registrations);

        return new SidecarHostDescriptorCatalog(
            actions.Values.ToArray(),
            events.Values.ToArray(),
            OutOfProcessSidecarHostProtocol.Version,
            new SidecarPayloadLimits());
    }

    private static void AddForeignActions(
        Dictionary<SharpClawActionKey, SidecarHostActionDescriptor> actions,
        SidecarDiscoveryEnvelope current,
        IReadOnlyList<SidecarDiscoveryEnvelope> registrations)
    {
        var ownActionKeys = current.ActionDefinitions
            .Select(item => item.ActionKey)
            .ToHashSet();
        foreach (var group in registrations
                     .Where(item => !string.Equals(
                         item.SourceId,
                         current.SourceId,
                         StringComparison.Ordinal))
                     .SelectMany(item => item.ActionDefinitions)
                     .Where(item => !ownActionKeys.Contains(item.ActionKey))
                     .GroupBy(item => item.ActionKey))
        {
            var definition = RequireOneDefinition(
                group,
                item => SidecarCapabilityTransportCodec.Serialize(item),
                group.Key.Value);
            if (!actions.TryAdd(
                    group.Key,
                    new SidecarHostActionDescriptor(
                        definition.ActionKey,
                        definition.Version,
                        definition.Category,
                        definition.InputSchema,
                        definition.ResultSchema,
                        definition.Capabilities,
                        definition.ContainsSensitiveData,
                        definition.ProtocolVersionRange)))
            {
                throw new InvalidOperationException(
                    $"The registration action '{group.Key.Value}' conflicts with a host action.");
            }
        }

    }

    private static void AddForeignEvents(
        Dictionary<SharpClawEventKey, SidecarHostEventDescriptor> events,
        SidecarDiscoveryEnvelope current,
        IReadOnlyList<SidecarDiscoveryEnvelope> registrations)
    {
        var ownEventKeys = current.EventDefinitions
            .Select(item => item.EventKey)
            .ToHashSet();
        foreach (var group in registrations
                     .Where(item => !string.Equals(
                         item.SourceId,
                         current.SourceId,
                         StringComparison.Ordinal))
                     .SelectMany(item => item.EventDefinitions)
                     .Where(item => !ownEventKeys.Contains(item.EventKey))
                     .GroupBy(item => item.EventKey))
        {
            var definition = RequireOneDefinition(
                group,
                item => SidecarCapabilityTransportCodec.Serialize(item),
                group.Key.Value);
            if (!events.TryAdd(
                    group.Key,
                    new SidecarHostEventDescriptor(
                        definition.EventKey,
                        definition.Version,
                        definition.Category,
                        definition.PayloadSchema,
                        definition.Capabilities,
                        definition.ContainsSensitiveData,
                        definition.ProtocolVersionRange)))
            {
                throw new InvalidOperationException(
                    $"The registration event '{group.Key.Value}' conflicts with a host event.");
            }
        }

    }

    private static T RequireOneDefinition<T>(
        IEnumerable<T> definitions,
        Func<T, byte[]> serialize,
        string key)
    {
        var values = definitions.ToArray();
        var first = values[0];
        var expected = serialize(first);
        if (values.Skip(1).Any(item => !serialize(item).SequenceEqual(expected)))
        {
            throw new InvalidOperationException(
                $"The registration definition '{key}' has conflicting authorities.");
        }

        return first;
    }

    private static OutOfProcessActionDescriptorCatalog CreateActionDescriptorCatalog(
        IEnumerable<OutOfProcessRegistrationProxy> registrations)
    {
        var catalog = new OutOfProcessActionDescriptorCatalog();
        var entries = registrations
            .Select(item => item.Client)
            .SelectMany(client => client.Application.ActionEntries.Select(entry =>
            {
#pragma warning disable HLQ005 // Exact action definitions must be unique; an arbitrary first definition would conceal conflicting authority.
                var definition = client.Discovery.ActionDefinitions.SingleOrDefault(item =>
                    item.ActionKey == entry.Descriptor.Key
                    && item.Version == entry.Descriptor.Version
                    && SidecarExternalActionDispatchAuthorityValidator.DescriptorMatchesDefinition(
                        entry.Descriptor,
                        item))
                    ?? throw new InvalidOperationException(
                        $"The action entry '{entry.Descriptor.Key.Value}' has no exact discovered definition.");
#pragma warning restore HLQ005
                return (Definition: definition, entry.Descriptor);
            }))
            .ToArray();
        foreach (var group in entries.GroupBy(item =>
                     (item.Descriptor.Key, item.Descriptor.Version)))
        {
            var registration = group.First();
            if (group.Skip(1).Any(item =>
                    item.Descriptor != registration.Descriptor
                    || !SidecarCapabilityTransportCodec.Serialize(item.Definition)
                        .SequenceEqual(SidecarCapabilityTransportCodec.Serialize(
                            registration.Definition))))
            {
                throw new InvalidOperationException(
                    $"The action entry '{group.Key.Key.Value}:{group.Key.Version}' has conflicting definitions.");
            }

            catalog.Add(registration.Definition, registration.Descriptor);
        }

        return catalog;
    }

    private static ActionPipelineSnapshot CreateActionSnapshot(
        OutOfProcessRegistrationClient client,
        string graphContractHash)
    {
        var grants = client.Authorization.ActionGrants.ToList();
        foreach (var entry in client.Application.ActionEntries)
        {
#pragma warning disable HLQ005 // A grant is derived from exactly one discovered definition; duplicate definitions must fail authority construction.
            var definition = client.Discovery.ActionDefinitions.Single(item =>
                item.ActionKey == entry.Descriptor.Key
                && item.Version == entry.Descriptor.Version
                && SidecarExternalActionDispatchAuthorityValidator.DescriptorMatchesDefinition(
                    entry.Descriptor,
                    item));
#pragma warning restore HLQ005
            grants.Add(new ActionCapabilityGrant(
                definition.ActionKey,
                definition.Version,
                definition.Capabilities,
                definition.ContainsSensitiveData,
                AcceptUnknownSchemas: false));
        }

        var uniqueGrants = grants
            .GroupBy(item => (item.ActionKey, item.ActionVersion))
            .Select(group =>
            {
                var values = group.Distinct().ToArray();
                if (values.Length != 1)
                {
                    throw new InvalidOperationException(
                        $"The action grant '{group.Key.ActionKey.Value}:{group.Key.ActionVersion}' conflicts.");
                }
                return values[0];
            })
            .OrderBy(item => item.ActionKey.Value, StringComparer.Ordinal)
            .ThenBy(item => item.ActionVersion)
            .ToArray();
        return new ActionPipelineSnapshot(
            graphContractHash,
            uniqueGrants,
            client.Authorization.EventGrants);
    }

    private static PackagedRegistrationManifest ReadManifest(string root, string path)
    {
        var json = File.ReadAllText(path);
        var manifest = SecureJsonOptions.DeserializeManifest(json);
        var runtimeInfo = PackageRuntimeInfo.FromJson(json);
        using var document = JsonDocument.Parse(
            json,
            new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = false,
            });
        var enabled = document.RootElement.TryGetProperty("enabled", out _)
            ? manifest.Enabled
            : manifest.DefaultEnabled;

        return new PackagedRegistrationManifest(
            manifest.Id,
            manifest,
            runtimeInfo,
            enabled,
            root,
            path,
            json);
    }

    private static IReadOnlyList<SharpClawModuleSettingsPage> ReadFrontendSettings(PackagedRegistrationManifest manifest) =>
        SharpClawModuleSettings.ReadManifest(manifest.ManifestJson, manifest.Id, manifest.Manifest.DisplayName);

    private static bool IsEnabled(
        PackagedRegistrationManifest manifest,
        IConfiguration configuration)
    {
        var configuredValue = configuration[$"Packages:{manifest.Id}"];
        if (configuredValue is null)
            return manifest.IsEnabled;

        if (!bool.TryParse(configuredValue, out var enabled))
        {
            throw new InvalidOperationException(
                $"The registration setting 'Packages:{manifest.Id}' must be true or false.");
        }

        return enabled;
    }

    private static string ResolveContainedPath(
        string root,
        string registrationDirectory,
        string relativePath,
        string SourceId,
        string description)
    {
        var fullRoot = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(registrationDirectory, relativePath));
        if (!path.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"The in-process registration '{SourceId}' {description} is outside the registration root.");
        return path;
    }

    private sealed class PackagedSidecarProcess : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly Task<string> _stdout;
        private readonly Task<string> _stderr;
        private readonly Lock _disposeGate = new();
        private Task? _disposeTask;

        private PackagedSidecarProcess(
            Process process,
            Uri controlAddress,
            string controlToken,
            Task<string> stdout,
            Task<string> stderr)
        {
            _process = process;
            ControlAddress = controlAddress;
            ControlToken = controlToken;
            _stdout = stdout;
            _stderr = stderr;
        }

        public Uri ControlAddress { get; }

        public string ControlToken { get; }

        public static async Task<PackagedSidecarProcess> StartAsync(
            PackagedRegistrationManifest manifest,
            IConfiguration configuration,
            CancellationToken cancellationToken)
        {
            var startupTimeout = PackagedSidecarReadiness.ResolveTimeout(configuration);
            var address = new Uri($"http://127.0.0.1:{FindFreePort()}");
            var token = "sharpclaw-sidecar-" + Guid.NewGuid().ToString("N");
            var start = CreateStartInfo(manifest, configuration, address, token);
            var process = Process.Start(start)
                ?? throw new InvalidOperationException(
                    $"The sidecar process for registration '{manifest.Id}' did not start.");
            var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
            var result = new PackagedSidecarProcess(process, address, token, stdout, stderr);
            try
            {
                await result.WaitForReadinessAsync(startupTimeout, cancellationToken).ConfigureAwait(false);
                return result;
            }
            catch (Exception exception)
            {
                await result.DisposeAsync().ConfigureAwait(false);
                if (exception is OperationCanceledException && cancellationToken.IsCancellationRequested)
                    throw;
                throw new InvalidOperationException(
                    $"The sidecar process for registration '{manifest.Id}' did not become ready " +
                    $"within its {startupTimeout.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)}-second bootstrap budget. " +
                    $"stdout={await SafeOutputAsync(stdout).ConfigureAwait(false)} stderr={await SafeOutputAsync(stderr).ConfigureAwait(false)}",
                    exception);
            }
        }

        private static ProcessStartInfo CreateStartInfo(
            PackagedRegistrationManifest manifest, IConfiguration configuration, Uri address, string token)
        {
            var configuredPath = configuration["Packages:OutOfProcessSidecarHostPath"];
            var useBundledRuntime = string.IsNullOrWhiteSpace(configuredPath);
            var executablePath = ResolveExecutablePath(configuredPath);
            var registrationDirectory = Path.GetDirectoryName(manifest.ManifestPath)!;
            var start = new ProcessStartInfo
            {
                FileName = executablePath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                    ? "dotnet"
                    : executablePath,
                WorkingDirectory = AppContext.BaseDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            if (executablePath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                start.ArgumentList.Add(executablePath);
            if (useBundledRuntime)
                start.ArgumentList.Add(RuntimeLauncher.SidecarModeArgument);
            start.Environment[OutOfProcessSidecarHostProtocol.RegistrationDirectoryEnvironmentVariable] =
                registrationDirectory;
            start.Environment[OutOfProcessSidecarHostProtocol.ControlAddressEnvironmentVariable] =
                address.ToString();
            start.Environment[OutOfProcessSidecarHostProtocol.ControlTokenEnvironmentVariable] = token;
            return start;
        }

        private static string ResolveExecutablePath(string? configuredPath)
        {
            var useBundledRuntime = string.IsNullOrWhiteSpace(configuredPath);
            var executablePath = useBundledRuntime
                ? Path.Combine(
                    AppContext.BaseDirectory,
                    OperatingSystem.IsWindows()
                        ? "SharpClaw.Runtime.Host.exe"
                        : "SharpClaw.Runtime.Host")
                : Path.GetFullPath(configuredPath!);
            if (!File.Exists(executablePath))
            {
                throw new FileNotFoundException(
                    "The out-of-process registration host executable was not found.",
                    executablePath);
            }
            return executablePath;
        }

        public ValueTask DisposeAsync()
        {
            lock (_disposeGate)
            {
                _disposeTask ??= DisposeCoreAsync();
                return new ValueTask(_disposeTask);
            }
        }

        private async Task DisposeCoreAsync()
        {
            ExceptionDispatchInfo? failure = null;
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    await _process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                }
#pragma warning disable VSTHRD003 // Join both retained process pipe readers after termination; they perform context-free I/O and have no UI/JoinableTask dependency.
                await Task.WhenAll(_stdout, _stderr).ConfigureAwait(false);
#pragma warning restore VSTHRD003
            }
#pragma warning disable CA1031 // Preserve termination failures while disposing the process and observing both owned output readers; rethrow below.
            catch (Exception exception)
            {
                failure = ExceptionDispatchInfo.Capture(exception);
            }
#pragma warning restore CA1031
            finally
            {
                _process.Dispose();
            }
            try
            {
#pragma warning disable VSTHRD003 // Even after failed termination, closing the process settles these owned pipe readers; observe both before returning or rethrowing.
                await Task.WhenAll(_stdout, _stderr).ConfigureAwait(false);
#pragma warning restore VSTHRD003
            }
#pragma warning disable CA1031 // Both reader tasks must be observed; retain the earlier termination failure if present and rethrow after observation.
            catch (Exception exception)
            {
                failure ??= ExceptionDispatchInfo.Capture(exception);
            }
#pragma warning restore CA1031
            failure?.Throw();
        }

        private async Task WaitForReadinessAsync(TimeSpan startupTimeout, CancellationToken cancellationToken)
        {
            using var http = new HttpClient
            {
                BaseAddress = ControlAddress,
                Timeout = TimeSpan.FromSeconds(2),
            };
            http.DefaultRequestHeaders.Add(
                OutOfProcessSidecarHostProtocol.TokenHeaderName,
                ControlToken);
            await PackagedSidecarReadiness.WaitAsync(
                http, () => _process.HasExited, startupTimeout, cancellationToken).ConfigureAwait(false);
        }

        private static async Task<string> SafeOutputAsync(Task<string> output)
        {
            try
            {
#pragma warning disable VSTHRD003 // This context-free diagnostic helper joins the retained process reader task after process termination, not UI-owned work.
                return await output.ConfigureAwait(false);
#pragma warning restore VSTHRD003
            }
            catch (OperationCanceledException)
            {
                return string.Empty;
            }
        }

        private static int FindFreePort()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                return ((IPEndPoint)listener.LocalEndpoint).Port;
            }
            finally
            {
                listener.Stop();
            }
        }
    }

    private sealed record PackagedRegistrationManifest(
        string Id,
        PackageManifest Manifest,
        PackageRuntimeInfo RuntimeInfo,
        bool IsEnabled,
        string Root,
        string ManifestPath,
        string ManifestJson);

    private sealed record PendingSidecar(
        PackagedRegistrationManifest Manifest,
        PackagedSidecarProcess Process,
        OutOfProcessRegistrationDiscovery Discovery);

    private sealed record PendingInProcess(
        InProcessRegistrationHost Host,
        SidecarDiscoveryEnvelope Discovery);
}
