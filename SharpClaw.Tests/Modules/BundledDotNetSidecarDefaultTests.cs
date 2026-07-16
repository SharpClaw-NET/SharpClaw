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
using SharpClaw.Runtime.BLL.Services;
using SharpClaw.Contracts.Modules;
using SharpClaw.Contracts.Persistence;
using SharpClaw.Runtime.INF.Persistence;
using SharpClaw.Runtime.INF.Persistence.Modules;
using SharpClaw.TestFixtures.ExternalModule;
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

        loader.IsManifestOnlyBundledModule(TestHarnessConstants.OutOfProcessModuleId).Should().BeTrue();
        loader.GetBundledModule(TestHarnessConstants.OutOfProcessModuleId)
            .Should()
            .NotBeNull();
        loader.IsManifestOnlyBundledModule("sharpclaw_agent_orchestration")
            .Should()
            .BeTrue("agent orchestration no longer has sidecar readiness blockers");
    }

    [Test]
    public void DiscoverBundledIgnoresLegacyForceInProcessSetting()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Modules:ForceInProcessDotNetSidecars"] = "true",
            })
            .Build();

        var loader = ModuleLoader.DiscoverBundled(configuration);

        loader.IsManifestOnlyBundledModule(TestHarnessConstants.OutOfProcessModuleId).Should().BeTrue();
        loader.GetBundledModule(TestHarnessConstants.OutOfProcessModuleId)
            .Should()
            .NotBeNull();
    }

    [Test]
    public void InProcessDotNetHostingModeIsAcceptedByDiscovery()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [DotNetModuleHostingModeOptions.ConfigKey] = "in-process",
            })
            .Build();

        var loader = ModuleLoader.DiscoverBundled(configuration);

        loader.IsManifestOnlyBundledModule(TestHarnessConstants.OutOfProcessModuleId).Should().BeTrue();
        loader.GetBundledModule(TestHarnessConstants.OutOfProcessModuleId)
            .Should()
            .NotBeNull(
                "discovery remains manifest-only; enabling the module chooses the runtime host");
    }

    [Test]
    public async Task ManifestOnlyBundledSidecarReportsRuntimeDetailsAfterEnable()
    {
        var loader = ModuleLoader.DiscoverBundled();
        loader.IsManifestOnlyBundledModule(TestHarnessConstants.OutOfProcessModuleId).Should().BeTrue();
        await using var harness = ModuleServiceHarness.Create(moduleLoader: loader);

        var response = await harness.ModuleService.EnableAsync(
            TestHarnessConstants.OutOfProcessModuleId,
            harness.RootServices,
            CancellationToken.None);

        response.Enabled.Should().BeTrue();
        harness.Registry.GetRuntimeHost(TestHarnessConstants.OutOfProcessModuleId)
            .Should()
            .BeAssignableTo<IForeignModuleRuntimeHost>();

        var detail = await harness.ModuleService.GetDetailAsync(
            TestHarnessConstants.OutOfProcessModuleId,
            CancellationToken.None);

        detail.Should().NotBeNull();
        detail!.ToolCount.Should().BeGreaterThan(0);
        detail.DisplayName.Should().Be("Test Harness Out Of Process");
    }

    [Test]
    public async Task BundledModuleWithSidecarHostModeRegistersThroughForeignRuntimeHost()
    {
        await using var harness = ModuleServiceHarness.Create();

        var response = await harness.ModuleService.EnableAsync(
            TestHarnessConstants.OutOfProcessModuleId,
            harness.RootServices,
            CancellationToken.None);

        response.Enabled.Should().BeTrue();
        var runtimeHost = harness.Registry.GetRuntimeHost(TestHarnessConstants.OutOfProcessModuleId);
        runtimeHost.Should().BeAssignableTo<IForeignModuleRuntimeHost>();
        harness.Registry.IsExternal(TestHarnessConstants.OutOfProcessModuleId)
            .Should()
            .BeFalse("bundled sidecars have runtime hosts without becoming user-loaded external modules");

        var module = harness.Registry.GetModule(TestHarnessConstants.OutOfProcessModuleId);
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

        await harness.ModuleService.DisableAsync(TestHarnessConstants.OutOfProcessModuleId, CancellationToken.None);
        harness.Registry.GetRuntimeHost(TestHarnessConstants.OutOfProcessModuleId).Should().BeNull();
        harness.Registry.GetModule(TestHarnessConstants.OutOfProcessModuleId).Should().BeNull();
    }

    [Test]
    public async Task SidecarOnlyModeKeepsReadinessCleanBundledModulesOutOfProcess()
    {
        await using var harness = ModuleServiceHarness.Create(new Dictionary<string, string?>
        {
            [DotNetModuleHostingModeOptions.ConfigKey] = "sidecar-only",
        });

        var response = await harness.ModuleService.EnableAsync(
            TestHarnessConstants.OutOfProcessModuleId,
            harness.RootServices,
            CancellationToken.None);

        response.Enabled.Should().BeTrue();
        harness.Registry.GetRuntimeHost(TestHarnessConstants.OutOfProcessModuleId)
            .Should()
            .BeAssignableTo<IForeignModuleRuntimeHost>();
        harness.Registry.GetModule(TestHarnessConstants.OutOfProcessModuleId)
            .Should()
            .NotBeNull();
    }

    [Test]
    public async Task InProcessModeKeepsExplicitBundledSidecarManifestOutOfProcess()
    {
        var settings = new Dictionary<string, string?>
        {
            [DotNetModuleHostingModeOptions.ConfigKey] = "in-process",
        };
        var configuration = BuildConfiguration(settings);
        await using var harness = ModuleServiceHarness.Create(
            settings,
            moduleLoader: ModuleLoader.DiscoverBundled(configuration));

        var response = await harness.ModuleService.EnableAsync(
            TestHarnessConstants.OutOfProcessModuleId,
            harness.RootServices,
            CancellationToken.None);

        response.Enabled.Should().BeTrue();
        var runtimeHost = harness.Registry.GetRuntimeHost(TestHarnessConstants.OutOfProcessModuleId)
            .Should()
            .BeAssignableTo<IForeignModuleRuntimeHost>()
            .Subject;
        var module = harness.Registry.GetModule(TestHarnessConstants.OutOfProcessModuleId);
        module.Should().NotBeNull();
        module!.Id.Should().Be(TestHarnessConstants.OutOfProcessModuleId);
        module.Should().NotBeNull();
        harness.Registry.IsExternal(TestHarnessConstants.OutOfProcessModuleId).Should().BeFalse();

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
    public async Task InProcessTestHarnessLoadsThroughPayloadModuleDirectory()
    {
        await using var harness = ModuleServiceHarness.Create(new Dictionary<string, string?>
        {
            [DotNetModuleHostingModeOptions.ConfigKey] = "in-process",
        });
        var moduleDir = Path.Combine(
            TestContext.CurrentContext.TestDirectory,
            "test-modules",
            TestHarnessConstants.InProcessModuleId);

        Directory.Exists(moduleDir).Should().BeTrue(
            "the in-process TestHarness payload must be copied for module-boundary tests");

        var response = await harness.ModuleService.LoadExternalFromAbsolutePathAsync(
            moduleDir,
            harness.RootServices,
            CancellationToken.None,
            persistDisabledEnvEntry: false);

        response.Enabled.Should().BeTrue();
        var runtimeHost = harness.Registry.GetRuntimeHost(TestHarnessConstants.InProcessModuleId)
            .Should()
            .BeOfType<InProcessModuleHost>()
            .Subject;
        harness.Registry.GetModule(TestHarnessConstants.InProcessModuleId)
            .Should()
            .NotBeNull();

        using var scope = runtimeHost.CreateScope();
        var gateway = scope.ServiceProvider.GetRequiredService<IModuleStorageGateway>();
        gateway.ListContracts().Should().OnlyContain(contract =>
            string.Equals(contract.ModuleId, TestHarnessConstants.InProcessModuleId, StringComparison.Ordinal));
    }

    [Test]
    public async Task InProcessStorageGatewayRejectsOtherModuleStorageRequests()
    {
        await using var harness = ModuleServiceHarness.Create(new Dictionary<string, string?>
        {
            [DotNetModuleHostingModeOptions.ConfigKey] = "in-process",
        });
        var moduleDir = CreateExternalModuleDirectory(
            typeof(InProcessStorageFixtureModule),
            InProcessStorageFixtureModule.ModuleId,
            "Synthetic In-Process Storage",
            InProcessStorageFixtureModule.ToolPrefixValue);

        var response = await harness.ModuleService.LoadExternalFromAbsolutePathAsync(
            moduleDir,
            harness.RootServices,
            CancellationToken.None,
            persistDisabledEnvEntry: false);

        response.Enabled.Should().BeTrue();
        var runtimeHost = harness.Registry.GetRuntimeHost(InProcessStorageFixtureModule.ModuleId)
            .Should()
            .BeOfType<InProcessModuleHost>()
            .Subject;

        using var scope = runtimeHost.CreateScope();
        var gateway = scope.ServiceProvider.GetRequiredService<IModuleStorageGateway>();
        gateway.ListContracts().Should().NotBeEmpty();
        gateway.ListContracts().Should().OnlyContain(contract =>
            string.Equals(contract.ModuleId, InProcessStorageFixtureModule.ModuleId, StringComparison.Ordinal));
        scope.ServiceProvider.GetServices<IModuleStorageGateway>().Should().ContainSingle(
            "module-owned fake gateway registrations are replaced by the host-owned wrapper");
        scope.ServiceProvider.GetService<SharpClawDbContext>().Should().BeNull(
            "in-process modules must not receive the raw host DbContext");

        var restricted = ModuleHostServiceAccess.CreateRestrictedScope(
            scope.ServiceProvider,
            InProcessStorageFixtureModule.ModuleId);
        var blockedRawDb = () => restricted.GetRequiredService<SharpClawDbContext>();
        blockedRawDb.Should().Throw<InvalidOperationException>()
            .WithMessage("*blocked service*SharpClawDbContext*");

        using var parameters = JsonDocument.Parse("{}");
        var act = async () => await gateway.InvokeAsync(
            TestHarnessConstants.OutOfProcessModuleId,
            InProcessStorageFixtureModule.StorageName,
            ModuleStorageOperations.List,
            parameters.RootElement,
            CancellationToken.None);

        await act.Should().ThrowAsync<UnauthorizedAccessException>()
            .WithMessage("*cannot access storage owned by module*");
    }

    [Test]
    public async Task SidecarManifestBundledModulesRegisterThroughForeignRuntimeHost()
    {
        var loader = ModuleLoader.DiscoverBundled();
        var bundledModules = loader.GetAllBundled()
            .Where(module => loader.IsManifestOnlyBundledModule(module.Id))
            .OrderBy(module => module.Id, StringComparer.Ordinal)
            .ToArray();
        bundledModules.Select(module => module.Id).Should().Equal(
        [
            "sharpclaw_agent_orchestration",
            "sharpclaw_editor_common",
            "sharpclaw_metrics",
            "sharpclaw_module_dev",
            "sharpclaw_providers_anthropic",
            "sharpclaw_providers_google",
            "sharpclaw_providers_llamasharp",
            "sharpclaw_providers_ollama",
            "sharpclaw_providers_openai_compat",
            TestHarnessConstants.OutOfProcessModuleId,
            "sharpclaw_vs2026_editor",
            "sharpclaw_vscode_editor",
        ]);
        await using var harness = ModuleServiceHarness.Create(moduleLoader: loader);
        var enabledModuleIds = new List<string>();

        try
        {
            foreach (var bundledModule in bundledModules)
            {
                var response = await harness.ModuleService.EnableAsync(
                    bundledModule.Id,
                    harness.RootServices,
                    CancellationToken.None);

                enabledModuleIds.Add(bundledModule.Id);
                response.Enabled.Should().BeTrue();
                harness.Registry.GetRuntimeHost(bundledModule.Id)
                    .Should()
                    .BeAssignableTo<IForeignModuleRuntimeHost>(
                        $"module '{bundledModule.Id}' declares sidecar host mode");
                harness.Registry.GetModule(bundledModule.Id)
                    .Should()
                    .NotBeSameAs(bundledModule);
            }
        }
        finally
        {
            foreach (var moduleId in enabledModuleIds.AsEnumerable().Reverse())
                await harness.ModuleService.DisableAsync(moduleId, CancellationToken.None);
        }
    }

    [Test]
    public async Task SidecarProviderPluginsAreVisibleThroughParentFactoryOnlyWhileEnabled()
    {
        await using var harness = ModuleServiceHarness.Create();

        var response = await harness.ModuleService.EnableAsync(
            "sharpclaw_providers_openai_compat",
            harness.RootServices,
            CancellationToken.None);

        response.Enabled.Should().BeTrue();
        harness.Registry.GetRuntimeHost("sharpclaw_providers_openai_compat")
            .Should()
            .BeAssignableTo<IForeignModuleRuntimeHost>();

        var factory = harness.Root.GetRequiredService<ProviderApiClientFactory>();
        factory.IsAvailable("openai").Should().BeTrue();
        factory.GetPlugin("openai")!.OwnerModuleId.Should().Be("sharpclaw_providers_openai_compat");
        factory.GetPlugin("custom")!.RequiresEndpoint.Should().BeTrue();

        await harness.ModuleService.DisableAsync(
            "sharpclaw_providers_openai_compat",
            CancellationToken.None);

        factory.IsAvailable("openai").Should().BeFalse();
    }

    [Test]
    public async Task EditorCommonSidecarAdvertisesEditorWebSocketEndpoint()
    {
        await using var harness = ModuleServiceHarness.Create();

        var response = await harness.ModuleService.EnableAsync(
            "sharpclaw_editor_common",
            harness.RootServices,
            CancellationToken.None);

        response.Enabled.Should().BeTrue();
        var runtimeHost = harness.Registry.GetRuntimeHost("sharpclaw_editor_common")
            .Should()
            .BeAssignableTo<IForeignModuleRuntimeHost>()
            .Subject;

        runtimeHost.Endpoints.Should().Contain(endpoint =>
            string.Equals(endpoint.Method, "GET", StringComparison.Ordinal)
            && string.Equals(endpoint.RoutePattern, "/editor/ws", StringComparison.Ordinal)
            && string.Equals(
                endpoint.ResponseMode,
                ForeignModuleEndpointResponseMode.WebSocket,
                StringComparison.Ordinal));
    }

    [Test]
    public async Task SidecarOnlyModeRunsAgentOrchestrationOutOfProcess()
    {
        var settings = new Dictionary<string, string?>
        {
            [DotNetModuleHostingModeOptions.ConfigKey] = "sidecar-only",
        };
        var configuration = BuildConfiguration(settings);
        await using var harness = ModuleServiceHarness.Create(
            settings,
            moduleLoader: ModuleLoader.DiscoverBundled(configuration));

        var response = await harness.ModuleService.EnableAsync(
            "sharpclaw_agent_orchestration",
            harness.RootServices,
            CancellationToken.None);

        response.Enabled.Should().BeTrue();
        harness.Registry.GetRuntimeHost("sharpclaw_agent_orchestration")
            .Should()
            .BeAssignableTo<IForeignModuleRuntimeHost>();
        harness.Registry.GetModule("sharpclaw_agent_orchestration")
            .Should()
            .NotBeNull();
    }

    [Test]
    public async Task LegacyForceInProcessSettingNoLongerOverridesSidecarManifest()
    {
        await using var harness = ModuleServiceHarness.Create(new Dictionary<string, string?>
        {
            ["Modules:ForceInProcessDotNetSidecars"] = "true",
        });

        var response = await harness.ModuleService.EnableAsync(
            TestHarnessConstants.OutOfProcessModuleId,
            harness.RootServices,
            CancellationToken.None);

        response.Enabled.Should().BeTrue();
        harness.Registry.GetRuntimeHost(TestHarnessConstants.OutOfProcessModuleId)
            .Should()
            .BeAssignableTo<IForeignModuleRuntimeHost>();
        harness.Registry.GetModule(TestHarnessConstants.OutOfProcessModuleId)
            .Should()
            .NotBeNull();
    }

    [Test]
    public async Task ExternalDotNetModuleWithoutSidecarHostModeIsRejected()
    {
        await using var harness = ModuleServiceHarness.Create();
        var moduleDir = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "external-dotnet-hosting-mode",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(moduleDir);
        await File.WriteAllTextAsync(
            Path.Combine(moduleDir, "module.json"),
            """
            {
              "id": "synthetic_external_inprocess",
              "displayName": "Synthetic External In Process",
              "version": "1.0.0",
              "toolPrefix": "sei",
              "entryAssembly": "SharpClaw.TestFixtures.ExternalModule.dll",
              "minHostVersion": "0.0.0"
            }
            """);

        var act = async () => await harness.ModuleService.LoadExternalFromAbsolutePathAsync(
            moduleDir,
            harness.RootServices,
            CancellationToken.None,
            persistDisabledEnvEntry: false);

        await act.Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("*must declare \"hostMode\": \"sidecar\"*");

        harness.Registry.GetModule("synthetic_external_inprocess").Should().BeNull();
    }

    [Test]
    public async Task ExternalDotNetModuleWithoutSidecarHostModeLoadsWhenInProcessModeIsForced()
    {
        await using var harness = ModuleServiceHarness.Create(new Dictionary<string, string?>
        {
            [DotNetModuleHostingModeOptions.ConfigKey] = "in-process",
        });
        var moduleDir = CreateExternalModuleDirectory(
            typeof(SyntheticExternalLifecycleModule),
            SyntheticExternalLifecycleModule.ModuleId,
            "Synthetic External Lifecycle",
            SyntheticExternalLifecycleModule.ToolPrefixValue);

        var response = await harness.ModuleService.LoadExternalFromAbsolutePathAsync(
            moduleDir,
            harness.RootServices,
            CancellationToken.None,
            persistDisabledEnvEntry: false);

        response.ModuleId.Should().Be(SyntheticExternalLifecycleModule.ModuleId);
        harness.Registry.IsExternal(SyntheticExternalLifecycleModule.ModuleId).Should().BeTrue();
        var runtimeHost = harness.Registry.GetRuntimeHost(SyntheticExternalLifecycleModule.ModuleId)
            .Should()
            .BeOfType<InProcessModuleHost>()
            .Subject;
        var module = harness.Registry.GetModule(SyntheticExternalLifecycleModule.ModuleId);
        module.Should().NotBeNull();

        using var scope = runtimeHost.CreateScope();
        using var parameters = JsonDocument.Parse("""{"value":"forced"}""");
        var result = await module!.ExecuteToolAsync(
            SyntheticExternalLifecycleModule.JobTool,
            parameters.RootElement,
            new AgentJobContext(
                Guid.NewGuid(),
                Guid.NewGuid(),
                Guid.NewGuid(),
                ResourceId: null,
                ActionKey: SyntheticExternalLifecycleModule.JobTool),
            scope.ServiceProvider,
            CancellationToken.None);

        result.Should().Be("external job forced");
    }

    private sealed class ModuleServiceHarness : IAsyncDisposable
    {
        private ModuleServiceHarness(
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
        public ModuleService ModuleService => Scope.ServiceProvider.GetRequiredService<ModuleService>();
        public ModuleRegistry Registry => Root.GetRequiredService<ModuleRegistry>();

        public static ModuleServiceHarness Create(
            Dictionary<string, string?>? configurationOverrides = null,
            ISharpClawCoreModule[]? modules = null,
            ModuleLoader? moduleLoader = null)
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
                ["Modules:OutOfProcessModuleHostPath"] = ResolveOutOfProcessModuleHostPath(),
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
            var loader = moduleLoader
                ?? (modules is not null
                    ? new ModuleLoader(modules)
                    : ModuleLoader.DiscoverBundled(configuration));
            services.AddSingleton(loader);
            services.AddSingleton<ModuleRegistry>();
            services.AddSingleton<IModuleStorageContractProvider>(sp => sp.GetRequiredService<ModuleRegistry>());
            services.AddSingleton<ProviderApiClientFactory>();
            services.AddSingleton<RuntimeModuleDbContextRegistry>();
            services.AddSingleton<ModulePersistenceRegistrationFactory>();
            services.AddSingleton(new ModuleDbContextOptions
            {
                StorageMode = StorageMode.SQLite,
                ConnectionString = "Data Source=:memory:",
            });
            services.AddSingleton(new EncryptionOptions
            {
                Key = new byte[32],
                EncryptProviderKeys = false,
            });
            services.AddSingleton<IModuleDbContextFactory, ModuleDbContextFactory>();
            services.AddSingleton<ChatCache>();
            services.AddSingleton<ModuleEventDispatcher>(sp => new ModuleEventDispatcher(
                sp,
                sp.GetRequiredService<IConfiguration>(),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<ModuleEventDispatcher>>()));
            services.AddSingleton<ISharpClawEventSinkRegistry>(
                sp => sp.GetRequiredService<ModuleEventDispatcher>());
            services.AddScoped<IModuleStorageGateway, BundledModuleStorageGateway>();
            services.AddScoped<ModuleService>();

            var root = services.BuildServiceProvider();
            root.GetRequiredService<ModuleLoader>().LoadAllManifests()
                .Should()
                .ContainKey(TestHarnessConstants.OutOfProcessModuleId);

            return new ModuleServiceHarness(root, root.CreateAsyncScope(), instanceRoot);
        }

        public async ValueTask DisposeAsync()
        {
            var loader = Root.GetRequiredService<ModuleLoader>();
            var runtimeBackedModuleIds = Registry.GetAllModules()
                .Select(module => module.Id)
                .Where(moduleId => Registry.GetRuntimeHost(moduleId) is not null)
                .ToArray();

            foreach (var moduleId in runtimeBackedModuleIds)
            {
                if (Registry.GetModule(moduleId) is null)
                    continue;

                if (Registry.IsExternal(moduleId))
                    await ModuleService.UnloadExternalAsync(moduleId, CancellationToken.None);
                else if (loader.IsDefaultModule(moduleId))
                    await ModuleService.DisableAsync(moduleId, CancellationToken.None);
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

    private static string ResolveOutOfProcessModuleHostPath()
    {
        var hostPath = Path.Combine(
            TestContext.CurrentContext.TestDirectory,
            "SharpClaw.ModuleHost.OutOfProcess.dll");

        File.Exists(hostPath).Should().BeTrue(
            $"shared .NET sidecar host package payload must be copied to test output before tests run: '{hostPath}'");
        return hostPath;
    }

    private static string CreateExternalModuleDirectory(
        Type moduleType,
        string moduleId,
        string displayName,
        string toolPrefix)
    {
        var assemblyPath = moduleType.Assembly.Location;
        var sourceDir = Path.GetDirectoryName(assemblyPath)!;
        var moduleDir = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "external-inprocess-modules",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(moduleDir);

        foreach (var file in Directory.GetFiles(sourceDir, "*.dll"))
            File.Copy(file, Path.Combine(moduleDir, Path.GetFileName(file)), overwrite: true);

        foreach (var file in Directory.GetFiles(sourceDir, "*.deps.json"))
            File.Copy(file, Path.Combine(moduleDir, Path.GetFileName(file)), overwrite: true);

        File.WriteAllText(
            Path.Combine(moduleDir, "module.json"),
            $$"""
            {
              "id": "{{moduleId}}",
              "displayName": "{{displayName}}",
              "version": "1.0.0",
              "toolPrefix": "{{toolPrefix}}",
              "runtime": "dotnet",
              "entryAssembly": "{{Path.GetFileName(assemblyPath)}}",
              "type": "{{moduleType.FullName}}",
              "minHostVersion": "0.0.0"
            }
            """);

        return moduleDir;
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
