using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using SharpClaw.Gateway.Abstractions;
using SharpClaw.Gateway.Configuration;
using SharpClaw.Gateway.Modules;
using SharpClaw.Gateway.Security;

namespace SharpClaw.Tests.Gateway;

/// <summary>
/// Phase 3 verification of module endpoint mapping and the catalog-aware
/// gateway pipeline. The tests exercise the loader, catalog,
/// <see cref="GatewayModuleEndpointMapping"/>, and
/// <see cref="EndpointGateMiddleware"/> directly so no extra ASP.NET test
/// packages are pulled into the solution. The synthetic
/// <see cref="IGatewayModuleExtension"/> below exists only to drive these
/// assertions; it never reaches a real network listener.
/// </summary>
[TestFixture]
public sealed class SyntheticGatewayModuleTests
{
    private sealed class SyntheticExtension : IGatewayModuleExtension
    {
        public string ModuleId => "test";
        public string DisplayName => "Synthetic Test Module";
        public IReadOnlyList<GatewayEndpointGroup> GetEndpointGroups() =>
        [
            new GatewayEndpointGroup(
                GroupId: "ping",
                DisplayName: "Synthetic Ping Group",
                RateLimitPolicy: RateLimiterConfiguration.GlobalPolicy)
        ];

        public bool MapCalled { get; private set; }

        public void MapEndpoints(IGatewayEndpointGroupBuilder builder)
        {
            MapCalled = true;
            builder.MapGet("/", () => Results.Ok(new { ok = true }));
        }
    }

    private sealed class ThrowingExtension : IGatewayModuleExtension
    {
        public string ModuleId => "boom";
        public string DisplayName => "Throwing Module";
        public IReadOnlyList<GatewayEndpointGroup> GetEndpointGroups() =>
        [
            new GatewayEndpointGroup("explode", "Explode")
        ];

        public void MapEndpoints(IGatewayEndpointGroupBuilder builder)
            => throw new InvalidOperationException("synthetic boom");
    }

    private static WebApplication BuildApp(
        IGatewayModuleExtension extension,
        Dictionary<string, string?> config)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(config);

        builder.Services.Configure<GatewayEndpointOptions>(
            builder.Configuration.GetSection(GatewayEndpointOptions.SectionName));
        builder.Services.Configure<GatewayModuleOptions>(
            builder.Configuration.GetSection(GatewayModuleOptions.SectionName));
        builder.Services.AddSingleton(GatewayModuleLoader.FromExtensions([extension]));
        builder.Services.AddSingleton<GatewayEndpointGroupCatalog>();
        builder.Services.AddSingleton<IpBanService>();
        builder.Services.AddSharpClawRateLimiting();
        builder.Services.AddRouting();

        var app = builder.Build();
        app.UseRouting();
        app.UseRateLimiter();
        ((Microsoft.AspNetCore.Builder.IApplicationBuilder)app).Properties[
            GatewayModuleEndpointMapping.RateLimiterReadyKey] = true;
        app.MapGatewayModuleEndpoints();
        return app;
    }

    private static Dictionary<string, string?> EnabledConfig(string moduleId, string groupId)
        => new()
        {
            ["Gateway:Endpoints:Enabled"] = "true",
            [$"Gateway:Modules:Modules:{moduleId}"] = "true",
            [$"Gateway:Modules:Groups:{moduleId}/{groupId}"] = "true",
        };

    private static EndpointGateMiddleware CreateGate(WebApplication app, RequestDelegate next)
    {
        var optionsMonitor = app.Services.GetRequiredService<IOptionsMonitor<GatewayEndpointOptions>>();
        var catalog = app.Services.GetRequiredService<GatewayEndpointGroupCatalog>();
        return new EndpointGateMiddleware(next, optionsMonitor, catalog,
            NullLogger<EndpointGateMiddleware>.Instance);
    }

    private static HttpContext NewContext(IServiceProvider services, string path)
    {
        var ctx = new DefaultHttpContext { RequestServices = services };
        ctx.Request.Path = path;
        ctx.Request.Method = "GET";
        ctx.Response.Body = new MemoryStream();
        return ctx;
    }

    [Test]
    public void EnabledGroup_IsRegisteredInCatalog()
    {
        var ext = new SyntheticExtension();
        using var app = BuildApp(ext, EnabledConfig("test", "ping"));

        ext.MapCalled.Should().BeTrue();

        var catalog = app.Services.GetRequiredService<GatewayEndpointGroupCatalog>();
        var match = catalog.Resolve("/api/modules/test/ping");
        match.Should().NotBeNull();
        match!.ModuleId.Should().Be("test");
        match.Group.GroupId.Should().Be("ping");
    }

    [Test]
    public async Task EnabledGroup_PassesGate()
    {
        using var app = BuildApp(new SyntheticExtension(), EnabledConfig("test", "ping"));
        var nextCalled = false;
        var gate = CreateGate(app, _ => { nextCalled = true; return Task.CompletedTask; });

        var ctx = NewContext(app.Services, "/api/modules/test/ping");
        await gate.InvokeAsync(ctx);

        nextCalled.Should().BeTrue();
        ctx.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
    }

    [Test]
    public async Task DisabledGroup_GateReturns503()
    {
        using var app = BuildApp(new SyntheticExtension(), EnabledConfig("test", "ping"));
        var monitor = app.Services.GetRequiredService<IOptionsMonitor<GatewayModuleOptions>>();
        monitor.CurrentValue.Groups["test/ping"] = false;

        var nextCalled = false;
        var gate = CreateGate(app, _ => { nextCalled = true; return Task.CompletedTask; });

        var ctx = NewContext(app.Services, "/api/modules/test/ping");
        await gate.InvokeAsync(ctx);

        nextCalled.Should().BeFalse();
        ctx.Response.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
    }

    [Test]
    public async Task UnmappedModulePath_GateReturns404()
    {
        using var app = BuildApp(new SyntheticExtension(), EnabledConfig("test", "ping"));
        var nextCalled = false;
        var gate = CreateGate(app, _ => { nextCalled = true; return Task.CompletedTask; });

        var ctx = NewContext(app.Services, "/api/modules/test/notmapped");
        await gate.InvokeAsync(ctx);

        nextCalled.Should().BeFalse();
        ctx.Response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
    }

    [Test]
    public void ThrowingModule_IsUnregisteredFromCatalog()
    {
        var config = new Dictionary<string, string?>
        {
            ["Gateway:Endpoints:Enabled"] = "true",
            ["Gateway:Modules:Modules:boom"] = "true",
            ["Gateway:Modules:Groups:boom/explode"] = "true",
        };

        using var app = BuildApp(new ThrowingExtension(), config);
        var catalog = app.Services.GetRequiredService<GatewayEndpointGroupCatalog>();

        catalog.All.Should().BeEmpty(
            because: "a module that throws during MapEndpoints must be unregistered.");
    }

    [Test]
    public void DisabledModule_IsNotMapped()
    {
        var config = new Dictionary<string, string?>
        {
            ["Gateway:Endpoints:Enabled"] = "true",
            ["Gateway:Modules:Modules:test"] = "false",
            ["Gateway:Modules:Groups:test/ping"] = "true",
        };

        var ext = new SyntheticExtension();
        using var app = BuildApp(ext, config);

        ext.MapCalled.Should().BeFalse();
        var catalog = app.Services.GetRequiredService<GatewayEndpointGroupCatalog>();
        catalog.All.Should().BeEmpty();
    }

    [Test]
    public void RateLimit_GlobalPolicy_ReportedAs60()
    {
        var optionsMonitor = new TestOptionsMonitor<GatewayModuleOptions>(new GatewayModuleOptions
        {
            Modules = { ["test"] = true },
            Groups = { ["test/ping"] = true },
        });
        var catalog = new GatewayEndpointGroupCatalog(optionsMonitor);
        catalog.TryRegister("test", new GatewayEndpointGroup(
            "ping", "Synthetic Ping Group",
            RateLimitPolicy: RateLimiterConfiguration.GlobalPolicy)).Should().BeTrue();

        RateLimiterConfiguration
            .ResolveRateLimit("/api/modules/test/ping", catalog)
            .Should().Be(60);
    }

    [Test]
    public void MapGatewayModuleEndpoints_BeforeUseRateLimiter_Throws()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(EnabledConfig("test", "ping"));
        builder.Services.Configure<GatewayEndpointOptions>(
            builder.Configuration.GetSection(GatewayEndpointOptions.SectionName));
        builder.Services.Configure<GatewayModuleOptions>(
            builder.Configuration.GetSection(GatewayModuleOptions.SectionName));
        builder.Services.AddSingleton(GatewayModuleLoader.FromExtensions([new SyntheticExtension()]));
        builder.Services.AddSingleton<GatewayEndpointGroupCatalog>();
        builder.Services.AddSingleton<IpBanService>();
        builder.Services.AddSharpClawRateLimiting();
        builder.Services.AddRouting();

        using var app = builder.Build();

        Action act = () => app.MapGatewayModuleEndpoints();
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*after app.UseRateLimiter()*");
    }

    private sealed class TestOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
