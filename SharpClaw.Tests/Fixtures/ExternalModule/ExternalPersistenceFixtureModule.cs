using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.ModuleSDK;
using SharpClaw.Persistence;

namespace SharpClaw.TestFixtures.ExternalRegistration;

public sealed class ExternalPersistenceFixtureModule : ISharpClawModule
{
    public const string SourceId = "synthetic_external_persistence";
    public const string ProviderKey = "ExternalInMemory";

    public ModuleIdentity Identity { get; } = new(
        SourceId,
        "Synthetic external persistence",
        "sep");

    public void ConfigureServices(IServiceCollection services) =>
        services.AddSingleton<ISharpClawPersistenceProvider, ExternalPersistenceFixtureProvider>();
}
