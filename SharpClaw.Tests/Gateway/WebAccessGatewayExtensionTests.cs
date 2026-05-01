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
using SharpClaw.Modules.WebAccess.Gateway;

namespace SharpClaw.Tests.Gateway;

/// <summary>
/// Phase 4 verification: the WebAccess gateway extension contributes a
/// search-engine endpoint group, the catalog records its prefix, the gate
/// passes enabled traffic through to the mapped routes, and toggling either
/// the module or the group off causes the gate to refuse traffic with 503.
/// </summary>
[TestFixture]
public sealed class WebAccessGatewayExtensionTests
{
    private static WebApplication BuildApp(Dictionary<string, string?> config)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(config);

        builder.Services.Configure<GatewayEndpointOptions>(
            builder.Configuration.GetSection(GatewayEndpointOptions.SectionName));
        builder.Services.Configure<GatewayModuleOptions>(
            builder.Configuration.GetSection(GatewayModuleOptions.SectionName));
        builder.Services.AddSingleton(
            GatewayModuleLoader.FromExtensions([new WebAccessGatewayExtension()]));
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

    private static Dictionary<string, string?> EnabledConfig() => new()
    {
        ["Gateway:Endpoints:Enabled"] = "true",
        ["Gateway:Modules:Modules:webaccess"] = "true",
        ["Gateway:Modules:Groups:webaccess/searchengines"] = "true",
    };

    private static EndpointGateMiddleware CreateGate(WebApplication app, RequestDelegate next)
        => new(next,
            app.Services.GetRequiredService<IOptionsMonitor<GatewayEndpointOptions>>(),
            app.Services.GetRequiredService<GatewayEndpointGroupCatalog>(),
            NullLogger<EndpointGateMiddleware>.Instance);

    private static HttpContext NewContext(IServiceProvider services, string path, string method = "GET")
    {
        var ctx = new DefaultHttpContext { RequestServices = services };
        ctx.Request.Path = path;
        ctx.Request.Method = method;
        ctx.Response.Body = new MemoryStream();
        return ctx;
    }

    [Test]
    public void Extension_DeclaresWebAccessSearchEnginesGroup()
    {
        var ext = new WebAccessGatewayExtension();

        ext.ModuleId.Should().Be("webaccess");
        var groups = ext.GetEndpointGroups();
        groups.Should().ContainSingle();
        groups[0].GroupId.Should().Be("searchengines");
        groups[0].DefaultEnabled.Should().BeFalse(
            because: "Phase 4 mandates a two-step opt-in — disabled by default.");
    }

    [Test]
    public void EnabledGroup_RegistersUnderModulesPrefix()
    {
        using var app = BuildApp(EnabledConfig());
        var catalog = app.Services.GetRequiredService<GatewayEndpointGroupCatalog>();

        var match = catalog.Resolve("/api/modules/webaccess/searchengines");
        match.Should().NotBeNull();
        match!.ModuleId.Should().Be("webaccess");
        match.Group.GroupId.Should().Be("searchengines");
    }

    [Test]
    public async Task EnabledGroup_PassesGate()
    {
        using var app = BuildApp(EnabledConfig());
        var nextCalled = false;
        var gate = CreateGate(app, _ => { nextCalled = true; return Task.CompletedTask; });

        var ctx = NewContext(app.Services, "/api/modules/webaccess/searchengines");
        await gate.InvokeAsync(ctx);

        nextCalled.Should().BeTrue();
    }

    [Test]
    public async Task DisabledGroup_GateReturns503()
    {
        using var app = BuildApp(EnabledConfig());
        var monitor = app.Services.GetRequiredService<IOptionsMonitor<GatewayModuleOptions>>();
        monitor.CurrentValue.Groups["webaccess/searchengines"] = false;

        var nextCalled = false;
        var gate = CreateGate(app, _ => { nextCalled = true; return Task.CompletedTask; });

        var ctx = NewContext(app.Services, "/api/modules/webaccess/searchengines/abc");
        await gate.InvokeAsync(ctx);

        nextCalled.Should().BeFalse();
        ctx.Response.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
    }

    [Test]
    public void DisabledModule_DoesNotMapAnyGroup()
    {
        var config = new Dictionary<string, string?>
        {
            ["Gateway:Endpoints:Enabled"] = "true",
            ["Gateway:Modules:Modules:webaccess"] = "false",
            ["Gateway:Modules:Groups:webaccess/searchengines"] = "true",
        };

        using var app = BuildApp(config);
        var catalog = app.Services.GetRequiredService<GatewayEndpointGroupCatalog>();

        catalog.All.Should().BeEmpty();
    }
}
