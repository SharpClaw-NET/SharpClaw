using System.Reflection;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Runtime.Host;
using SharpClaw.Core.Clients;
using SharpClaw.Runtime.BLL.Modules;
using SharpClaw.Runtime.BLL.Modules.Foreign;
using SharpClaw.Runtime.BLL.Kernel;
using SharpClaw.Runtime.BLL.Services;
using SharpClaw.Contracts.Kernel;
using SharpClaw.Contracts.Persistence;
using SharpClaw.Runtime.INF.Persistence;
using SharpClaw.Tests.Kernel;
using SharpClaw.Runtime.INF.Persistence.Modules;
using SharpClaw.TestFixtures.ExternalRegistration;
using SharpClaw.Tests.TestHarness;
using SharpClaw.Shared.Instances;
using SharpClaw.Core.Modules;
using SharpClaw.Runtime.INF.Configuration;
using Supprocom.Secrets;

namespace SharpClaw.Tests.Modules;

[TestFixture]
public sealed class BundledDotNetSidecarDefaultTests
{
    [Test]
    public void DiscoverBundledUsesManifestOnlyEntriesForSidecarHostModeByDefault()
    {
        var loader = ModuleLoader.DiscoverBundled();

        loader.IsManifestOnlyBundledRegistration(TestHarnessConstants.OutOfProcessRegistrationId).Should().BeTrue();
        loader.GetBundledRegistration(TestHarnessConstants.OutOfProcessRegistrationId)
            .Should()
            .NotBeNull();
        loader.IsManifestOnlyBundledRegistration("sharpclaw_agent_orchestration")
            .Should()
            .BeTrue("agent orchestration no longer has sidecar readiness blockers");
    }

    [Test]
    public void DiscoverBundledIgnoresLegacyForceInProcessSetting()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Packages:ForceInProcessDotNetSidecars"] = "true",
            })
            .Build();

        var loader = ModuleLoader.DiscoverBundled(configuration);

        loader.IsManifestOnlyBundledRegistration(TestHarnessConstants.OutOfProcessRegistrationId).Should().BeTrue();
        loader.GetBundledRegistration(TestHarnessConstants.OutOfProcessRegistrationId)
            .Should()
            .NotBeNull();
    }

    [Test]
    public void InProcessDotNetHostingModeIsAcceptedByDiscovery()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [DotNetRegistrationHostingModeOptions.ConfigKey] = "in-process",
            })
            .Build();

        var loader = ModuleLoader.DiscoverBundled(configuration);

        loader.IsManifestOnlyBundledRegistration(TestHarnessConstants.OutOfProcessRegistrationId).Should().BeTrue();
        loader.GetBundledRegistration(TestHarnessConstants.OutOfProcessRegistrationId)
            .Should()
            .NotBeNull(
                "discovery remains manifest-only; enabling the module chooses the runtime host");
    }

    [Test]
    public async Task ManifestOnlyBundledSidecarReportsRuntimeDetailsAfterEnable()
    {
        var loader = ModuleLoader.DiscoverBundled();
        loader.IsManifestOnlyBundledRegistration(TestHarnessConstants.OutOfProcessRegistrationId).Should().BeTrue();
        await using var harness = RegistrationServiceHarness.Create(registrationLoader: loader);

        var response = await harness.RegistrationService.EnableAsync(
            TestHarnessConstants.OutOfProcessRegistrationId,
            harness.RootServices,
            CancellationToken.None);

        response.Enabled.Should().BeTrue();
        harness.Registry.GetRuntimeHost(TestHarnessConstants.OutOfProcessRegistrationId)
            .Should()
            .BeAssignableTo<IForeignRegistrationRuntimeHost>();

        var detail = await harness.RegistrationService.GetDetailAsync(
            TestHarnessConstants.OutOfProcessRegistrationId,
            CancellationToken.None);

        detail.Should().NotBeNull();
        detail!.ToolCount.Should().BeGreaterThan(0);
        detail.DisplayName.Should().Be("Test Harness Out Of Process");
    }

    [Test]
    public async Task BundledRegistrationWithSidecarHostModeRegistersThroughForeignRuntimeHost()
    {
        await using var harness = RegistrationServiceHarness.Create();

        var response = await harness.RegistrationService.EnableAsync(
            TestHarnessConstants.OutOfProcessRegistrationId,
            harness.RootServices,
            CancellationToken.None);

        response.Enabled.Should().BeTrue();
        var runtimeHost = harness.Registry.GetRuntimeHost(TestHarnessConstants.OutOfProcessRegistrationId);
        runtimeHost.Should().BeAssignableTo<IForeignRegistrationRuntimeHost>();
        harness.Registry.IsExternal(TestHarnessConstants.OutOfProcessRegistrationId)
            .Should()
            .BeFalse("bundled sidecars have runtime hosts without becoming user-loaded external modules");

        var module = harness.Registry.GetRegistration(TestHarnessConstants.OutOfProcessRegistrationId);
        module.Should().NotBeNull();
        module.Should().NotBeNull();

        using var parameters = JsonDocument.Parse("""{"result":"default sidecar"}""");
        var result = await module!.ExecuteToolAsync(
            TestHarnessConstants.JobPermissionedTool,
            parameters.RootElement,
            new AgentJobContext(
                Guid.NewGuid(),
                Guid.NewGuid(),
                Guid.NewGuid(),
                ResourceId: null,
                ActionKey: TestHarnessConstants.JobPermissionedTool),
            runtimeHost!.Services,
            CancellationToken.None);

        result.Should().Be("default sidecar");

        await harness.RegistrationService.DisableAsync(TestHarnessConstants.OutOfProcessRegistrationId, CancellationToken.None);
        harness.Registry.GetRuntimeHost(TestHarnessConstants.OutOfProcessRegistrationId).Should().BeNull();
        harness.Registry.GetRegistration(TestHarnessConstants.OutOfProcessRegistrationId).Should().BeNull();
    }

    [Test]
    public async Task SidecarOnlyModeKeepsReadinessCleanBundledRegistrationsOutOfProcess()
    {
        await using var harness = RegistrationServiceHarness.Create(new Dictionary<string, string?>
        {
            [DotNetRegistrationHostingModeOptions.ConfigKey] = "sidecar-only",
        });

        var response = await harness.RegistrationService.EnableAsync(
            TestHarnessConstants.OutOfProcessRegistrationId,
            harness.RootServices,
            CancellationToken.None);

        response.Enabled.Should().BeTrue();
        harness.Registry.GetRuntimeHost(TestHarnessConstants.OutOfProcessRegistrationId)
            .Should()
            .BeAssignableTo<IForeignRegistrationRuntimeHost>();
        harness.Registry.GetRegistration(TestHarnessConstants.OutOfProcessRegistrationId)
            .Should()
            .NotBeNull();
    }

    [Test]
    public async Task InProcessModeKeepsExplicitBundledSidecarManifestOutOfProcess()
    {
        var settings = new Dictionary<string, string?>
        {
            [DotNetRegistrationHostingModeOptions.ConfigKey] = "in-process",
        };
        var configuration = BuildConfiguration(settings);
        await using var harness = RegistrationServiceHarness.Create(
            settings,
            registrationLoader: ModuleLoader.DiscoverBundled(configuration));

        var response = await harness.RegistrationService.EnableAsync(
            TestHarnessConstants.OutOfProcessRegistrationId,
            harness.RootServices,
            CancellationToken.None);

        response.Enabled.Should().BeTrue();
        var runtimeHost = harness.Registry.GetRuntimeHost(TestHarnessConstants.OutOfProcessRegistrationId)
            .Should()
            .BeAssignableTo<IForeignRegistrationRuntimeHost>()
            .Subject;
        var module = harness.Registry.GetRegistration(TestHarnessConstants.OutOfProcessRegistrationId);
        module.Should().NotBeNull();
        module!.Id.Should().Be(TestHarnessConstants.OutOfProcessRegistrationId);
        module.Should().NotBeNull();
        harness.Registry.IsExternal(TestHarnessConstants.OutOfProcessRegistrationId).Should().BeFalse();

        using var parameters = JsonDocument.Parse("""{"result":"in-process tool"}""");
        var result = await module!.ExecuteToolAsync(
            TestHarnessConstants.JobPermissionedTool,
            parameters.RootElement,
            new AgentJobContext(
                Guid.NewGuid(),
                Guid.NewGuid(),
                Guid.NewGuid(),
                ResourceId: null,
                ActionKey: TestHarnessConstants.JobPermissionedTool),
            runtimeHost.Services,
            CancellationToken.None);

        result.Should().Be("in-process tool");
    }

    [Test]
    public async Task InProcessTestHarnessLoadsThroughPayloadRegistrationDirectory()
    {
        await using var harness = RegistrationServiceHarness.Create(new Dictionary<string, string?>
        {
            [DotNetRegistrationHostingModeOptions.ConfigKey] = "in-process",
        });
        var registrationDir = Path.Combine(
            TestContext.CurrentContext.TestDirectory,
            "test-contributions",
            TestHarnessConstants.InProcessRegistrationId);

        Directory.Exists(registrationDir).Should().BeTrue(
            "the in-process TestHarness payload must be copied for module-boundary tests");

        var response = await harness.RegistrationService.LoadExternalFromAbsolutePathAsync(
            registrationDir,
            harness.RootServices,
            CancellationToken.None,
            persistDisabledEnvEntry: false);

        response.Enabled.Should().BeTrue();
        var runtimeHost = harness.Registry.GetRuntimeHost(TestHarnessConstants.InProcessRegistrationId)
            .Should()
            .BeOfType<InProcessRegistrationHost>()
            .Subject;
        harness.Registry.GetRegistration(TestHarnessConstants.InProcessRegistrationId)
            .Should()
            .NotBeNull();

        using var scope = runtimeHost.CreateScope();
        var gateway = scope.ServiceProvider.GetRequiredService<IScopedStorageGateway>();
        gateway.ListContracts().Should().OnlyContain(contract =>
            string.Equals(contract.SourceId, TestHarnessConstants.InProcessRegistrationId, StringComparison.Ordinal));
    }

    [Test]
    public async Task InProcessStorageGatewayRejectsOtherScopedStorageRequests()
    {
        await using var harness = RegistrationServiceHarness.Create(new Dictionary<string, string?>
        {
            [DotNetRegistrationHostingModeOptions.ConfigKey] = "in-process",
        });
        var registrationDir = CreateExternalRegistrationDirectory(
            typeof(InProcessStorageFixtureRegistration),
            InProcessStorageFixtureRegistration.SourceId,
            "Synthetic In-Process Storage",
            InProcessStorageFixtureRegistration.ToolPrefixValue);

        var response = await harness.RegistrationService.LoadExternalFromAbsolutePathAsync(
            registrationDir,
            harness.RootServices,
            CancellationToken.None,
            persistDisabledEnvEntry: false);

        response.Enabled.Should().BeTrue();
        var runtimeHost = harness.Registry.GetRuntimeHost(InProcessStorageFixtureRegistration.SourceId)
            .Should()
            .BeOfType<InProcessRegistrationHost>()
            .Subject;

        using var scope = runtimeHost.CreateScope();
        var gateway = scope.ServiceProvider.GetRequiredService<IScopedStorageGateway>();
        gateway.ListContracts().Should().NotBeEmpty();
        gateway.ListContracts().Should().OnlyContain(contract =>
            string.Equals(contract.SourceId, InProcessStorageFixtureRegistration.SourceId, StringComparison.Ordinal));
        scope.ServiceProvider.GetServices<IScopedStorageGateway>().Should().ContainSingle(
            "module-owned fake gateway registrations are replaced by the host-owned wrapper");
        scope.ServiceProvider.GetService<SharpClawDbContext>().Should().BeNull(
            "in-process modules must not receive the raw host DbContext");

        var restricted = RegistrationHostServiceAccess.CreateRestrictedScope(
            scope.ServiceProvider,
            InProcessStorageFixtureRegistration.SourceId);
        var blockedRawDb = () => restricted.GetRequiredService<SharpClawDbContext>();
        blockedRawDb.Should().Throw<InvalidOperationException>()
            .WithMessage("*blocked service*SharpClawDbContext*");

        using var parameters = JsonDocument.Parse("{}");
        var act = async () => await gateway.InvokeAsync(
            TestHarnessConstants.OutOfProcessRegistrationId,
            InProcessStorageFixtureRegistration.StorageName,
            ScopedStorageOperations.List,
            parameters.RootElement,
            CancellationToken.None);

        await act.Should().ThrowAsync<UnauthorizedAccessException>()
            .WithMessage("*cannot access storage owned by module*");
    }

    [Test]
    public async Task SidecarManifestBundledRegistrationsRegisterThroughForeignRuntimeHost()
    {
        var loader = ModuleLoader.DiscoverBundled();
        var bundledRegistrations = loader.GetAllBundled()
            .Where(module => loader.IsManifestOnlyBundledRegistration(module.Id))
            .OrderBy(module => module.Id, StringComparer.Ordinal)
            .ToArray();
        bundledRegistrations.Select(module => module.Id).Should().Equal(
        [
            "sharpclaw_agent_orchestration",
            "sharpclaw_editor_common",
            "sharpclaw_metrics",
            "sharpclaw_registration_dev",
            "sharpclaw_providers_anthropic",
            "sharpclaw_providers_google",
            "sharpclaw_providers_llamasharp",
            "sharpclaw_providers_ollama",
            "sharpclaw_providers_openai_compat",
            TestHarnessConstants.OutOfProcessRegistrationId,
            "sharpclaw_vs2026_editor",
            "sharpclaw_vscode_editor",
        ]);
        await using var harness = RegistrationServiceHarness.Create(registrationLoader: loader);
        var enabledRegistrationIds = new List<string>();

        try
        {
            foreach (var bundledRegistration in bundledRegistrations)
            {
                var response = await harness.RegistrationService.EnableAsync(
                    bundledRegistration.Id,
                    harness.RootServices,
                    CancellationToken.None);

                enabledRegistrationIds.Add(bundledRegistration.Id);
                response.Enabled.Should().BeTrue();
                harness.Registry.GetRuntimeHost(bundledRegistration.Id)
                    .Should()
                    .BeAssignableTo<IForeignRegistrationRuntimeHost>(
                        $"module '{bundledRegistration.Id}' declares sidecar host mode");
                harness.Registry.GetRegistration(bundledRegistration.Id)
                    .Should()
                    .NotBeSameAs(bundledRegistration);
            }
        }
        finally
        {
            foreach (var SourceId in enabledRegistrationIds.AsEnumerable().Reverse())
                await harness.RegistrationService.DisableAsync(SourceId, CancellationToken.None);
        }
    }

    [Test]
    public async Task SidecarProviderPluginsAreVisibleThroughParentFactoryOnlyWhileEnabled()
    {
        await using var harness = RegistrationServiceHarness.Create();

        var response = await harness.RegistrationService.EnableAsync(
            "sharpclaw_providers_openai_compat",
            harness.RootServices,
            CancellationToken.None);

        response.Enabled.Should().BeTrue();
        harness.Registry.GetRuntimeHost("sharpclaw_providers_openai_compat")
            .Should()
            .BeAssignableTo<IForeignRegistrationRuntimeHost>();

        var factory = harness.Root.GetRequiredService<ProviderApiClientFactory>();
        factory.IsAvailable("openai").Should().BeTrue();
        factory.GetPlugin("openai")!.OwnerId.Should().Be("sharpclaw_providers_openai_compat");
        factory.GetPlugin("custom")!.RequiresEndpoint.Should().BeTrue();

        await harness.RegistrationService.DisableAsync(
            "sharpclaw_providers_openai_compat",
            CancellationToken.None);

        factory.IsAvailable("openai").Should().BeFalse();
    }

    [Test]
    public async Task EditorCommonSidecarAdvertisesEditorWebSocketEndpoint()
    {
        await using var harness = RegistrationServiceHarness.Create();

        var response = await harness.RegistrationService.EnableAsync(
            "sharpclaw_editor_common",
            harness.RootServices,
            CancellationToken.None);

        response.Enabled.Should().BeTrue();
        var runtimeHost = harness.Registry.GetRuntimeHost("sharpclaw_editor_common")
            .Should()
            .BeAssignableTo<IForeignRegistrationRuntimeHost>()
            .Subject;

        runtimeHost.Endpoints.Should().Contain(endpoint =>
            string.Equals(endpoint.Method, "GET", StringComparison.Ordinal)
            && string.Equals(endpoint.RoutePattern, "/editor/ws", StringComparison.Ordinal)
            && string.Equals(
                endpoint.ResponseMode,
                ForeignEndpointResponseMode.WebSocket,
                StringComparison.Ordinal));
    }

    [Test]
    public async Task SidecarOnlyModeRunsAgentOrchestrationOutOfProcess()
    {
        var settings = new Dictionary<string, string?>
        {
            [DotNetRegistrationHostingModeOptions.ConfigKey] = "sidecar-only",
        };
        var configuration = BuildConfiguration(settings);
        await using var harness = RegistrationServiceHarness.Create(
            settings,
            registrationLoader: ModuleLoader.DiscoverBundled(configuration));

        var response = await harness.RegistrationService.EnableAsync(
            "sharpclaw_agent_orchestration",
            harness.RootServices,
            CancellationToken.None);

        response.Enabled.Should().BeTrue();
        harness.Registry.GetRuntimeHost("sharpclaw_agent_orchestration")
            .Should()
            .BeAssignableTo<IForeignRegistrationRuntimeHost>();
        harness.Registry.GetRegistration("sharpclaw_agent_orchestration")
            .Should()
            .NotBeNull();
    }

    [Test]
    public async Task LegacyForceInProcessSettingNoLongerOverridesSidecarManifest()
    {
        await using var harness = RegistrationServiceHarness.Create(new Dictionary<string, string?>
        {
            ["Packages:ForceInProcessDotNetSidecars"] = "true",
        });

        var response = await harness.RegistrationService.EnableAsync(
            TestHarnessConstants.OutOfProcessRegistrationId,
            harness.RootServices,
            CancellationToken.None);

        response.Enabled.Should().BeTrue();
        harness.Registry.GetRuntimeHost(TestHarnessConstants.OutOfProcessRegistrationId)
            .Should()
            .BeAssignableTo<IForeignRegistrationRuntimeHost>();
        harness.Registry.GetRegistration(TestHarnessConstants.OutOfProcessRegistrationId)
            .Should()
            .NotBeNull();
    }

    [Test]
    public async Task ExternalDotNetRegistrationWithoutSidecarHostModeIsRejected()
    {
        await using var harness = RegistrationServiceHarness.Create();
        var registrationDir = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "external-dotnet-hosting-mode",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(registrationDir);
        await File.WriteAllTextAsync(
            Path.Combine(registrationDir, "package.json"),
            """
            {
              "id": "synthetic_external_inprocess",
              "displayName": "Synthetic External In Process",
              "version": "1.0.0",
              "toolPrefix": "sei",
              "entryAssembly": "SharpClaw.TestFixtures.ExternalRegistration.dll",
              "minHostVersion": "0.0.0"
            }
            """);

        var act = async () => await harness.RegistrationService.LoadExternalFromAbsolutePathAsync(
            registrationDir,
            harness.RootServices,
            CancellationToken.None,
            persistDisabledEnvEntry: false);

        await act.Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("*must declare \"hostMode\": \"sidecar\"*");

        harness.Registry.GetRegistration("synthetic_external_inprocess").Should().BeNull();
    }

    [Test]
    public async Task ExternalDotNetRegistrationWithoutSidecarHostModeLoadsWhenInProcessModeIsForced()
    {
        await using var harness = RegistrationServiceHarness.Create(new Dictionary<string, string?>
        {
            [DotNetRegistrationHostingModeOptions.ConfigKey] = "in-process",
        });
        var registrationDir = CreateExternalRegistrationDirectory(
            typeof(SyntheticExternalLifecycleRegistration),
            SyntheticExternalLifecycleRegistration.SourceId,
            "Synthetic External Lifecycle",
            SyntheticExternalLifecycleRegistration.ToolPrefixValue);

        var response = await harness.RegistrationService.LoadExternalFromAbsolutePathAsync(
            registrationDir,
            harness.RootServices,
            CancellationToken.None,
            persistDisabledEnvEntry: false);

        response.SourceId.Should().Be(SyntheticExternalLifecycleRegistration.SourceId);
        harness.Registry.IsExternal(SyntheticExternalLifecycleRegistration.SourceId).Should().BeTrue();
        var runtimeHost = harness.Registry.GetRuntimeHost(SyntheticExternalLifecycleRegistration.SourceId)
            .Should()
            .BeOfType<InProcessRegistrationHost>()
            .Subject;
        var module = harness.Registry.GetRegistration(SyntheticExternalLifecycleRegistration.SourceId);
        module.Should().NotBeNull();

        using var scope = runtimeHost.CreateScope();
        using var parameters = JsonDocument.Parse("""{"value":"forced"}""");
        var result = await module!.ExecuteToolAsync(
            SyntheticExternalLifecycleRegistration.JobTool,
            parameters.RootElement,
            new AgentJobContext(
                Guid.NewGuid(),
                Guid.NewGuid(),
                Guid.NewGuid(),
                ResourceId: null,
                ActionKey: SyntheticExternalLifecycleRegistration.JobTool),
            scope.ServiceProvider,
            CancellationToken.None);

        result.Should().Be("external job forced");
    }

    private sealed class RegistrationServiceHarness : IAsyncDisposable
    {
        private RegistrationServiceHarness(
            ServiceProvider root,
            AsyncServiceScope scope,
            string instanceRoot)
        {
            Root = root;
            Scope = scope;
            InstanceRoot = instanceRoot;
        }

        public ServiceProvider Root { get; }
        public AsyncServiceScope Scope { get; }
        public string InstanceRoot { get; }
        public IServiceProvider RootServices => Root;
        public RegistrationService RegistrationService => Scope.ServiceProvider.GetRequiredService<RegistrationService>();
        public RegistrationCatalog Registry => Root.GetRequiredService<RegistrationCatalog>();

        public static RegistrationServiceHarness Create(
            Dictionary<string, string?>? configurationOverrides = null,
            ISharpClawCoreRegistration[]? modules = null,
            ModuleLoader? registrationLoader = null)
        {
            var instanceRoot = Path.Combine(
                TestContext.CurrentContext.WorkDirectory,
                "bundled-sidecar-default",
                Guid.NewGuid().ToString("N"));
            var instancePaths = new SharpClawInstancePaths(
                SharpClawInstanceKind.Backend,
                explicitInstanceRoot: instanceRoot);
            instancePaths.EnsureDirectories();
            var secretStore = new SupprocomSecretFileStore(
                LocalEnvironment.CreateSecretsOptions(
                    Path.Combine(instanceRoot, "Environment"),
                    isDevelopment: false,
                    instancePaths));

            var configurationValues = new Dictionary<string, string?>
            {
                ["Packages:OutOfProcessSidecarHostPath"] = ResolveOutOfProcessRegistrationHostPath(),
            };
            if (configurationOverrides is not null)
            {
                foreach (var pair in configurationOverrides)
                    configurationValues[pair.Key] = pair.Value;
            }

            var configuration = BuildConfiguration(configurationValues);

            var services = new ServiceCollection();
            services.AddSingleton<IConfiguration>(configuration);
            services.AddSingleton(instancePaths);
            services.AddSingleton(secretStore);
            services.AddSingleton<ISecretDocumentStore>(secretStore);
            services.AddSingleton<ISecretDocumentUpdater>(secretStore);
            services.AddSingleton<ISecretFileProtectionManager>(secretStore);
            services.AddLogging();
            services.AddHttpClient();
            services.AddDbContext<SharpClawDbContext>(options =>
                options.UseInMemoryDatabase(
                    "BundledSidecarDefault_" + Guid.NewGuid().ToString("N"),
                    new InMemoryDatabaseRoot()));
            var loader = registrationLoader
                ?? (modules is not null
                    ? new ModuleLoader(modules)
                    : ModuleLoader.DiscoverBundled(configuration));
            services.AddSingleton(loader);
            services.AddSingleton<RegistrationCatalog>();
            services.AddSingleton<IStorageContractProvider>(sp => sp.GetRequiredService<RegistrationCatalog>());
            services.AddSingleton<ProviderApiClientFactory>();
            services.AddSingleton<RuntimeRegistrationDbContextRegistry>();
            services.AddSingleton<RegistrationPersistenceRegistrationFactory>();
            services.AddSingleton(new RegistrationDbContextOptions
            {
                StorageMode = StorageMode.SQLite,
                ConnectionString = "Data Source=:memory:",
            });
            services.AddSingleton(new EncryptionOptions
            {
                Key = new byte[32],
                EncryptProviderKeys = false,
            });
            services.AddSingleton<IOwnedDbContextFactory, RegistrationDbContextFactory>();
            services.AddSingleton<ChatCache>();
            services.AddSingleton<RegistrationEventDispatcher>(sp => new RegistrationEventDispatcher(
                sp,
                sp.GetRequiredService<IConfiguration>(),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<RegistrationEventDispatcher>>()));
            services.AddSingleton<ISharpClawEventSinkRegistry>(
                sp => sp.GetRequiredService<RegistrationEventDispatcher>());
            services.AddScoped<IScopedStorageGateway, ScopedStorageGateway>();
            services.AddSingleton<IRuntimeTransactionActionBoundary, TestRuntimeTransactionActionBoundary>();
            services.AddScoped<IRuntimeTransactionActionRunnerAccessor,
                RuntimeTransactionActionRunnerAccessor>();
            services.AddScoped<IRuntimeTransactionActionRunner, RuntimeTransactionActionRunner>();
            services.AddScoped<RegistrationService>();

            var root = services.BuildServiceProvider();
            root.GetRequiredService<ModuleLoader>().LoadAllManifests()
                .Should()
                .ContainKey(TestHarnessConstants.OutOfProcessRegistrationId);

            return new RegistrationServiceHarness(root, root.CreateAsyncScope(), instanceRoot);
        }

        public async ValueTask DisposeAsync()
        {
            var loader = Root.GetRequiredService<ModuleLoader>();
            var runtimeBackedRegistrationIds = Registry.GetAllPackages()
                .Select(module => module.Id)
                .Where(SourceId => Registry.GetRuntimeHost(SourceId) is not null)
                .ToArray();

            foreach (var SourceId in runtimeBackedRegistrationIds)
            {
                if (Registry.GetRegistration(SourceId) is null)
                    continue;

                if (Registry.IsExternal(SourceId))
                    await RegistrationService.UnloadExternalAsync(SourceId, CancellationToken.None);
                else if (loader.IsDefaultRegistration(SourceId))
                    await RegistrationService.DisableAsync(SourceId, CancellationToken.None);
            }

            foreach (var runtimeHost in Registry.GetRuntimeHosts())
                await runtimeHost.DisposeAsync();

            await Scope.DisposeAsync();
            await Root.DisposeAsync();

            try
            {
                if (Directory.Exists(InstanceRoot))
                    Directory.Delete(InstanceRoot, recursive: true);
            }
            catch
            {
            }
        }
    }

    private static IConfiguration BuildConfiguration(Dictionary<string, string?> values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

    private static string ResolveOutOfProcessRegistrationHostPath()
    {
        var hostPath = Path.Combine(
            TestContext.CurrentContext.TestDirectory,
            "SharpClaw.SidecarHost.OutOfProcess.dll");

        File.Exists(hostPath).Should().BeTrue(
            $"shared .NET sidecar host package payload must be copied to test output before tests run: '{hostPath}'");
        return hostPath;
    }

    private static string CreateExternalRegistrationDirectory(
        Type entryType,
        string SourceId,
        string displayName,
        string toolPrefix)
    {
        var assemblyPath = entryType.Assembly.Location;
        var sourceDir = Path.GetDirectoryName(assemblyPath)!;
        var registrationDir = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "external-inprocess-modules",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(registrationDir);

        foreach (var file in Directory.GetFiles(sourceDir, "*.dll"))
            File.Copy(file, Path.Combine(registrationDir, Path.GetFileName(file)), overwrite: true);

        foreach (var file in Directory.GetFiles(sourceDir, "*.deps.json"))
            File.Copy(file, Path.Combine(registrationDir, Path.GetFileName(file)), overwrite: true);

        File.WriteAllText(
            Path.Combine(registrationDir, "package.json"),
            $$"""
            {
              "id": "{{SourceId}}",
              "displayName": "{{displayName}}",
              "version": "1.0.0",
              "toolPrefix": "{{toolPrefix}}",
              "runtime": "dotnet",
              "entryAssembly": "{{Path.GetFileName(assemblyPath)}}",
              "type": "{{entryType.FullName}}",
              "minHostVersion": "0.0.0"
            }
            """);

        return registrationDir;
    }

    private static string ResolveRepoRoot()
    {
        var current = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "Directory.Packages.props")))
                return current.FullName;

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate SharpClaw repository root.");
    }
}
