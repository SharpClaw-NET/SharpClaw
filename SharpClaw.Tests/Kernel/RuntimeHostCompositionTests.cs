using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.Persistence;
using SharpClaw.Runtime.BLL.Kernel;
using SharpClaw.Runtime.Host;
using SharpClaw.Runtime.INF.Persistence;
using SharpClaw.Shared.Instances;
using SharpClaw.Shared.Security;

namespace SharpClaw.Tests.Kernel;

[TestFixture]
public sealed class RuntimeHostCompositionTests
{
    [Test]
    [NonParallelizable]
    public async Task PackagedInProcessModule_ComposesHostGraphAndServesChat()
    {
        var moduleRoot = Path.Combine(AppContext.BaseDirectory, "test-modules");
        Directory.Exists(moduleRoot).Should().BeTrue(
            $"the test build must provide the packaged module payload at '{moduleRoot}'");

        using var workspace = new TemporaryWorkspace();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Provider:Key"] = "sharpclaw-test",
                ["Provider:Model"] = "test-harness-model",
            })
            .Build();
        using var moduleSet = PackagedDotNetModuleSet.Load(
            moduleRoot,
            configuration);
        moduleSet.Modules.Should().ContainSingle()
            .Which.Identity.Id.Should().Be("sharpclaw_test_harness_in_process");

        var databaseOptions = new DatabaseProviderOptions
        {
            Provider = StorageMode.JsonFile,
        };
        databaseOptions.JsonFile.DataDirectory = workspace.DatabaseDirectory;
        databaseOptions.JsonFile.EncryptAtRest = false;

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(KernelHostEndpoints).Assembly.GetName().Name,
        });
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddConfiguration(configuration);
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        RuntimeHostComposition.RegisterServices(
            builder.Services,
            configuration,
            workspace.InstancePaths,
            new EncryptionOptions
            {
                Key = new byte[32],
            },
            databaseOptions,
            moduleSet.Modules);

        await using var app = builder.Build();
        var readiness = app.Services.GetRequiredService<RuntimeReadinessState>();
        readiness.IsReady.Should().BeFalse();
        var adapter = app.Services.GetRequiredService<RuntimeKernelAdapter>();
        await app.Services.GetRequiredService<RuntimeDatabaseReadiness>().ValidateAsync();
        await adapter.StartAsync("test-host");
        readiness.MarkReady();
        KernelHostEndpoints.Map(app);

        try
        {
            await app.StartAsync();
            using var client = new HttpClient
            {
                BaseAddress = new Uri(app.Urls.Single()),
            };
            using var response = await client.PostAsJsonAsync(
                "/chat",
                new { message = "hello" });
            var body = await response.Content.ReadAsStringAsync();

            response.StatusCode.Should().Be(HttpStatusCode.OK, body);
            body.Should().Contain("test harness response");
            readiness.IsReady.Should().BeTrue();
        }
        finally
        {
            readiness.MarkNotReady();
            await adapter.StopAsync();
            await app.StopAsync();
        }
    }

    [Test]
    public void MissingConfiguredProviderFailsBeforeReadiness()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();
        using var workspace = new TemporaryWorkspace();
        using var moduleSet = PackagedDotNetModuleSet.Load(
            Path.Combine(AppContext.BaseDirectory, "test-modules"),
            configuration);
        var services = new ServiceCollection();
        RuntimeHostComposition.RegisterServices(
            services,
            configuration,
            workspace.InstancePaths,
            new EncryptionOptions { Key = new byte[32] },
            new DatabaseProviderOptions
            {
                Provider = StorageMode.JsonFile,
            },
            moduleSet.Modules);

        using var provider = services.BuildServiceProvider();
        var exception = FluentActions.Invoking(() =>
            provider.GetRequiredService<RuntimeKernelAdapter>())
            .Should().Throw<InvalidOperationException>();
        exception.Which.Message.Should().Contain("Provider:Key");
        provider.GetRequiredService<RuntimeReadinessState>().IsReady.Should().BeFalse();
    }

    [Test]
    public void DisabledPackagedInProcessModule_IsExcludedBeforeGraphCompilation()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Modules:sharpclaw_test_harness_in_process"] = "false",
            })
            .Build();

        using var moduleSet = PackagedDotNetModuleSet.Load(
            Path.Combine(AppContext.BaseDirectory, "test-modules"),
            configuration);

        moduleSet.Modules.Should().BeEmpty();
    }

    private sealed class TemporaryWorkspace : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            "sharpclaw-runtime-host-" + Guid.NewGuid().ToString("N"));

        public TemporaryWorkspace()
        {
            Directory.CreateDirectory(_root);
            InstancePaths = new SharpClawInstancePaths(
                SharpClawInstanceKind.Backend,
                _root,
                _root,
                _root);
            InstancePaths.EnsureDirectories();
        }

        public SharpClawInstancePaths InstancePaths { get; }

        public string DatabaseDirectory => Path.Combine(_root, "database");

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_root))
                    Directory.Delete(_root, recursive: true);
            }
            catch
            {
            }
        }
    }
}
