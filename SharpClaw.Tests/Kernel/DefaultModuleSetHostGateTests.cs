using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.Kernel;
using SharpClaw.Contracts.Persistence;
using SharpClaw.Core.Kernel;
using SharpClaw.Runtime.BLL.Kernel;
using SharpClaw.Runtime.Host;
using SharpClaw.Runtime.Host.Api;
using SharpClaw.Runtime.INF.Persistence;
using SharpClaw.Shared.Instances;
using SharpClaw.Shared.Security;

namespace SharpClaw.Tests.Kernel;

[TestFixture]
[NonParallelizable]
public sealed class DefaultModuleSetHostGateTests
{
    private static readonly string[] ArchivedRegistrationIds =
    [
        "sharpclaw_agents",
        "sharpclaw_context",
        "sharpclaw_two_tier_permission",
        "sharpclaw_test_permission_restriction",
    ];

    [Test, CancelAfter(300000)]
    public async Task ProductionHost_ComposesDefaultModulesWithoutArchivedAgentOrchestration()
    {
        var initialSidecars = FindSidecarProcessIds();
        await using var provider = await FakeOpenAiServer.CreateAsync();
        using var workspace = new TemporaryWorkspace();
        var configuration = CreateConfiguration(provider.Endpoint);
        var contributionRoot = Path.Combine(AppContext.BaseDirectory, "contributions");

        Directory.Exists(contributionRoot).Should().BeTrue(
            $"the test build must provide the default package payload at '{contributionRoot}'");
        var packagedIds = Directory.EnumerateDirectories(contributionRoot)
            .Select(Path.GetFileName)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.Ordinal);
        packagedIds.Should().NotContain(ArchivedRegistrationIds);
        packagedIds.Should().Contain("sharpclaw_providers_openai_compat");

        await using (var registrationSet = await PackagedDotNetRegistrationSet.LoadProductionAsync(
                         [contributionRoot],
                         configuration))
        {
            registrationSet.SourceIds.Should().NotContain(ArchivedRegistrationIds);
            registrationSet.SourceIds.Should().Contain("sharpclaw_providers_openai_compat");

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
                new EncryptionOptions { Key = new byte[32] },
                databaseOptions,
                registrationSet.Services);

            await using var app = builder.Build();
            var readiness = app.Services.GetRequiredService<RuntimeReadinessState>();
            var adapter = app.Services.GetRequiredService<RuntimeKernelAdapter>();
            app.Services.GetRequiredService<IActionDispatcher>().Should().BeSameAs(adapter.ActionDispatcher);

            await app.Services.GetRequiredService<RuntimeDatabaseReadiness>().ValidateAsync();
            await registrationSet.ConnectCapabilitiesAsync(app.Services);
            await adapter.StartAsync("default-package-production-gate");
            readiness.MarkReady();

            app.Use((context, next) =>
            {
                context.User = Administrator();
                return next();
            });
            app.UseMiddleware<ApiKeyMiddleware>();
            app.UseWebSockets();
            KernelHostEndpoints.Map(app);
            registrationSet.Application.MapEndpoints(app, adapter);

            try
            {
                await app.StartAsync();
                using var client = new HttpClient
                {
                    BaseAddress = new Uri(app.Urls.Single()),
                    Timeout = TimeSpan.FromSeconds(30),
                };
                client.DefaultRequestHeaders.Add(
                    "X-Api-Key",
                    app.Services.GetRequiredService<ApiKeyProvider>().ApiKey);

                using var response = await client.PostAsJsonAsync("/chat", new { message = "default gate" });
                var body = await response.Content.ReadAsStringAsync();
                response.StatusCode.Should().Be(HttpStatusCode.OK, body);
                body.Should().Contain("default package graph response");
                provider.RequestCount.Should().Be(1);
            }
            finally
            {
                readiness.MarkNotReady();
                try
                {
                    await adapter.StopAsync();
                }
                finally
                {
                    await app.StopAsync();
                }
            }
        }

        await AssertSidecarsStoppedAsync(initialSidecars);
    }

    private static IConfiguration CreateConfiguration(string providerEndpoint) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Provider:Key"] = "custom",
                ["Provider:Model"] = "default-gate-model",
                ["Provider:ApiKey"] = "default-gate-key",
                ["Provider:Endpoint"] = providerEndpoint,
                ["Auth:DisableApiKeyCheck"] = "false",
            })
            .Build();

    private static ClaimsPrincipal Administrator()
    {
        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, "default-gate-administrator"),
            new Claim(ClaimTypes.Name, "Default Gate Administrator"),
            new Claim(ClaimTypes.Role, "admin"),
            new Claim(ClaimTypes.Role, "administrator"),
        ],
        "default-package-gate");
        return new ClaimsPrincipal(identity);
    }

    private static HashSet<int> FindSidecarProcessIds() =>
        Process.GetProcessesByName("SharpClaw.SidecarHost.OutOfProcess")
            .Select(process => process.Id)
            .ToHashSet();

    private static async Task AssertSidecarsStoppedAsync(IReadOnlySet<int> initialProcessIds)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        HashSet<int> remaining;
        do
        {
            remaining = FindSidecarProcessIds();
            remaining.ExceptWith(initialProcessIds);
            if (remaining.Count == 0)
                return;
            await Task.Delay(100);
        }
        while (DateTimeOffset.UtcNow < deadline);

        remaining.Should().BeEmpty("the default module set must stop every task-owned sidecar");
    }

    private sealed class TemporaryWorkspace : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            "sharpclaw-default-package-gate-" + Guid.NewGuid().ToString("N"));

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
                    Directory.Delete(_root, true);
            }
            catch
            {
            }
        }
    }

    private sealed class FakeOpenAiServer : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private int _requestCount;

        private FakeOpenAiServer(WebApplication app)
        {
            _app = app;
        }

        public string Endpoint => _app.Urls.Single() + "/v1";

        public int RequestCount => Volatile.Read(ref _requestCount);

        public static async Task<FakeOpenAiServer> CreateAsync()
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            var app = builder.Build();
            var server = new FakeOpenAiServer(app);
            app.MapPost("/v1/chat/completions", () =>
            {
                Interlocked.Increment(ref server._requestCount);
                return Results.Json(new
                {
                    id = "default-package-production-gate",
                    choices = new[]
                    {
                        new
                        {
                            index = 0,
                            message = new
                            {
                                role = "assistant",
                                content = "default package graph response",
                            },
                            finish_reason = "stop",
                        },
                    },
                    usage = new
                    {
                        prompt_tokens = 1,
                        completion_tokens = 1,
                    },
                });
            });
            await app.StartAsync();
            return server;
        }

        public ValueTask DisposeAsync() => _app.DisposeAsync();
    }
}
