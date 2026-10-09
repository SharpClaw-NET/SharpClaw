using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.Kernel;
using SharpClaw.ModuleSDK;

namespace SharpClaw.TestFixtures.ExternalRegistration;


public sealed class EmptyFrontendSettingsFixtureModule : ISharpClawModule
{
    public ModuleIdentity Identity { get; } = new("empty_frontend_settings", "Empty frontend settings", "efs");
    public void ConfigureServices(IServiceCollection services) { }
}
