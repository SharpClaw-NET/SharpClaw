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
using SharpClaw.Persistence;
using SharpClaw.Runtime.BLL.Kernel;
using SharpClaw.Runtime.Host;
using SharpClaw.Runtime.Host.Api;
using SharpClaw.Runtime.INF.Persistence;
using SharpClaw.Shared.Instances;
using SharpClaw.Shared.Security;

namespace SharpClaw.Tests.Kernel;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812",
    Justification = "NUnit discovers and constructs this internal fixture through reflection; its tests are executed by the maintained test suite.")]
[TestFixture]
[NonParallelizable]
internal sealed class DefaultModuleSetHostGateTests
{
    private static readonly string[] ArchivedRegistrationIds =
    [
        "sharpclaw_agents",
        "sharpclaw_context",
        "sharpclaw_two_tier_permission",
        "sharpclaw_test_permission_restriction",
    ];

    [TestCase(true), TestCase(false), CancelAfter(300000)]
    public async Task ProductionHost_ComposesDefaultModulesWithoutArchivedAgentOrchestrationAsync(bool configured)
    {
        var initialSidecars = FindSidecarProcessIds();
        var provider = await FakeOpenAiServer.CreateAsync().ConfigureAwait(false);
        await using var providerAsyncDisposal = provider.ConfigureAwait(false);
        using var workspace = new TemporaryWorkspace();
        var configuration = CreateConfiguration(provider.Endpoint, configured);
        var contributionRoot = Path.Combine(AppContext.BaseDirectory, "contributions");

        Directory.Exists(contributionRoot).Should().BeTrue(
            $"the test build must provide the default package payload at '{contributionRoot}'");
        var packagedIds = Directory.EnumerateDirectories(contributionRoot)
            .Select(Path.GetFileName)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.Ordinal);
        packagedIds.Should().NotContain(ArchivedRegistrationIds);
        packagedIds.Should().Contain("sharpclaw_providers_openai_compat");

        {
            var registrationSet = await PackagedDotNetRegistrationSet.LoadProductionAsync(
                         [contributionRoot],
                         configuration).ConfigureAwait(false);
            await using (registrationSet.ConfigureAwait(false))
            {
                registrationSet.SourceIds.Should().NotContain(ArchivedRegistrationIds);
                registrationSet.SourceIds.Should().Contain("sharpclaw_providers_openai_compat");

                var databaseOptions = new SharpClawPersistenceOptions
                {
                    ProviderKey = SharpClawPersistenceOptions.DefaultProviderKey,
                    DataDirectory = workspace.DatabaseDirectory,
                };

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

                var app = builder.Build();
                await using var appAsyncDisposal = app.ConfigureAwait(false);
                var readiness = app.Services.GetRequiredService<RuntimeReadinessState>();
                var adapter = app.Services.GetRequiredService<RuntimeKernelAdapter>();
                app.Services.GetRequiredService<IActionDispatcher>().Should().BeSameAs(adapter.ActionDispatcher);

                await app.Services.GetRequiredService<RuntimeDatabaseReadiness>().ValidateAsync().ConfigureAwait(false);
                await registrationSet.ConnectCapabilitiesAsync(app.Services).ConfigureAwait(false);
                await adapter.StartAsync("default-package-production-gate").ConfigureAwait(false);
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
                    await app.StartAsync().ConfigureAwait(false);
                    using var client = new HttpClient
                    {
                        BaseAddress = new Uri(app.Urls.Single()),
                        Timeout = TimeSpan.FromSeconds(30),
                    };
                    client.DefaultRequestHeaders.Add(
                        "X-Api-Key",
                        app.Services.GetRequiredService<ApiKeyProvider>().ApiKey);

                    foreach (var path in new[] { "/echo", "/healthz", "/readyz", "/ping" })
                    {
                        using var health = await client.GetAsync(path).ConfigureAwait(false);
                        health.StatusCode.Should().Be(HttpStatusCode.OK, path);
                    }
                    var setup = await client.GetFromJsonAsync<SharpClawProviderSetup>("/setup/provider").ConfigureAwait(false);
                    setup!.SetupRequired.Should().Be(!configured);
                    setup.Providers.Should().Contain(item => item.Key == "custom");
                    using var anonymous = new HttpClient { BaseAddress = client.BaseAddress };
                    using var unauthorized = await anonymous.GetAsync("/setup/provider").ConfigureAwait(false);
                    unauthorized.StatusCode.Should().Be(HttpStatusCode.Locked,
                        "the session-key middleware must still protect provider setup");

                    using var response = await client.PostAsJsonAsync("/chat", new { message = "default gate" }).ConfigureAwait(false);
                    var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                    if (!configured)
                    {
                        response.StatusCode.Should().Be(HttpStatusCode.Conflict, body);
                        body.Should().Contain("provider_setup_required");
                        using var blockedStream = await client.PostAsJsonAsync("/chat/stream", new { message = "stream" }).ConfigureAwait(false);
                        blockedStream.StatusCode.Should().Be(HttpStatusCode.Conflict);
                        provider.RequestCount.Should().Be(0);
                        readiness.IsReady.Should().BeTrue();
                    }
                    else
                    {
                        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
                        body.Should().Contain("default package graph response");
                        provider.RequestCount.Should().Be(1);
                    }
                }
                finally
                {
                    readiness.MarkNotReady();
                    try
                    {
                        await adapter.StopAsync().ConfigureAwait(false);
                    }
                    finally
                    {
                        await app.StopAsync().ConfigureAwait(false);
                    }
                }
            }
        }

        await AssertSidecarsStoppedAsync(initialSidecars).ConfigureAwait(false);
    }

    private static IConfiguration CreateConfiguration(string providerEndpoint, bool configured) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Provider:Key"] = configured ? "custom" : null,
                ["Provider:Model"] = configured ? "default-gate-model" : null,
                ["Provider:ApiKey"] = configured ? "default-gate-key" : null,
                ["Provider:Endpoint"] = configured ? providerEndpoint : null,
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
        Process.GetProcessesByName("SharpClaw.Runtime.Host")
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
            await Task.Delay(100).ConfigureAwait(false);
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
            await app.StartAsync().ConfigureAwait(false);
            return server;
        }

        public ValueTask DisposeAsync() => _app.DisposeAsync();
    }
}
