using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.ModuleSDK;
using SharpClaw.Persistence;

namespace SharpClaw.TestFixtures.ExternalRegistration;


public sealed class ExternalPersistenceFixtureProvider : ISharpClawPersistenceProvider, IDisposable
{
    private readonly ServiceProvider _efServices = new ServiceCollection()
        .AddEntityFrameworkInMemoryDatabase()
        .BuildServiceProvider();

    public string Key => ExternalPersistenceFixtureModule.ProviderKey;
    public IReadOnlyCollection<string> Aliases { get; } = [];
    public bool IsRelational => false;

    public void Configure(
        DbContextOptionsBuilder optionsBuilder,
        SharpClawPersistenceProviderContext context) =>
        // Own this reloadable fixture's EF services instead of retaining its
        // module-local types in EF's process-global service-provider cache.
        optionsBuilder.UseInMemoryDatabase("external-persistence-package")
            .UseLoggerFactory(null)
            .UseInternalServiceProvider(_efServices);

    public void Dispose()
    {
        _efServices.Dispose();
        GC.SuppressFinalize(this);
    }
}
