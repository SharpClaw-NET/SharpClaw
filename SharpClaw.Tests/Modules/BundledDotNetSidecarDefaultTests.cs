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

        public static ModuleServiceHarness Create()
        {
            var instanceRoot = Path.Combine(
                TestContext.CurrentContext.WorkDirectory,
                "bundled-sidecar-default",
                Guid.NewGuid().ToString("N"));
            var instancePaths = new SharpClawInstancePaths(
                SharpClawInstanceKind.Backend,
                explicitInstanceRoot: instanceRoot);
            instancePaths.EnsureDirectories();

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Modules:DotNetSidecarHostPath"] = ResolveDotNetSidecarHostPath(),
                })
                .Build();

            var services = new ServiceCollection();
            services.AddSingleton<IConfiguration>(configuration);
            services.AddSingleton(instancePaths);
            services.AddLogging();
            services.AddHttpClient();
            services.AddDbContext<SharpClawDbContext>(options =>
                options.UseInMemoryDatabase(
                    "BundledSidecarDefault_" + Guid.NewGuid().ToString("N"),
                    new InMemoryDatabaseRoot()));
            services.AddSingleton(new ModuleLoader(new TestHarnessModule()));
            services.AddSingleton<ModuleRegistry>();
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
            var runtimeHost = Registry.GetRuntimeHost(TestHarnessConstants.ModuleId);
            if (runtimeHost is not null)
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
