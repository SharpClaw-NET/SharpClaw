using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SharpClaw.Contracts.Kernel;
using SharpClaw.Contracts.Providers;
using SharpClaw.Core.Kernel;
using SharpClaw.Gateway;
using SharpClaw.Gateway.Infrastructure;
using SharpClaw.Runtime.BLL.Kernel;
using SharpClaw.Runtime.Host;
using SharpClaw.Runtime.Host.Api;
using SharpClaw.Shared.Instances;

namespace SharpClaw.Tests.Gateway;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812", Justification = "NUnit constructs this loopback security fixture.")]
[TestFixture]
internal sealed class GatewayPrivateConfigurationTests
{
    [TestCase("/api/env/core", false), TestCase("/api/env/core", true)]
    [TestCase("/api/internal/storage", false), TestCase("/api/internal/storage", true)]
    [TestCase("/api/configuration/options", false), TestCase("/api/configuration/options", true)]
    [TestCase("/api/control/shutdown", false), TestCase("/api/control/shutdown", true)]
    [TestCase("/api/diagnostics/logs", false), TestCase("/api/diagnostics/logs", true)]
    [TestCase("/api/bootstrap/start", false), TestCase("/api/bootstrap/start", true)]
    [TestCase("/api/remote/connect", false), TestCase("/api/remote/connect", true)]
    public async Task NormalGatewayDeniesPrivatePathsWithoutAttachingRuntimeCredentialsAsync(string path, bool authenticated)
    {
        var rig = await Rig.CreateAsync().ConfigureAwait(false);
        await using var disposal = rig.ConfigureAwait(false);
        using var client = rig.CreateClient(gateway: true, authenticated ? "member" : null);
        using var response = await client.GetAsync(new Uri(path, UriKind.Relative), TestContext.CurrentContext.CancellationToken).ConfigureAwait(false);
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        rig.RuntimeRequests.Should().BeEmpty();
        AssertNoSentinels(await response.Content.ReadAsStringAsync(TestContext.CurrentContext.CancellationToken).ConfigureAwait(false));
        using var publicResponse = await client.GetAsync(new Uri("/api/healthz", UriKind.Relative), TestContext.CurrentContext.CancellationToken).ConfigureAwait(false);
        publicResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        rig.RuntimeRequests.Should().Equal("/healthz");
    }

    [TestCase(null), TestCase("member"), TestCase("admin")]
    public async Task DirectRuntimeRequiresAdminAndNeverEnumeratesProtectedConfigurationAsync(string? role)
    {
        var rig = await Rig.CreateAsync().ConfigureAwait(false);
        await using var disposal = rig.ConfigureAwait(false);
        using var client = rig.CreateClient(gateway: false, role);
        // A bearer string alone never creates a trusted identity in either host.
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "unvalidated-admin-claim");
        using var response = await client.GetAsync(new Uri("/env/core", UriKind.Relative), TestContext.CurrentContext.CancellationToken).ConfigureAwait(false);
        response.StatusCode.Should().Be(string.Equals(role, "admin", StringComparison.Ordinal) ? HttpStatusCode.OK : HttpStatusCode.Forbidden);
        var body = await response.Content.ReadAsStringAsync(TestContext.CurrentContext.CancellationToken).ConfigureAwait(false);
        AssertNoSentinels(body);
        if (string.Equals(role, "admin", StringComparison.Ordinal))
        {
            using var json = JsonDocument.Parse(body);
            json.RootElement.EnumerateObject().Select(static item => item.Name)
                .Should().BeEquivalentTo("status", "providerConfigured", "modelConfigured");
            json.RootElement.GetProperty("status").GetString().Should().Be("available");
        }
    }

    private static void AssertNoSentinels(string body)
    {
        foreach (var value in Rig.Sentinels) body.Should().NotContain(value);
        body.Should().NotContain("ConnectionStrings").And.NotContain("ApiKey").And.NotContain("PrivateKey");
    }

    private sealed class Rig : IAsyncDisposable
    {
        internal static readonly string[] Sentinels = ["fixture-provider-secret", "fixture-connection-secret", "fixture-signing-secret"];
        private readonly string _root = Path.Combine(Path.GetTempPath(), "sharpclaw-private-gateway-" + Guid.NewGuid().ToString("N"));
        private WebApplication? _runtime;
        private WebApplication? _gateway;
        private ApiKeyProvider RuntimeKey => (_runtime ?? throw new InvalidOperationException("Runtime is unavailable."))
            .Services.GetRequiredService<ApiKeyProvider>();
        internal ConcurrentQueue<string> RuntimeRequests { get; } = new();

        internal static async Task<Rig> CreateAsync()
        {
            var rig = new Rig();
            try
            {
                rig._runtime = rig.BuildRuntime();
                await rig._runtime.StartAsync(TestContext.CurrentContext.CancellationToken).ConfigureAwait(false);
                rig._gateway = rig.BuildGateway();
                await rig._gateway.StartAsync(TestContext.CurrentContext.CancellationToken).ConfigureAwait(false);
                return rig;
            }
            catch { await rig.DisposeAsync().ConfigureAwait(false); throw; }
        }

        private static WebApplicationBuilder Builder()
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
            builder.Configuration.Sources.Clear();
            builder.Configuration.AddInMemoryCollection();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            return builder;
        }

        private WebApplication BuildRuntime()
        {
            var builder = Builder();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
            { ["Provider:ApiKey"] = Sentinels[0], ["ConnectionStrings:SQLite"] = Sentinels[1], ["Signing:PrivateKey"] = Sentinels[2] });
            var paths = new SharpClawInstancePaths(SharpClawInstanceKind.Backend, _root, _root, _root);
            builder.Services.AddSingleton(paths);
            builder.Services.AddSingleton<ApiKeyProvider>();
            builder.Services.AddSingleton<IRuntimeProviderClientFactory, NoProviders>();
            builder.Services.AddSingleton(services => new RuntimeKernelAdapter(builder.Configuration, services,
                new KernelJobsBindings(), [], paths, services.GetRequiredService<IRuntimeProviderClientFactory>()));
            builder.Services.AddSingleton(services => services.GetRequiredService<RuntimeKernelAdapter>().Kernel);
            builder.Services.AddSingleton<RuntimeReadinessState>();
            var app = _runtime = builder.Build();
            _ = app.Services.GetRequiredService<ApiKeyProvider>();
            app.Use(ApplyFixtureIdentityAsync);
            app.Use(async (HttpContext context, RequestDelegate next) =>
            {
                RuntimeRequests.Enqueue(context.Request.Path.Value ?? "/");
                await next(context).ConfigureAwait(false);
            });
            app.UseMiddleware<ApiKeyMiddleware>();
            KernelHostEndpoints.Map(app);
            return app;
        }

        private WebApplication BuildGateway()
        {
            var runtime = _runtime ?? throw new InvalidOperationException("Runtime has not started.");
            var key = RuntimeKey;
            var builder = Builder();
            builder.Services.AddHttpContextAccessor();
            builder.Services.AddSingleton(Options.Create(new InternalApiOptions
            { BaseUrl = Address(runtime).ToString(), ApiKey = key.ApiKey, GatewayToken = key.GatewayToken }));
            builder.Services.AddHttpClient<InternalApiClient>(client => client.BaseAddress = Address(runtime))
                .ConfigurePrimaryHttpMessageHandler(static () => new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false });
            builder.Services.AddSingleton(CreateGatewayGraph());
            builder.Services.AddSingleton(services => new KernelActionDispatcher(services.GetRequiredService<KernelGraph>(),
                new KernelActionExecutionContext(RequestPrincipal.Anonymous, ExtensionFeatureSet.Empty, Guid.NewGuid(), Guid.NewGuid())));
            builder.Services.AddSingleton<GatewayBackgroundActionBoundary>();
            var app = _gateway = builder.Build();
            app.Use(ApplyFixtureIdentityAsync);
            app.UseMiddleware<GatewayActionMiddleware>();
            app.MapGatewayProxyEndpoints();
            return app;
        }

        private static Task ApplyFixtureIdentityAsync(HttpContext context, RequestDelegate next)
        {
            var role = context.Request.Headers["X-Fixture-Identity"].ToString();
            if (string.Equals(role, "member", StringComparison.Ordinal) || string.Equals(role, "admin", StringComparison.Ordinal))
                context.User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ClaimTypes.NameIdentifier, "fixture-user"), new Claim(ClaimTypes.Role, role)], "fixture"));
            return next(context);
        }

        internal HttpClient CreateClient(bool gateway, string? role)
        {
            var app = (gateway ? _gateway : _runtime) ?? throw new InvalidOperationException("Host is unavailable.");
            var client = new HttpClient { BaseAddress = Address(app) };
            try
            {
                if (role is not null) client.DefaultRequestHeaders.Add("X-Fixture-Identity", role);
                if (!gateway) client.DefaultRequestHeaders.Add("X-Api-Key", RuntimeKey.ApiKey);
                return client;
            }
            catch { client.Dispose(); throw; }
        }
        private static Uri Address(WebApplication app)
        {
            var urls = app.Urls.ToArray();
            if (urls.Length != 1) throw new InvalidOperationException("Fixture requires exactly one listener.");
            return new Uri(urls[0]);
        }
        public async ValueTask DisposeAsync()
        {
            if (_gateway is not null) await _gateway.DisposeAsync().ConfigureAwait(false);
            if (_runtime is not null) await _runtime.DisposeAsync().ConfigureAwait(false);
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
    }

    private static KernelGraph CreateGatewayGraph()
    {
        var keys = GatewayActionManifest.Published;
        var grants = keys.ToDictionary(static key => key.Value,
            static _ => ActionInterceptionCapabilities.Inspect | ActionInterceptionCapabilities.Wrap, StringComparer.Ordinal);
        return TestServiceGraph.Compile([new GatewayRegistration()], new KernelGraphCompileOptions
        {
            ActionRegistrationCapabilityGrants = new Dictionary<string, IReadOnlyDictionary<string, ActionInterceptionCapabilities>>(StringComparer.Ordinal)
            { ["private-ingress-test"] = grants },
            SensitiveActionApprovals = keys.Where(key => KernelActionCatalog.DescriptorFor(key).ContainsSensitiveData).Select(key =>
            {
                var descriptor = KernelActionCatalog.DescriptorFor(key).ToDescriptor();
                var types = KernelSchemaIdentity.ActionTypes(descriptor, typeof(KernelActionEnvelope), typeof(object));
                return new KernelSensitiveActionApproval("private-ingress-test", key, descriptor.Version,
                    types.ActionType.AssemblyQualifiedName ?? throw new InvalidOperationException("Action type has no identity."),
                    types.ResultType.AssemblyQualifiedName ?? throw new InvalidOperationException("Result type has no identity."),
                    KernelSchemaIdentity.Action(descriptor));
            }).ToArray(),
        });
    }
    private sealed class GatewayRegistration : ISharpClawModule
    {
        public ModuleIdentity Identity { get; } = new("private-ingress-test", "Private ingress test", "test");
        public void ConfigureServices(IServiceCollection services)
        {
            services.AddSingleton<GatewayInterceptor>();
            foreach (var key in GatewayActionManifest.Published)
                services.OnAction(key).Use<GatewayInterceptor>(new HookOrdering("private-ingress-test"));
        }
    }
    private sealed class GatewayInterceptor : IActionInterceptor<KernelActionEnvelope, object>
    {
        public ValueTask<IActionOutcome<object>> InvokeAsync(ActionContext<KernelActionEnvelope> context,
            IActionControl<KernelActionEnvelope, object> control, CancellationToken cancellationToken) => control.ProceedAsync(cancellationToken);
    }
    private sealed class NoProviders : IRuntimeProviderClientFactory
    {
        public IProviderApiClient Create(IConfiguration configuration, IReadOnlyList<IProviderPlugin> plugins,
            string providerKey) => throw new AssertionException("Private status controls must not construct a provider client.");
    }
}
