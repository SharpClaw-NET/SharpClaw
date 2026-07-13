using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using SharpClaw.Gateway.Contracts;
using SharpClaw.Gateway.Configuration;
using SharpClaw.Gateway.Modules;
using SharpClaw.Gateway.Modules.Hosting;
using SharpClaw.Gateway.Modules.Routing;
using SharpClaw.Gateway.Security;

namespace SharpClaw.Tests.Gateway.HotReload;

/// <summary>
/// Phase 5a verification: <see cref="GatewayModuleHostManager"/> drives the
/// dynamic <see cref="ModuleEndpointDataSource"/> and the
/// <see cref="GatewayEndpointGroupCatalog"/> through enable/disable/reload
/// transitions without restarting the host process. ALC unload is out of
/// scope for 5a; these tests cover only the routing/catalog half of the
/// hot-reload contract.
/// </summary>
[TestFixture]
public sealed class HotReloadHostManagerTests
{
    private sealed class ToggleableExtension : IGatewayModuleExtension
    {
        public string ModuleId { get; }
        public string DisplayName => $"Toggleable {ModuleId}";
        public int MapCallCount { get; private set; }

        public ToggleableExtension(string moduleId) => ModuleId = moduleId;

        public IReadOnlyList<GatewayEndpointGroup> GetEndpointGroups() =>
        [
            new GatewayEndpointGroup("ping", "Ping",
                RateLimitPolicy: RateLimiterConfiguration.GlobalPolicy)
        ];

        public void MapEndpoints(IGatewayEndpointGroupBuilder builder)
        {
            MapCallCount++;
            builder.MapGet("/", () => Results.Ok(new { ok = true }));
        }
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
        builder.Services.AddSingleton<ModuleEndpointDataSource>();
        builder.Services.AddSingleton<GatewayModuleHostManager>();
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

    private static Dictionary<string, string?> EnabledConfig(string moduleId, bool moduleOn = true, bool groupOn = true, bool hotReload = true)
        => new()
        {
            ["Gateway:Endpoints:Enabled"] = "true",
            [$"Gateway:Modules:Modules:{moduleId}"] = moduleOn ? "true" : "false",
            [$"Gateway:Modules:Groups:{moduleId}/ping"] = groupOn ? "true" : "false",
            ["Gateway:Modules:HotReloadEnabled"] = hotReload ? "true" : "false",
        };

    [Test]
    public async Task EnableAsync_OnDisabledModule_RegistersInDataSourceAndCatalog()
    {
        var ext = new ToggleableExtension("alpha");
        using var app = BuildApp(ext, EnabledConfig("alpha", moduleOn: false, groupOn: true));

        var manager = app.Services.GetRequiredService<GatewayModuleHostManager>();
        var catalog = app.Services.GetRequiredService<GatewayEndpointGroupCatalog>();
        var dataSource = app.Services.GetRequiredService<ModuleEndpointDataSource>();

        ext.MapCallCount.Should().Be(0);
        catalog.Resolve("/api/modules/alpha/ping").Should().BeNull();

        var monitor = app.Services.GetRequiredService<IOptionsMonitor<GatewayModuleOptions>>();
        monitor.CurrentValue.Modules["alpha"] = true;

        var result = await manager.EnableAsync("alpha");

        result.Should().Be(ModuleHostChange.Enabled);
        ext.MapCallCount.Should().Be(1);
        catalog.Resolve("/api/modules/alpha/ping").Should().NotBeNull();
        dataSource.GetEndpointsFor("alpha").Should().NotBeEmpty();
    }

    [Test]
    public async Task DisableAsync_RemovesEndpointsAndCatalogEntries()
    {
        var ext = new ToggleableExtension("beta");
        using var app = BuildApp(ext, EnabledConfig("beta"));
        var manager = app.Services.GetRequiredService<GatewayModuleHostManager>();
        var catalog = app.Services.GetRequiredService<GatewayEndpointGroupCatalog>();
        var dataSource = app.Services.GetRequiredService<ModuleEndpointDataSource>();

        catalog.Resolve("/api/modules/beta/ping").Should().NotBeNull();

        var result = await manager.DisableAsync("beta");

        result.Should().Be(ModuleHostChange.Disabled);
        catalog.Resolve("/api/modules/beta/ping").Should().BeNull();
        dataSource.GetEndpointsFor("beta").Should().BeEmpty();
    }

    [Test]
    public async Task ReloadAsync_RebuildsHostAndIncrementsMapCount()
    {
        var ext = new ToggleableExtension("gamma");
        using var app = BuildApp(ext, EnabledConfig("gamma"));
        var manager = app.Services.GetRequiredService<GatewayModuleHostManager>();

        ext.MapCallCount.Should().Be(1, "initial mapping ran during MapGatewayModuleEndpoints");

        var result = await manager.ReloadAsync("gamma");

        result.Should().Be(ModuleHostChange.Reloaded);
        ext.MapCallCount.Should().Be(2);
    }

    [Test]
    public async Task EnableAsync_OnUnknownModule_ReturnsNotFound()
    {
        var ext = new ToggleableExtension("known");
        using var app = BuildApp(ext, EnabledConfig("known"));
        var manager = app.Services.GetRequiredService<GatewayModuleHostManager>();

        var monitor = app.Services.GetRequiredService<IOptionsMonitor<GatewayModuleOptions>>();
        monitor.CurrentValue.Modules["ghost"] = true;

        var result = await manager.EnableAsync("ghost");
        result.Should().Be(ModuleHostChange.NotFound);
    }

    [Test]
    public async Task ConcurrentEnableDisable_SerializesWithoutDeadlock()
    {
        var ext = new ToggleableExtension("delta");
        using var app = BuildApp(ext, EnabledConfig("delta", moduleOn: false));

        var manager = app.Services.GetRequiredService<GatewayModuleHostManager>();
        var monitor = app.Services.GetRequiredService<IOptionsMonitor<GatewayModuleOptions>>();
        monitor.CurrentValue.Modules["delta"] = true;

        var tasks = new List<Task>();
        for (int i = 0; i < 8; i++)
        {
            tasks.Add(Task.Run(() => manager.EnableAsync("delta")));
            tasks.Add(Task.Run(() => manager.DisableAsync("delta")));
        }

        var completed = Task.WhenAll(tasks);
        var firstFinished = await Task.WhenAny(completed, Task.Delay(TimeSpan.FromSeconds(10)));

        firstFinished.Should().BeSameAs(completed,
            because: "operations must serialize on the manager's lock without deadlocking.");
    }

    [Test]
    public async Task DataSource_ChangeToken_FiresOnEnableAndDisable()
    {
        var ext = new ToggleableExtension("epsilon");
        using var app = BuildApp(ext, EnabledConfig("epsilon", moduleOn: false));
        var manager = app.Services.GetRequiredService<GatewayModuleHostManager>();
        var dataSource = app.Services.GetRequiredService<ModuleEndpointDataSource>();
        var monitor = app.Services.GetRequiredService<IOptionsMonitor<GatewayModuleOptions>>();
        monitor.CurrentValue.Modules["epsilon"] = true;

        var tokenBeforeEnable = dataSource.GetChangeToken();
        await manager.EnableAsync("epsilon");
        tokenBeforeEnable.HasChanged.Should().BeTrue();

        var tokenBeforeDisable = dataSource.GetChangeToken();
        await manager.DisableAsync("epsilon");
        tokenBeforeDisable.HasChanged.Should().BeTrue();
    }
}
