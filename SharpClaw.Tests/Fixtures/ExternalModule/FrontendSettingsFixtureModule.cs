using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.Kernel;
using SharpClaw.ModuleSDK;

namespace SharpClaw.TestFixtures.ExternalRegistration;

public sealed class FrontendSettingsFixtureModule : ISharpClawModule
{
    public const string SourceId = "synthetic_frontend_settings";
    public ModuleIdentity Identity { get; } = new(SourceId, "External frontend settings", "sfs");
    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<SettingsFixtureState>();
        services.AddHttpEndpoint<SettingsFixtureEndpoint>(new("fixture.settings.read", "/fixture/settings", "GET", HostEndpointTransport.Http));
        services.AddHttpEndpoint<SettingsFixtureEndpoint>(new("fixture.settings.save", "/fixture/settings", "POST", HostEndpointTransport.Http));
    }
}
