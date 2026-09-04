using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
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

namespace SharpClaw.Runtime.Host;

/// <summary>Loads enabled .NET registrations whose manifest selects in-process hosting.</summary>
internal sealed class PackagedDotNetRegistrationSet : IDisposable, IAsyncDisposable
{
    private readonly List<ServiceDescriptor> _services;
    private readonly List<InProcessRegistrationHost> _inProcessHosts;
    private readonly List<OutOfProcessRegistrationProxy> _sidecarRegistrations = [];
    private readonly List<PackagedSidecarProcess> _sidecarProcesses = [];
    private PackagedApplicationRegistry _application =
        PackagedApplicationRegistry.Empty;
    private AsyncServiceScope? _capabilityScope;
    private int _disposed;

    private PackagedDotNetRegistrationSet(
        IReadOnlyList<ServiceDescriptor> services,
        IReadOnlyList<InProcessRegistrationHost> inProcessHosts)
    {
        _services = services.ToList();
        _inProcessHosts = inProcessHosts.ToList();
    }

    public IReadOnlyList<ServiceDescriptor> Services => _services;

    public IReadOnlyList<string> SourceIds =>
        _inProcessHosts.Select(host => host.Manifest.Id)
            .Concat(_sidecarRegistrations.Select(registration => registration.SourceId))
            .ToArray();

    internal IReadOnlyList<OutOfProcessRegistrationProxy> Sidecars => _sidecarRegistrations;

    public PackagedApplicationRegistry Application => _application;

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
        ArgumentNullException.ThrowIfNull(registrationRoots);
        ArgumentNullException.ThrowIfNull(configuration);

        var services = new List<ServiceDescriptor>();
        var inProcessHosts = new List<InProcessRegistrationHost>();

        try
        {
            foreach (var manifest in EnumerateManifests(registrationRoots))
            {
                if (!IsEnabled(manifest, configuration))
                    continue;

                if (!manifest.RuntimeInfo.IsDotNet)
                    throw new NotSupportedException(
                        $"The registration '{manifest.Id}' declares unsupported runtime '{manifest.RuntimeInfo.Runtime}'. " +
                        "SharpClaw supports only .NET registration runtimes.");

                if (!manifest.RuntimeInfo.IsInProcessHostMode)
                    continue;

                var registrationDirectory = Path.GetDirectoryName(manifest.ManifestPath)!;
                var host = InProcessRegistrationHost.LoadAsync(
                        registrationDirectory,
                        CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();
                inProcessHosts.Add(host);
                services.AddRange(host.ServiceDescriptors);
            }

            return new PackagedDotNetRegistrationSet(services, inProcessHosts);
        }
        catch
        {
            foreach (var host in inProcessHosts)
                host.DisposeAsync().AsTask().GetAwaiter().GetResult();
            throw;
        }
    }

    public static async Task<PackagedDotNetRegistrationSet> LoadProductionAsync(
        string registrationsRoot,
        IConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(registrationsRoot);
        ArgumentNullException.ThrowIfNull(configuration);

        var registrationSet = Load(registrationsRoot, configuration);
        var pending = new List<PendingSidecar>();
        try
        {
            foreach (var manifest in EnumerateManifests([registrationsRoot])
                         .Where(item => IsEnabled(item, configuration))
                         .Where(item => item.RuntimeInfo.IsSidecarHostMode))
            {
                if (!manifest.RuntimeInfo.IsDotNet)
                {
                    throw new NotSupportedException(
                        $"The registration '{manifest.Id}' declares unsupported runtime " +
                        $"'{manifest.RuntimeInfo.Runtime}'. SharpClaw supports only .NET registration runtimes.");
                }

                manifest.RuntimeInfo.EnsureDotNetEntryAssembly(manifest.Manifest);
                var process = await PackagedSidecarProcess.StartAsync(
                    manifest,
                    configuration,
                    cancellationToken);
                try
                {
                    var discovery = await OutOfProcessRegistrationClient.DiscoverAsync(
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
                var proxy = new OutOfProcessRegistrationProxy(
                    item.Manifest.Manifest.Id,
                    item.Manifest.Manifest.DisplayName,
                    item.Manifest.Manifest.ToolPrefix,
                    client);
                registrationSet._services.AddRange(proxy.GetServiceDescriptors());
                registrationSet._sidecarRegistrations.Add(proxy);
                registrationSet._sidecarProcesses.Add(item.Process);
            }

            registrationSet._application = new PackagedApplicationRegistry(
                registrationSet._sidecarRegistrations);

            return registrationSet;
        }
        catch
        {
            foreach (var item in pending)
            {
                await item.Discovery.DisposeAsync();
                await item.Process.DisposeAsync();
            }

            await registrationSet.DisposeAsync();
            throw;
        }
    }

    public async Task ConnectCapabilitiesAsync(
        IServiceProvider services,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        foreach (var host in _inProcessHosts)
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

            foreach (var registration in _sidecarRegistrations)
            {
                var client = registration.Client;
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
        foreach (var registration in _sidecarRegistrations.AsEnumerable().Reverse())
        {
            try
            {
                await registration.DisposeAsync();
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

        foreach (var host in _inProcessHosts.AsEnumerable().Reverse())
        {
            try
            {
                await host.DisposeAsync();
            }
            catch (Exception exception)
            {
                failure ??= exception;
            }
        }

        if (failure is not null)
            throw failure;
    }

    private static IReadOnlyList<PackagedRegistrationManifest> EnumerateManifests(
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

    private static SidecarHostDescriptorCatalog CreateHostCatalog(
        PendingSidecar current,
        IReadOnlyList<PendingSidecar> registrations)
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
        foreach (var group in registrations
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
                    $"The registration action '{group.Key.Value}' conflicts with a host action.");
            }
        }

        var ownEventKeys = current.Discovery.Discovery.EventDefinitions
            .Select(item => item.EventKey)
            .ToHashSet();
        foreach (var group in registrations
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
                    $"The registration event '{group.Key.Value}' conflicts with a host event.");
            }
        }

        return new SidecarHostDescriptorCatalog(
            actions.Values.ToArray(),
            events.Values.ToArray(),
            OutOfProcessSidecarHostProtocol.Version,
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
            path);
    }

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
            PackagedRegistrationManifest manifest,
            IConfiguration configuration,
            CancellationToken cancellationToken)
        {
            var configuredPath = configuration["Packages:OutOfProcessSidecarHostPath"];
            var executablePath = string.IsNullOrWhiteSpace(configuredPath)
                ? Path.Combine(
                    AppContext.BaseDirectory,
                    OperatingSystem.IsWindows()
                        ? "SharpClaw.SidecarHost.OutOfProcess.exe"
                        : "SharpClaw.SidecarHost.OutOfProcess")
                : Path.GetFullPath(configuredPath);
            if (!File.Exists(executablePath))
            {
                throw new FileNotFoundException(
                    "The out-of-process registration host executable was not found.",
                    executablePath);
            }

            var registrationDirectory = Path.GetDirectoryName(manifest.ManifestPath)!;
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
            start.Environment[OutOfProcessSidecarHostProtocol.RegistrationDirectoryEnvironmentVariable] =
                registrationDirectory;
            start.Environment[OutOfProcessSidecarHostProtocol.ControlAddressEnvironmentVariable] =
                address.ToString();
            start.Environment[OutOfProcessSidecarHostProtocol.ControlTokenEnvironmentVariable] = token;

            var process = Process.Start(start)
                ?? throw new InvalidOperationException(
                    $"The sidecar process for registration '{manifest.Id}' did not start.");
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
                    $"The sidecar process for registration '{manifest.Id}' did not become ready. " +
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
                OutOfProcessSidecarHostProtocol.TokenHeaderName,
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
                        OutOfProcessSidecarHostProtocol.ReadinessPath,
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

    private sealed record PackagedRegistrationManifest(
        string Id,
        PackageManifest Manifest,
        PackageRuntimeInfo RuntimeInfo,
        bool IsEnabled,
        string Root,
        string ManifestPath);

    private sealed record PendingSidecar(
        PackagedRegistrationManifest Manifest,
        PackagedSidecarProcess Process,
        OutOfProcessRegistrationDiscovery Discovery);
}
