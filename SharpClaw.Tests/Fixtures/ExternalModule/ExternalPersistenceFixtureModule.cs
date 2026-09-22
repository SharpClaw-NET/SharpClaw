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

public sealed class ExternalPersistenceFixtureProvider : ISharpClawPersistenceProvider
{
    public string Key => ExternalPersistenceFixtureModule.ProviderKey;
    public IReadOnlyCollection<string> Aliases { get; } = [];
    public bool IsRelational => false;

    public void Configure(
        DbContextOptionsBuilder optionsBuilder,
        SharpClawPersistenceProviderContext context) =>
        optionsBuilder.UseInMemoryDatabase("external-persistence-package");
}
