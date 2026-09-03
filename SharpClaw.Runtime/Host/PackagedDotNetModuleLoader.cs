using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.Modules;
using SharpClaw.Contracts.Persistence;
using SharpClaw.Core.Kernel;
using SharpClaw.ModuleHost.InProcess;
using SharpClaw.ModuleHost.OutOfProcess;
using SharpClaw.ModuleSDK;
using SharpClaw.Runtime.BLL.Kernel;
using SharpClaw.Runtime.BLL.Modules;

namespace SharpClaw.Runtime.Host;

/// <summary>Loads enabled .NET modules whose manifest selects in-process hosting.</summary>
internal sealed class PackagedDotNetModuleSet : IDisposable, IAsyncDisposable
{
    private readonly List<ISharpClawModule> _modules;
    private readonly IReadOnlyList<ModuleLoadContext> _loadContexts;
    private readonly List<OutOfProcessModuleProxy> _sidecarModules = [];
    private readonly List<PackagedSidecarProcess> _sidecarProcesses = [];
    private PackagedModuleApplicationRegistry _application =
        PackagedModuleApplicationRegistry.Empty;
    private AsyncServiceScope? _capabilityScope;
    private int _disposed;

    private PackagedDotNetModuleSet(
        IReadOnlyList<ISharpClawModule> modules,
        IReadOnlyList<ModuleLoadContext> loadContexts)
    {
        _modules = modules.ToList();
        _loadContexts = loadContexts;
    }

    public IReadOnlyList<ISharpClawModule> Modules => _modules;

    public PackagedModuleApplicationRegistry Application => _application;

    public static PackagedDotNetModuleSet Load(
        string modulesRoot,
        IConfiguration configuration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modulesRoot);
        return Load([modulesRoot], configuration);
    }

    internal static PackagedDotNetModuleSet Load(
        IReadOnlyList<string> moduleRoots,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(moduleRoots);
        ArgumentNullException.ThrowIfNull(configuration);

        var modules = new List<ISharpClawModule>();
        var contexts = new List<ModuleLoadContext>();

        try
        {
            foreach (var manifest in EnumerateManifests(moduleRoots))
            {
                if (!IsEnabled(manifest, configuration))
                    continue;

                if (!manifest.RuntimeInfo.IsDotNet)
                    throw new NotSupportedException(
                        $"The module '{manifest.Id}' declares unsupported runtime '{manifest.RuntimeInfo.Runtime}'. " +
                        "SharpClaw supports only .NET module runtimes.");

                if (!manifest.RuntimeInfo.IsInProcessHostMode)
                    continue;

                manifest.RuntimeInfo.EnsureDotNetEntryAssembly(manifest.Manifest);

                var moduleDirectory = Path.GetDirectoryName(manifest.ManifestPath)!;
                var entryPath = ResolveContainedPath(
                    manifest.Root,
                    moduleDirectory,
                    manifest.Manifest.EntryAssembly,
                    manifest.Id,
                    "entry assembly");
                if (!File.Exists(entryPath))
                    throw new FileNotFoundException(
                        $"The in-process module '{manifest.Id}' entry assembly was not found.",
                        entryPath);

                if (string.IsNullOrWhiteSpace(manifest.Manifest.ModuleType))
                    throw new InvalidOperationException(
                        $"The in-process module '{manifest.Id}' must declare moduleType.");

                var loadContext = new ModuleLoadContext(entryPath);
                var assembly = loadContext.LoadFromAssemblyPath(entryPath);
                var moduleType = assembly.GetType(manifest.Manifest.ModuleType, throwOnError: false);
                if (moduleType is null)
                    throw new InvalidOperationException(
                        $"The in-process module '{manifest.Id}' type '{manifest.Manifest.ModuleType}' was not found.");

                if (!typeof(ISharpClawModule).IsAssignableFrom(moduleType) ||
                    moduleType.IsAbstract ||
                    moduleType.GetConstructor(Type.EmptyTypes) is null)
                {
                    throw new InvalidOperationException(
                        $"The in-process module '{manifest.Id}' type '{manifest.Manifest.ModuleType}' " +
                        "must be a concrete ISharpClawModule with a public parameterless constructor.");
                }

                var module = Activator.CreateInstance(moduleType) as ISharpClawModule
                    ?? throw new InvalidOperationException(
                        $"The in-process module '{manifest.Id}' could not be created.");
                if (!string.Equals(module.Identity.Id, manifest.Id, StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        $"The in-process module '{manifest.Id}' identity is '{module.Identity.Id}'.");

                contexts.Add(loadContext);
                modules.Add(module);
            }

            return new PackagedDotNetModuleSet(modules, contexts);
        }
        catch
        {
            foreach (var context in contexts)
                context.Unload();
            throw;
        }
    }

    public static async Task<PackagedDotNetModuleSet> LoadProductionAsync(
        string modulesRoot,
        IConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modulesRoot);
        ArgumentNullException.ThrowIfNull(configuration);

        var moduleSet = Load(modulesRoot, configuration);
        var pending = new List<PendingSidecar>();
        try
        {
            foreach (var manifest in EnumerateManifests([modulesRoot])
                         .Where(item => IsEnabled(item, configuration))
                         .Where(item => item.RuntimeInfo.IsSidecarHostMode))
            {
                if (!manifest.RuntimeInfo.IsDotNet)
                {
                    throw new NotSupportedException(
                        $"The module '{manifest.Id}' declares unsupported runtime " +
                        $"'{manifest.RuntimeInfo.Runtime}'. SharpClaw supports only .NET module runtimes.");
                }

                manifest.RuntimeInfo.EnsureDotNetEntryAssembly(manifest.Manifest);
                var process = await PackagedSidecarProcess.StartAsync(
                    manifest,
                    configuration,
                    cancellationToken);
                try
                {
                    var discovery = await OutOfProcessModuleClient.DiscoverAsync(
                        process.ControlAddress,
                        process.ControlToken,
                        cancellationToken);
                    pending.Add(new PendingSidecar(manifest, process, discovery));
                }
                catch
                {
                    await process.DisposeAsync();
                    throw;
                }
            }

            foreach (var item in pending)
            {
                var hostCatalog = CreateHostCatalog(item, pending);
                var client = await item.Discovery.AuthorizeAsync(hostCatalog, cancellationToken);
                var identity = new ModuleIdentity(
                    item.Manifest.Manifest.Id,
                    item.Manifest.Manifest.DisplayName,
                    item.Manifest.Manifest.ToolPrefix);
                var proxy = new OutOfProcessModuleProxy(identity, client);
                moduleSet._modules.Add(proxy);
                moduleSet._sidecarModules.Add(proxy);
                moduleSet._sidecarProcesses.Add(item.Process);
            }

            moduleSet._application = new PackagedModuleApplicationRegistry(
                moduleSet._sidecarModules);

            return moduleSet;
        }
        catch
        {
            foreach (var item in pending)
            {
                await item.Discovery.DisposeAsync();
                await item.Process.DisposeAsync();
            }

            await moduleSet.DisposeAsync();
            throw;
        }
    }

    public async Task ConnectCapabilitiesAsync(
        IServiceProvider services,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (_sidecarModules.Count == 0)
            return;
        if (_capabilityScope is not null)
            throw new InvalidOperationException("The sidecar capability graph is already connected.");

        var scope = services.CreateAsyncScope();
        try
        {
            var storage = scope.ServiceProvider.GetRequiredService<IModuleStorageGateway>();
            var adapter = services.GetRequiredService<RuntimeKernelAdapter>();
            var dispatcher = services.GetRequiredService<IActionDispatcher>();
            var registry = services.GetRequiredService<KernelExternalAuthoritySessionRegistry>();
            if (!ReferenceEquals(dispatcher, adapter.ActionDispatcher))
                throw new InvalidOperationException("The sidecar graph did not resolve the Runtime dispatcher.");

            var actionDescriptors = CreateActionDescriptorCatalog(_sidecarModules);
            var crossSidecarEntries = new OutOfProcessCrossSidecarActionEntryCatalog();
            foreach (var module in _sidecarModules.Where(item => item.Client.Application.ActionEntries.Count > 0))
                crossSidecarEntries.Add(module.Client);

            foreach (var module in _sidecarModules)
            {
                var client = module.Client;
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
                    cancellationToken);
            }

            _capabilityScope = scope;
        }
        catch
        {
            await scope.DisposeAsync();
            throw;
        }
    }

    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        Exception? failure = null;
        foreach (var module in _sidecarModules.AsEnumerable().Reverse())
        {
            try
            {
                await module.DisposeAsync();
            }
            catch (Exception exception)
            {
                failure ??= exception;
            }
        }

        if (_capabilityScope is { } scope)
        {
            try
            {
                await scope.DisposeAsync();
            }
            catch (Exception exception)
            {
                failure ??= exception;
            }
        }

        foreach (var process in _sidecarProcesses.AsEnumerable().Reverse())
        {
            try
            {
                await process.DisposeAsync();
            }
            catch (Exception exception)
            {
                failure ??= exception;
            }
        }

        foreach (var context in _loadContexts)
            context.Unload();

        if (failure is not null)
            throw failure;
    }

    private static IReadOnlyList<PackagedModuleManifest> EnumerateManifests(
        IReadOnlyList<string> moduleRoots)
    {
        var moduleIds = new HashSet<string>(StringComparer.Ordinal);
        var manifests = new List<PackagedModuleManifest>();
        foreach (var (root, path) in moduleRoots
                     .Where(value => !string.IsNullOrWhiteSpace(value))
                     .Select(Path.GetFullPath)
                     .Where(Directory.Exists)
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .SelectMany(root => Directory.EnumerateFiles(
                         root,
                         "module.json",
                         SearchOption.AllDirectories).Select(path => (root, path)))
                     .OrderBy(item => item.path, StringComparer.OrdinalIgnoreCase))
        {
            var manifest = ReadManifest(root, path);
            if (!moduleIds.Add(manifest.Id))
                throw new InvalidOperationException(
                    $"The module id '{manifest.Id}' is declared more than once.");
            manifests.Add(manifest);
        }

        return manifests;
    }

    private static SidecarHostDescriptorCatalog CreateHostCatalog(
        PendingSidecar current,
        IReadOnlyList<PendingSidecar> modules)
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

        var ownActionKeys = current.Discovery.Discovery.ActionDefinitions
            .Select(item => item.ActionKey)
            .ToHashSet();
        foreach (var group in modules
                     .Where(item => !ReferenceEquals(item, current))
                     .SelectMany(item => item.Discovery.Discovery.ActionDefinitions)
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
                    $"The module action '{group.Key.Value}' conflicts with a host action.");
            }
        }

        var ownEventKeys = current.Discovery.Discovery.EventDefinitions
            .Select(item => item.EventKey)
            .ToHashSet();
        foreach (var group in modules
                     .Where(item => !ReferenceEquals(item, current))
                     .SelectMany(item => item.Discovery.Discovery.EventDefinitions)
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
                    $"The module event '{group.Key.Value}' conflicts with a host event.");
            }
        }

        return new SidecarHostDescriptorCatalog(
            actions.Values.ToArray(),
            events.Values.ToArray(),
            OutOfProcessModuleHostProtocol.Version,
            new SidecarPayloadLimits());
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
                $"The module definition '{key}' has conflicting authorities.");
        }

        return first;
    }

    private static OutOfProcessActionDescriptorCatalog CreateActionDescriptorCatalog(
        IEnumerable<OutOfProcessModuleProxy> modules)
    {
        var catalog = new OutOfProcessActionDescriptorCatalog();
        var registrations = modules
            .Select(item => item.Client)
            .SelectMany(client => client.Application.ActionEntries.Select(entry =>
            {
                var definition = client.Discovery.ActionDefinitions.SingleOrDefault(item =>
                    item.ActionKey == entry.Descriptor.Key
                    && item.Version == entry.Descriptor.Version
                    && SidecarExternalActionDispatchAuthorityValidator.DescriptorMatchesDefinition(
                        entry.Descriptor,
                        item))
                    ?? throw new InvalidOperationException(
                        $"The action entry '{entry.Descriptor.Key.Value}' has no exact discovered definition.");
                return (Definition: definition, entry.Descriptor);
            }))
            .ToArray();
        foreach (var group in registrations.GroupBy(item =>
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
        OutOfProcessModuleClient client,
        string graphContractHash)
    {
        var grants = client.Authorization.ActionGrants.ToList();
        foreach (var entry in client.Application.ActionEntries)
        {
            var definition = client.Discovery.ActionDefinitions.Single(item =>
                item.ActionKey == entry.Descriptor.Key
                && item.Version == entry.Descriptor.Version
                && SidecarExternalActionDispatchAuthorityValidator.DescriptorMatchesDefinition(
                    entry.Descriptor,
                    item));
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

    private static PackagedModuleManifest ReadManifest(string root, string path)
    {
        var json = File.ReadAllText(path);
        var manifest = SecureJsonOptions.DeserializeManifest(json);
        var runtimeInfo = ModuleManifestRuntimeInfo.FromJson(json);
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

        return new PackagedModuleManifest(
            manifest.Id,
            manifest,
            runtimeInfo,
            enabled,
            root,
            path);
    }

    private static bool IsEnabled(
        PackagedModuleManifest manifest,
        IConfiguration configuration)
    {
        var configuredValue = configuration[$"Modules:{manifest.Id}"];
        if (configuredValue is null)
            return manifest.IsEnabled;

        if (!bool.TryParse(configuredValue, out var enabled))
        {
            throw new InvalidOperationException(
                $"The module setting 'Modules:{manifest.Id}' must be true or false.");
        }

        return enabled;
    }

    private static string ResolveContainedPath(
        string root,
        string moduleDirectory,
        string relativePath,
        string moduleId,
        string description)
    {
        var fullRoot = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(moduleDirectory, relativePath));
        if (!path.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"The in-process module '{moduleId}' {description} is outside the module root.");
        return path;
    }

    private sealed class PackagedSidecarProcess : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly Task<string> _stdout;
        private readonly Task<string> _stderr;
        private int _disposed;

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
            PackagedModuleManifest manifest,
            IConfiguration configuration,
            CancellationToken cancellationToken)
        {
            var configuredPath = configuration["Modules:OutOfProcessModuleHostPath"];
            var executablePath = string.IsNullOrWhiteSpace(configuredPath)
                ? Path.Combine(
                    AppContext.BaseDirectory,
                    OperatingSystem.IsWindows()
                        ? "SharpClaw.ModuleHost.OutOfProcess.exe"
                        : "SharpClaw.ModuleHost.OutOfProcess")
                : Path.GetFullPath(configuredPath);
            if (!File.Exists(executablePath))
            {
                throw new FileNotFoundException(
                    "The out-of-process module host executable was not found.",
                    executablePath);
            }

            var moduleDirectory = Path.GetDirectoryName(manifest.ManifestPath)!;
            var address = new Uri($"http://127.0.0.1:{FindFreePort()}");
            var token = "sharpclaw-sidecar-" + Guid.NewGuid().ToString("N");
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
            start.Environment[OutOfProcessModuleHostProtocol.ModuleDirectoryEnvironmentVariable] =
                moduleDirectory;
            start.Environment[OutOfProcessModuleHostProtocol.ControlAddressEnvironmentVariable] =
                address.ToString();
            start.Environment[OutOfProcessModuleHostProtocol.ControlTokenEnvironmentVariable] = token;

            var process = Process.Start(start)
                ?? throw new InvalidOperationException(
                    $"The sidecar process for module '{manifest.Id}' did not start.");
            var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
            var result = new PackagedSidecarProcess(process, address, token, stdout, stderr);
            try
            {
                await result.WaitForReadinessAsync(cancellationToken);
                return result;
            }
            catch
            {
                await result.DisposeAsync();
                throw new InvalidOperationException(
                    $"The sidecar process for module '{manifest.Id}' did not become ready. " +
                    $"stdout={await SafeOutputAsync(stdout)} stderr={await SafeOutputAsync(stderr)}");
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    await _process.WaitForExitAsync(timeout.Token);
                }
            }
            finally
            {
                _process.Dispose();
            }
        }

        private async Task WaitForReadinessAsync(CancellationToken cancellationToken)
        {
            using var http = new HttpClient
            {
                BaseAddress = ControlAddress,
                Timeout = TimeSpan.FromSeconds(2),
            };
            http.DefaultRequestHeaders.Add(
                OutOfProcessModuleHostProtocol.TokenHeaderName,
                ControlToken);
            var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
            while (DateTimeOffset.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_process.HasExited)
                    throw new InvalidOperationException("The sidecar exited before readiness.");
                try
                {
                    using var response = await http.GetAsync(
                        OutOfProcessModuleHostProtocol.ReadinessPath,
                        cancellationToken);
                    if (response.StatusCode == HttpStatusCode.OK)
                        return;
                }
                catch (HttpRequestException)
                {
                }
                catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                }

                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
            }

            throw new TimeoutException("The sidecar readiness boundary timed out.");
        }

        private static async Task<string> SafeOutputAsync(Task<string> output)
        {
            try
            {
                return await output;
            }
            catch (OperationCanceledException)
            {
                return string.Empty;
            }
        }

        private static int FindFreePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
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

    private sealed record PackagedModuleManifest(
        string Id,
        ModuleManifest Manifest,
        ModuleManifestRuntimeInfo RuntimeInfo,
        bool IsEnabled,
        string Root,
        string ManifestPath);

    private sealed record PendingSidecar(
        PackagedModuleManifest Manifest,
        PackagedSidecarProcess Process,
        OutOfProcessModuleDiscovery Discovery);
}
