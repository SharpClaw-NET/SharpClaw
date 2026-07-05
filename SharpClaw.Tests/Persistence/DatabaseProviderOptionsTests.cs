using FluentAssertions;
using JSONColdStore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SharpClaw.Contracts.Persistence;
using SharpClaw.Runtime.INF;
using SharpClaw.Runtime.INF.Persistence;
using SharpClaw.Runtime.INF.Persistence.Modules;

namespace SharpClaw.Tests.Persistence;

[TestFixture]
public sealed class DatabaseProviderOptionsTests
{
    [Test]
    public void FromConfiguration_BindsJsonColdStoreProviderOptions()
    {
        var configuration = Configuration(
            ("Database:Provider", "JsonFile"),
            ("Encryption:EncryptDatabase", "false"),
            ("Database:JsonFile:Compression", "Auto"),
            ("Database:JsonFile:StartupMode", "FullHydration"),
            ("Database:JsonFile:FullScanPolicy", "FailUnlessExplicit"),
            ("Database:JsonFile:FsyncOnWrite", "false"),
            ("Database:JsonFile:FlushRetryMaxRetries", "5"),
            ("Database:JsonFile:FlushRetryBaseDelayMilliseconds", "150"),
            ("Database:JsonFile:TransactionReplayMaxRetries", "4"),
            ("Database:JsonFile:ReadRetryMaxRetries", "6"),
            ("Database:JsonFile:ReadRetryBaseDelayMilliseconds", "40"),
            ("Database:JsonFile:IndexRescanIntervalMinutes", "11"),
            ("Database:JsonFile:QuarantineMaxAgeDays", "12"),
            ("Database:JsonFile:EnableChecksums", "false"),
            ("Database:JsonFile:VerifyChecksumsOnRead", "true"),
            ("Database:JsonFile:EnableEventLog", "true"),
            ("Database:JsonFile:EventLogRetentionDays", "13"),
            ("Database:JsonFile:EnableSnapshots", "true"),
            ("Database:JsonFile:SnapshotIntervalHours", "14"),
            ("Database:JsonFile:SnapshotRetentionCount", "15"));

        var options = DatabaseProviderOptions.FromConfiguration(configuration, "E:\\sharpclaw-data");

        options.Provider.Should().Be(StorageMode.JsonFile);
        options.ConnectionString.Should().BeNull();
        options.JsonFile.DataDirectory.Should().Be("E:\\sharpclaw-data");
        options.JsonFile.EncryptAtRest.Should().BeFalse();
        options.JsonFile.Compression.Should().Be(JsonColdStoreCompression.Auto);
        options.JsonFile.StartupMode.Should().Be(JsonColdStoreStartupMode.FullHydration);
        options.JsonFile.FullScanPolicy.Should().Be(JsonColdStoreScanPolicy.FailUnlessExplicit);
        options.JsonFile.FsyncOnWrite.Should().BeFalse();
        options.JsonFile.FlushRetryMaxRetries.Should().Be(5);
        options.JsonFile.FlushRetryBaseDelayMilliseconds.Should().Be(150);
        options.JsonFile.TransactionReplayMaxRetries.Should().Be(4);
        options.JsonFile.ReadRetryMaxRetries.Should().Be(6);
        options.JsonFile.ReadRetryBaseDelayMilliseconds.Should().Be(40);
        options.JsonFile.IndexRescanIntervalMinutes.Should().Be(11);
        options.JsonFile.QuarantineMaxAgeDays.Should().Be(12);
        options.JsonFile.EnableChecksums.Should().BeFalse();
        options.JsonFile.VerifyChecksumsOnRead.Should().BeTrue();
        options.JsonFile.EnableEventLog.Should().BeTrue();
        options.JsonFile.EventLogRetentionDays.Should().Be(13);
        options.JsonFile.EnableSnapshots.Should().BeTrue();
        options.JsonFile.SnapshotIntervalHours.Should().Be(14);
        options.JsonFile.SnapshotRetentionCount.Should().Be(15);
    }

    [Test]
    public void FromConfiguration_BindsRelationalProviderOptions()
    {
        var configuration = Configuration(
            ("Database:Provider", "Postgres"),
            ("ConnectionStrings:Postgres", "Host=localhost;Database=sharpclaw"),
            ("Database:EnableDetailedErrors", "false"),
            ("Database:EnableSensitiveDataLogging", "true"),
            ("Database:Relational:CommandTimeoutSeconds", "30"),
            ("Database:Postgres:CommandTimeoutSeconds", "45"),
            ("Database:Postgres:EnableRetryOnFailure", "true"),
            ("Database:Postgres:MaxRetryCount", "7"),
            ("Database:Postgres:MaxRetryDelaySeconds", "8"));

        var options = DatabaseProviderOptions.FromConfiguration(configuration, "E:\\unused-json-data");

        options.Provider.Should().Be(StorageMode.Postgres);
        options.ConnectionString.Should().Be("Host=localhost;Database=sharpclaw");
        options.EnableDetailedErrors.Should().BeFalse();
        options.EnableSensitiveDataLogging.Should().BeTrue();
        options.Postgres.CommandTimeoutSeconds.Should().Be(45);
        options.Postgres.EnableRetryOnFailure.Should().BeTrue();
        options.Postgres.MaxRetryCount.Should().Be(7);
        options.Postgres.MaxRetryDelaySeconds.Should().Be(8);
        options.SqlServer.CommandTimeoutSeconds.Should().Be(30);
        options.SQLite.CommandTimeoutSeconds.Should().Be(30);
    }

    [Test]
    public void FromConfiguration_RejectsInvalidProviderOptionValues()
    {
        var configuration = Configuration(
            ("Database:Provider", "JsonFile"),
            ("Database:JsonFile:Compression", "ShrinkRay"));

        var act = () => DatabaseProviderOptions.FromConfiguration(configuration, "E:\\sharpclaw-data");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Database:JsonFile:Compression*");
    }

    [Test]
    public void AddInfrastructure_RejectsMissingRelationalConnectionString()
    {
        var services = new ServiceCollection();
        var options = new DatabaseProviderOptions
        {
            Provider = StorageMode.Postgres,
        };

        var act = () => services.AddInfrastructure(options);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*ConnectionStrings:Postgres*");
    }

    [Test]
    public void AddInfrastructure_RegistersSharedProviderOptionsForMainDbContext()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddInfrastructure(new DatabaseProviderOptions
        {
            Provider = StorageMode.SQLite,
            ConnectionString = "Data Source=:memory:",
            SQLite =
            {
                CommandTimeoutSeconds = 23,
            },
        });

        using var provider = services.BuildServiceProvider();
        var registeredOptions = provider.GetRequiredService<DatabaseProviderOptions>();
        var moduleOptions = provider.GetRequiredService<ModuleDbContextOptions>();

        registeredOptions.Provider.Should().Be(StorageMode.SQLite);
        registeredOptions.SQLite.CommandTimeoutSeconds.Should().Be(23);
        moduleOptions.StorageMode.Should().Be(StorageMode.SQLite);
        moduleOptions.ConnectionString.Should().Be("Data Source=:memory:");
    }

    [Test]
    public void ModuleDbContextFactory_AppliesSharedSqliteCommandTimeout()
    {
        var registry = new RuntimeModuleDbContextRegistry();
        registry.Register(new RuntimeModuleDbContextRegistration(
            "test_module",
            typeof(ConfiguredModuleDbContext),
            [typeof(ConfiguredModuleEntity)]));
        var options = new DatabaseProviderOptions
        {
            Provider = StorageMode.SQLite,
            ConnectionString = "Data Source=:memory:",
            SQLite =
            {
                CommandTimeoutSeconds = 17,
            },
        };
        var factory = new ModuleDbContextFactory(
            registry,
            new ModuleDbContextOptions
            {
                StorageMode = StorageMode.SQLite,
                ConnectionString = "Data Source=:memory:",
            },
            options,
            LoggerFactory.Create(_ => { }));

        using var db = (ConfiguredModuleDbContext)factory.CreateDbContext(typeof(ConfiguredModuleDbContext));

        db.Database.ProviderName.Should().Be("Microsoft.EntityFrameworkCore.Sqlite");
        db.Database.GetCommandTimeout().Should().Be(17);
    }

    private static IConfiguration Configuration(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(value => new KeyValuePair<string, string?>(value.Key, value.Value)))
            .Build();

    private sealed class ConfiguredModuleDbContext(DbContextOptions<ConfiguredModuleDbContext> options)
        : DbContext(options)
    {
        public DbSet<ConfiguredModuleEntity> Entities => Set<ConfiguredModuleEntity>();
    }

    private sealed class ConfiguredModuleEntity
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
    }
}
