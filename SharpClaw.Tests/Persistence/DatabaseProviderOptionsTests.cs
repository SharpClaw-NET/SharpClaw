using System.Reflection;
using System.Runtime.Loader;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.Persistence;
using SharpClaw.Persistence;
using SharpClaw.Persistence.JSONColdStore;
using SharpClaw.Persistence.PostgreSQL;
using SharpClaw.Persistence.SQLite;
using SharpClaw.Persistence.SQLServer;
using SharpClaw.Runtime.Host;
using SharpClaw.Runtime.INF;
using SharpClaw.Runtime.INF.Persistence;
using SharpClaw.Runtime.INF.Persistence.Registrations;

namespace SharpClaw.Tests.Persistence;

[TestFixture]
public sealed class DatabaseProviderOptionsTests
{
    [Test]
    public void FromConfiguration_PreservesAnArbitraryModuleProviderKey()
    {
        var configuration = Configuration(
            ("Database:Provider", "FutureVectorStore"),
            ("Database:EnableDetailedErrors", "false"),
            ("Database:EnableSensitiveDataLogging", "true"));

        var options = SharpClawPersistenceOptions.FromConfiguration(
            configuration,
            Path.Combine(Path.GetTempPath(), "sharpclaw-options"));

        options.ProviderKey.Should().Be("FutureVectorStore");
        options.EnableDetailedErrors.Should().BeFalse();
        options.EnableSensitiveDataLogging.Should().BeTrue();
    }

    [Test]
    public void AddInfrastructure_FailsClearlyWhenSelectedModuleIsNotInstalled()
    {
        var configuration = Configuration(("Database:Provider", "MissingStore"));
        var services = new ServiceCollection();
        services.AddInfrastructure(
            configuration,
            SharpClawPersistenceOptions.FromConfiguration(configuration));
        using var serviceProvider = services.BuildServiceProvider();

        var act = () => serviceProvider.GetRequiredService<PersistenceProviderSelection>();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*MissingStore*not installed*");
    }

    [TestCase("SQLite", "Data Source=:memory:", "Microsoft.EntityFrameworkCore.Sqlite")]
    [TestCase("PostgreSQL", "Host=localhost;Database=sharpclaw;Username=test;Password=test", "Npgsql.EntityFrameworkCore.PostgreSQL")]
    [TestCase("SQLServer", "Server=localhost;Database=sharpclaw;User Id=test;Password=test;TrustServerCertificate=True", "Microsoft.EntityFrameworkCore.SqlServer")]
    public void AddInfrastructure_UsesOnlyTheSelectedModuleProvider(
        string providerKey,
        string connectionString,
        string expectedProvider)
    {
        using var serviceProvider = BuildProvider(
            providerKey,
            ("ConnectionStrings:" + providerKey, connectionString));
        using var scope = serviceProvider.CreateScope();

        var dbContext = scope.ServiceProvider.GetRequiredService<SharpClawDbContext>();

        dbContext.Database.ProviderName.Should().Be(expectedProvider);
        serviceProvider.GetRequiredService<PersistenceProviderSelection>()
            .Provider.Key.Should().Be(providerKey);
    }

    [TestCase("PostgreSQL", "ConnectionStrings:PostgreSQL", "Host=localhost;Database=sharpclaw;Username=test;Password=test", "20260920114914_InitialCreate")]
    [TestCase("SQLServer", "ConnectionStrings:SQLServer", "Server=localhost;Database=sharpclaw;User Id=test;Password=test;TrustServerCertificate=True", "20260920114918_InitialCreate")]
    [TestCase("SQLite", "ConnectionStrings:SQLite", "Data Source=:memory:", "20260920114922_InitialCreate")]
    public void RelationalModule_ContributesItsOwnOfficialMigration(
        string providerKey,
        string connectionStringKey,
        string connectionString,
        string expectedMigration)
    {
        using var serviceProvider = BuildProvider(
            providerKey,
            (connectionStringKey, connectionString));
        using var scope = serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<SharpClawDbContext>();

        dbContext.Database.GetMigrations().Should().ContainSingle().Which.Should().Be(expectedMigration);
        dbContext.Database.HasPendingModelChanges().Should().BeFalse();
    }

    [Test]
    public void ProviderAlias_PreservesExistingPostgresConfiguration()
    {
        using var serviceProvider = BuildProvider(
            "Postgres",
            ("ConnectionStrings:Postgres", "Host=localhost;Database=sharpclaw;Username=test;Password=test"));

        serviceProvider.GetRequiredService<PersistenceProviderSelection>()
            .Provider.Should().BeOfType<PostgreSQLPersistenceProvider>();
    }

    [Test]
    public void NewProviderModule_RequiresNoHostSwitchOrEnumChange()
    {
        var configuration = Configuration(("Database:Provider", "FutureVectorStore"));
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ISharpClawPersistenceProvider, FuturePersistenceProvider>();
        services.AddInfrastructure(
            configuration,
            SharpClawPersistenceOptions.FromConfiguration(configuration));
        using var serviceProvider = services.BuildServiceProvider();
        using var scope = serviceProvider.CreateScope();

        scope.ServiceProvider.GetRequiredService<SharpClawDbContext>()
            .Database.ProviderName.Should().Be("Microsoft.EntityFrameworkCore.InMemory");
    }

    [Test]
    public void RegistrationDbContextFactory_DelegatesOwnedContextConfigurationToSelectedModule()
    {
        using var serviceProvider = BuildProvider(
            "SQLite",
            ("ConnectionStrings:SQLite", "Data Source=:memory:"),
            ("Database:SQLite:CommandTimeoutSeconds", "17"));
        var registry = serviceProvider.GetRequiredService<RuntimeRegistrationDbContextRegistry>();
        registry.Register(new RuntimeRegistrationDbContextRegistration(
            "test_registration",
            typeof(ConfiguredRegistrationDbContext),
            [typeof(ConfiguredRegistrationEntity)]));

        var factory = serviceProvider.GetRequiredService<IOwnedDbContextFactory>();
        using var dbContext = (ConfiguredRegistrationDbContext)factory.CreateDbContext(
            typeof(ConfiguredRegistrationDbContext));

        dbContext.Database.ProviderName.Should().Be("Microsoft.EntityFrameworkCore.Sqlite");
        dbContext.Database.GetCommandTimeout().Should().Be(17);
        dbContext.Database.GetMigrations().Should().BeEmpty();
    }

    [Test]
    public void PackagedSQLServerProvider_KeepsProviderDependenciesInItsModuleLoadContext()
    {
        using var registrations = PackagedDotNetRegistrationSet.Load(
            Path.Combine(AppContext.BaseDirectory, "contributions"),
            Configuration());
        IServiceCollection services = new ServiceCollection();
        foreach (var descriptor in registrations.Services.Where(descriptor =>
                     descriptor.ServiceType == typeof(ISharpClawPersistenceProvider)))
        {
            services.Add(descriptor);
        }

        using var serviceProvider = services.BuildServiceProvider();
        var provider = serviceProvider.GetServices<ISharpClawPersistenceProvider>()
            .Single(candidate => candidate.Key == "SQLServer");
        var moduleLoadContext = AssemblyLoadContext.GetLoadContext(provider.GetType().Assembly);

        moduleLoadContext.Should().NotBeNull();
        moduleLoadContext.Should().NotBeSameAs(AssemblyLoadContext.Default);
        foreach (var assemblyName in new[]
                 {
                     "Microsoft.Data.SqlClient",
                     "Microsoft.EntityFrameworkCore.SqlServer",
                     "System.ClientModel",
                     "System.Configuration.ConfigurationManager",
                 })
        {
            var assembly = moduleLoadContext!.LoadFromAssemblyName(new AssemblyName(assemblyName));
            AssemblyLoadContext.GetLoadContext(assembly).Should().BeSameAs(moduleLoadContext);
        }

        var sharedAssembly = moduleLoadContext!.LoadFromAssemblyName(
            new AssemblyName("Microsoft.EntityFrameworkCore"));
        AssemblyLoadContext.GetLoadContext(sharedAssembly).Should().BeSameAs(AssemblyLoadContext.Default);
    }

    [Test]
    public void PackagedSQLiteProvider_AppliesItsOwnedMigrationAcrossTheModuleBoundary()
    {
        var configuration = Configuration(
            ("Database:Provider", "SQLite"),
            ("ConnectionStrings:SQLite", "Data Source=:memory:"));
        using var registrations = PackagedDotNetRegistrationSet.Load(
            Path.Combine(AppContext.BaseDirectory, "contributions"),
            configuration);
        IServiceCollection services = new ServiceCollection();
        foreach (var descriptor in registrations.Services.Where(descriptor =>
                     descriptor.ServiceType == typeof(ISharpClawPersistenceProvider)))
        {
            services.Add(descriptor);
        }
        services.AddLogging();
        services.AddInfrastructure(
            configuration,
            SharpClawPersistenceOptions.FromConfiguration(configuration));

        using var serviceProvider = services.BuildServiceProvider();
        using var scope = serviceProvider.CreateScope();
        using var dbContext = scope.ServiceProvider.GetRequiredService<SharpClawDbContext>();
        dbContext.Database.OpenConnection();

        dbContext.Database.Migrate();

        dbContext.Database.GetAppliedMigrations()
            .Should().ContainSingle().Which.Should().Be("20260920114922_InitialCreate");
        dbContext.Database.HasPendingModelChanges().Should().BeFalse();
    }

    private static ServiceProvider BuildProvider(
        string providerKey,
        params (string Key, string? Value)[] values)
    {
        var dataDirectory = Path.Combine(
            Path.GetTempPath(),
            "SharpClaw.Tests",
            Guid.NewGuid().ToString("N"));
        var configurationValues = values
            .Append(("Database:Provider", providerKey))
            .Append(("Encryption:EncryptDatabase", "false"))
            .ToArray();
        var configuration = Configuration(configurationValues);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new EncryptionOptions { Key = new byte[32] });
        new JSONColdStorePersistenceModule().ConfigureServices(services);
        new PostgreSQLPersistenceModule().ConfigureServices(services);
        new SQLServerPersistenceModule().ConfigureServices(services);
        new SQLitePersistenceModule().ConfigureServices(services);
        services.AddInfrastructure(
            configuration,
            SharpClawPersistenceOptions.FromConfiguration(configuration, dataDirectory));
        return services.BuildServiceProvider();
    }

    private static IConfiguration Configuration(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(value =>
                new KeyValuePair<string, string?>(value.Key, value.Value)))
            .Build();

    private sealed class FuturePersistenceProvider : ISharpClawPersistenceProvider
    {
        public string Key => "FutureVectorStore";
        public IReadOnlyCollection<string> Aliases { get; } = [];
        public bool IsRelational => false;

        public void Configure(
            DbContextOptionsBuilder optionsBuilder,
            SharpClawPersistenceProviderContext context) =>
            optionsBuilder.UseInMemoryDatabase("future-provider");
    }

    private sealed class ConfiguredRegistrationDbContext(
        DbContextOptions<ConfiguredRegistrationDbContext> options) : DbContext(options)
    {
        public DbSet<ConfiguredRegistrationEntity> Entities => Set<ConfiguredRegistrationEntity>();
    }

    private sealed class ConfiguredRegistrationEntity
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
    }
}
