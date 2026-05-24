using System.Reflection;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Application.Core.Clients;
using SharpClaw.Application.Core.Modules;
using SharpClaw.Application.Core.Modules.Foreign;
using SharpClaw.Application.Services;
using SharpClaw.Contracts.Modules;
using SharpClaw.Contracts.Persistence;
using SharpClaw.Infrastructure.Persistence;
using SharpClaw.Infrastructure.Persistence.JSON;
using SharpClaw.Infrastructure.Persistence.Modules;
using SharpClaw.Modules.TestHarness;
using SharpClaw.Utils.Instances;

namespace SharpClaw.Tests.Modules;

[TestFixture]
public sealed class BundledDotNetSidecarDefaultTests
{
    [Test]
    public void DiscoverBundledUsesManifestOnlyEntriesForSidecarHostModeByDefault()
    {
        var loader = ModuleLoader.DiscoverBundled();

        loader.IsManifestOnlyBundledModule(TestHarnessConstants.ModuleId).Should().BeTrue();
        loader.GetBundledModule(TestHarnessConstants.ModuleId)
            .Should()
            .NotBeOfType<TestHarnessModule>();
        loader.IsManifestOnlyBundledModule("sharpclaw_agent_orchestration")
            .Should()
            .BeFalse("agent orchestration still has sidecar readiness blockers");
    }

    [Test]
    public void DiscoverBundledForceInProcessLoadsConcreteModules()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [DotNetModuleHostingModeOptions.ForceInProcessKey] = "true",
            })
            .Build();

        var loader = ModuleLoader.DiscoverBundled(configuration);

        loader.IsManifestOnlyBundledModule(TestHarnessConstants.ModuleId).Should().BeFalse();
        loader.GetBundledModule(TestHarnessConstants.ModuleId)
            .Should()
            .BeOfType<TestHarnessModule>();
    }

    [Test]
    public async Task ManifestOnlyBundledSidecarReportsRuntimeDetailsAfterEnable()
    {
        var loader = ModuleLoader.DiscoverBundled();
        loader.IsManifestOnlyBundledModule(TestHarnessConstants.ModuleId).Should().BeTrue();
        await using var harness = ModuleServiceHarness.Create(moduleLoader: loader);

        var response = await harness.ModuleService.EnableAsync(
            TestHarnessConstants.ModuleId,
            harness.RootServices,
            CancellationToken.None);

        response.Enabled.Should().BeTrue();
        harness.Registry.GetRuntimeHost(TestHarnessConstants.ModuleId)
            .Should()
            .BeAssignableTo<IForeignModuleRuntimeHost>();

        var detail = await harness.ModuleService.GetDetailAsync(
            TestHarnessConstants.ModuleId,
            CancellationToken.None);

        detail.Should().NotBeNull();
        detail!.ToolCount.Should().BeGreaterThan(0);
        detail.DisplayName.Should().Be("Test Harness");
    }

    [Test]
    public async Task BundledModuleWithSidecarHostModeRegistersThroughForeignRuntimeHost()
    {
        await using var harness = ModuleServiceHarness.Create();

        var response = await harness.ModuleService.EnableAsync(
            TestHarnessConstants.ModuleId,
            harness.RootServices,
            CancellationToken.None);

        response.Enabled.Should().BeTrue();
        var runtimeHost = harness.Registry.GetRuntimeHost(TestHarnessConstants.ModuleId);
        runtimeHost.Should().BeAssignableTo<IForeignModuleRuntimeHost>();
        harness.Registry.IsExternal(TestHarnessConstants.ModuleId)
            .Should()
            .BeFalse("bundled sidecars have runtime hosts without becoming user-loaded external modules");

        var module = harness.Registry.GetModule(TestHarnessConstants.ModuleId);
        module.Should().NotBeNull();
        module.Should().NotBeOfType<TestHarnessModule>();

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

        await harness.ModuleService.DisableAsync(TestHarnessConstants.ModuleId, CancellationToken.None);
        harness.Registry.GetRuntimeHost(TestHarnessConstants.ModuleId).Should().BeNull();
        harness.Registry.GetModule(TestHarnessConstants.ModuleId).Should().BeNull();
    }

    [Test]
    public async Task SidecarOnlyModeKeepsReadinessCleanBundledModulesOutOfProcess()
    {
        await using var harness = ModuleServiceHarness.Create(new Dictionary<string, string?>
        {
            [DotNetModuleHostingModeOptions.ConfigKey] = "sidecar-only",
        });

        var response = await harness.ModuleService.EnableAsync(
            TestHarnessConstants.ModuleId,
            harness.RootServices,
            CancellationToken.None);

        response.Enabled.Should().BeTrue();
        harness.Registry.GetRuntimeHost(TestHarnessConstants.ModuleId)
            .Should()
            .BeAssignableTo<IForeignModuleRuntimeHost>();
        harness.Registry.GetModule(TestHarnessConstants.ModuleId)
            .Should()
            .NotBeOfType<TestHarnessModule>();
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
            "sharpclaw_metrics",
            "sharpclaw_module_dev",
            "sharpclaw_providers_anthropic",
            "sharpclaw_providers_google",
            "sharpclaw_providers_ollama",
            "sharpclaw_providers_openai_compat",
            TestHarnessConstants.ModuleId,
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
                    .BeAssignableTo<IForeignModuleRuntimeHost>();
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
    public async Task SidecarOnlyModeRejectsBundledModulesWithReadinessBlockers()
    {
        var settings = new Dictionary<string, string?>
        {
            [DotNetModuleHostingModeOptions.ConfigKey] = "sidecar-only",
        };
        var configuration = BuildConfiguration(settings);
        await using var harness = ModuleServiceHarness.Create(
            settings,
            moduleLoader: ModuleLoader.DiscoverBundled(configuration));

        var act = async () => await harness.ModuleService.EnableAsync(
            "sharpclaw_agent_orchestration",
            harness.RootServices,
            CancellationToken.None);

        var assertion = await act.Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("*Sidecar readiness blocker*");
        assertion.Which.Message.Should().Contain("storage.module_dbcontexts");

        harness.Registry.GetModule("sharpclaw_agent_orchestration").Should().BeNull();
        harness.Registry.GetRuntimeHost("sharpclaw_agent_orchestration").Should().BeNull();
    }

    [Test]
    public async Task LegacyForceInProcessSettingStillOverridesSidecarManifest()
    {
        await using var harness = ModuleServiceHarness.Create(new Dictionary<string, string?>
        {
            [DotNetModuleHostingModeOptions.ForceInProcessKey] = "true",
        });

        var response = await harness.ModuleService.EnableAsync(
            TestHarnessConstants.ModuleId,
            harness.RootServices,
            CancellationToken.None);

        response.Enabled.Should().BeTrue();
        harness.Registry.GetRuntimeHost(TestHarnessConstants.ModuleId).Should().BeNull();
        harness.Registry.GetModule(TestHarnessConstants.ModuleId)
            .Should()
            .BeOfType<TestHarnessModule>();
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
            ISharpClawModule[]? modules = null,
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

            var configurationValues = new Dictionary<string, string?>
            {
                ["Modules:DotNetSidecarHostPath"] = ResolveDotNetSidecarHostPath(),
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
            services.AddSingleton<ProviderApiClientFactory>();
            services.AddSingleton<RuntimeModuleDbContextRegistry>();
            services.AddSingleton<ModulePersistenceRegistrationFactory>();
            services.AddSingleton(new ModuleDbContextOptions
            {
                StorageMode = StorageMode.SQLite,
                ConnectionString = "Data Source=:memory:",
            });
            services.AddSingleton(new JsonFileOptions
            {
                DataDirectory = Path.Combine(instanceRoot, "Data"),
            });
            services.AddSingleton(new EncryptionOptions
            {
                Key = new byte[32],
                EncryptProviderKeys = false,
            });
            services.AddSingleton<IPersistenceFileSystem, InMemoryPersistenceFileSystem>();
            services.AddSingleton<IModuleDbContextFactory, ModuleDbContextFactory>();
            services.AddSingleton<ModuleJsonPersistenceService>();
            services.AddSingleton<ChatCache>();
            services.AddSingleton<ModuleEventDispatcher>(sp => new ModuleEventDispatcher(
                sp,
                sp.GetRequiredService<IConfiguration>(),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<ModuleEventDispatcher>>()));
            services.AddScoped<ModuleService>();

            var root = services.BuildServiceProvider();
            root.GetRequiredService<ModuleLoader>().LoadAllManifests()
                .Should()
                .ContainKey(TestHarnessConstants.ModuleId);

            return new ModuleServiceHarness(root, root.CreateAsyncScope(), instanceRoot);
        }

        public async ValueTask DisposeAsync()
        {
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

    private static string ResolveDotNetSidecarHostPath()
    {
        var root = ResolveRepoRoot();
        var configuration = Directory.GetParent(TestContext.CurrentContext.TestDirectory)!.Name;
        var hostPath = Path.Combine(
            root,
            "SharpClaw.Modules.DotNetSidecarHost",
            "bin",
            configuration,
            "net10.0",
            "SharpClaw.Modules.DotNetSidecarHost.dll");

        File.Exists(hostPath).Should().BeTrue(
            $"shared .NET sidecar host must be built before tests run: '{hostPath}'");
        return hostPath;
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
