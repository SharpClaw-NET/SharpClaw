using JSONColdStore;
using Microsoft.Extensions.Configuration;
using SharpClaw.Contracts.Persistence;

namespace SharpClaw.Runtime.INF.Persistence;

public sealed class DatabaseProviderOptions
{
    public StorageMode Provider { get; set; } = StorageMode.JsonFile;
    public string? ConnectionString { get; set; }
    public bool EnableDetailedErrors { get; set; } = true;
    public bool EnableSensitiveDataLogging { get; set; }
    public JsonColdStoreStorageOptions JsonFile { get; } = new();
    public RelationalProviderOptions Postgres { get; } = new("SharpClaw.Migrations.Postgres", SupportsRetryOnFailure: true);
    public RelationalProviderOptions SqlServer { get; } = new("SharpClaw.Migrations.SqlServer", SupportsRetryOnFailure: true);
    public RelationalProviderOptions SQLite { get; } = new("SharpClaw.Migrations.SQLite", SupportsRetryOnFailure: false);

    public static DatabaseProviderOptions FromConfiguration(
        IConfiguration configuration,
        string? jsonFileDataDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var options = new DatabaseProviderOptions();
        var database = configuration.GetSection("Database");

        options.Provider = ReadEnum(database["Provider"], StorageMode.JsonFile, "Database:Provider");
        options.ConnectionString = options.Provider == StorageMode.JsonFile
            ? null
            : configuration[$"ConnectionStrings:{options.Provider}"];
        options.EnableDetailedErrors = ReadBool(
            database["EnableDetailedErrors"],
            options.EnableDetailedErrors,
            "Database:EnableDetailedErrors");
        options.EnableSensitiveDataLogging = ReadBool(
            database["EnableSensitiveDataLogging"],
            options.EnableSensitiveDataLogging,
            "Database:EnableSensitiveDataLogging");

        options.JsonFile.DataDirectory = string.IsNullOrWhiteSpace(jsonFileDataDirectory)
            ? options.JsonFile.DataDirectory
            : jsonFileDataDirectory;
        options.JsonFile.EncryptAtRest = ReadBool(
            configuration["Encryption:EncryptDatabase"],
            options.JsonFile.EncryptAtRest,
            "Encryption:EncryptDatabase");
        ApplyJsonFileSection(database.GetSection("JsonFile"), options.JsonFile, "Database:JsonFile");

        ApplyRelationalSection(database.GetSection("Relational"), options.Postgres, "Database:Relational");
        ApplyRelationalSection(database.GetSection("Relational"), options.SqlServer, "Database:Relational");
        ApplyRelationalSection(database.GetSection("Relational"), options.SQLite, "Database:Relational");
        ApplyRelationalSection(database.GetSection("Postgres"), options.Postgres, "Database:Postgres");
        ApplyRelationalSection(database.GetSection("SqlServer"), options.SqlServer, "Database:SqlServer");
        ApplyRelationalSection(database.GetSection("SQLite"), options.SQLite, "Database:SQLite");

        options.Validate();
        return options;
    }

    public RelationalProviderOptions GetRelationalOptions(StorageMode mode) =>
        mode switch
        {
            StorageMode.Postgres => Postgres,
            StorageMode.SqlServer => SqlServer,
            StorageMode.SQLite => SQLite,
            _ => throw new InvalidOperationException(
                $"Storage mode '{mode}' does not use relational provider options."),
        };

    public void Validate()
    {
        JsonFile.Validate();
        Postgres.Validate("Postgres");
        SqlServer.Validate("SqlServer");
        SQLite.Validate("SQLite");
    }

    private static void ApplyJsonFileSection(
        IConfiguration section,
        JsonColdStoreStorageOptions options,
        string path)
    {
        options.Compression = ReadEnum(section["Compression"], options.Compression, $"{path}:Compression");
        options.StartupMode = ReadEnum(section["StartupMode"], options.StartupMode, $"{path}:StartupMode");
        options.FullScanPolicy = ReadEnum(section["FullScanPolicy"], options.FullScanPolicy, $"{path}:FullScanPolicy");
        options.FsyncOnWrite = ReadBool(section["FsyncOnWrite"], options.FsyncOnWrite, $"{path}:FsyncOnWrite");
        options.IndexRescanIntervalMinutes = ReadInt(
            section["IndexRescanIntervalMinutes"],
            options.IndexRescanIntervalMinutes,
            $"{path}:IndexRescanIntervalMinutes");
        options.QuarantineMaxAgeDays = ReadInt(
            section["QuarantineMaxAgeDays"],
            options.QuarantineMaxAgeDays,
            $"{path}:QuarantineMaxAgeDays");
        options.EnableChecksums = ReadBool(section["EnableChecksums"], options.EnableChecksums, $"{path}:EnableChecksums");
        options.VerifyChecksumsOnRead = ReadBool(
            section["VerifyChecksumsOnRead"],
            options.VerifyChecksumsOnRead,
            $"{path}:VerifyChecksumsOnRead");
        options.EnableEventLog = ReadBool(section["EnableEventLog"], options.EnableEventLog, $"{path}:EnableEventLog");
        options.EventLogRetentionDays = ReadInt(
            section["EventLogRetentionDays"],
            options.EventLogRetentionDays,
            $"{path}:EventLogRetentionDays");
        options.EnableSnapshots = ReadBool(section["EnableSnapshots"], options.EnableSnapshots, $"{path}:EnableSnapshots");
        options.SnapshotIntervalHours = ReadInt(
            section["SnapshotIntervalHours"],
            options.SnapshotIntervalHours,
            $"{path}:SnapshotIntervalHours");
        options.SnapshotRetentionCount = ReadInt(
            section["SnapshotRetentionCount"],
            options.SnapshotRetentionCount,
            $"{path}:SnapshotRetentionCount");
        options.FlushRetryMaxRetries = ReadInt(
            section["FlushRetryMaxRetries"],
            options.FlushRetryMaxRetries,
            $"{path}:FlushRetryMaxRetries");
        options.FlushRetryBaseDelayMilliseconds = ReadInt(
            section["FlushRetryBaseDelayMilliseconds"],
            options.FlushRetryBaseDelayMilliseconds,
            $"{path}:FlushRetryBaseDelayMilliseconds");
        options.TransactionReplayMaxRetries = ReadInt(
            section["TransactionReplayMaxRetries"],
            options.TransactionReplayMaxRetries,
            $"{path}:TransactionReplayMaxRetries");
        options.ReadRetryMaxRetries = ReadInt(
            section["ReadRetryMaxRetries"],
            options.ReadRetryMaxRetries,
            $"{path}:ReadRetryMaxRetries");
        options.ReadRetryBaseDelayMilliseconds = ReadInt(
            section["ReadRetryBaseDelayMilliseconds"],
            options.ReadRetryBaseDelayMilliseconds,
            $"{path}:ReadRetryBaseDelayMilliseconds");
    }

    private static void ApplyRelationalSection(
        IConfiguration section,
        RelationalProviderOptions options,
        string path)
    {
        options.CommandTimeoutSeconds = ReadNullableInt(
            section["CommandTimeoutSeconds"],
            options.CommandTimeoutSeconds,
            $"{path}:CommandTimeoutSeconds");

        if (options.SupportsRetryOnFailure)
        {
            options.EnableRetryOnFailure = ReadBool(
                section["EnableRetryOnFailure"],
                options.EnableRetryOnFailure,
                $"{path}:EnableRetryOnFailure");
            options.MaxRetryCount = ReadInt(
                section["MaxRetryCount"],
                options.MaxRetryCount,
                $"{path}:MaxRetryCount");
            options.MaxRetryDelaySeconds = ReadInt(
                section["MaxRetryDelaySeconds"],
                options.MaxRetryDelaySeconds,
                $"{path}:MaxRetryDelaySeconds");
        }
    }

    private static bool ReadBool(string? value, bool defaultValue, string key)
    {
        if (string.IsNullOrWhiteSpace(value))
            return defaultValue;
        if (bool.TryParse(value, out var parsed))
            return parsed;
        throw new InvalidOperationException($"{key} must be true or false.");
    }

    private static int ReadInt(string? value, int defaultValue, string key)
    {
        if (string.IsNullOrWhiteSpace(value))
            return defaultValue;
        if (int.TryParse(value, out var parsed))
            return parsed;
        throw new InvalidOperationException($"{key} must be an integer.");
    }

    private static int? ReadNullableInt(string? value, int? defaultValue, string key)
    {
        if (string.IsNullOrWhiteSpace(value))
            return defaultValue;
        if (int.TryParse(value, out var parsed))
            return parsed;
        throw new InvalidOperationException($"{key} must be an integer.");
    }

    private static TEnum ReadEnum<TEnum>(string? value, TEnum defaultValue, string key)
        where TEnum : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(value))
            return defaultValue;
        if (Enum.TryParse<TEnum>(value, ignoreCase: true, out var parsed)
            && Enum.IsDefined(parsed))
        {
            return parsed;
        }

        var values = string.Join(", ", Enum.GetNames<TEnum>());
        throw new InvalidOperationException($"{key} must be one of: {values}.");
    }
}

public sealed class RelationalProviderOptions(
    string migrationsAssembly,
    bool SupportsRetryOnFailure)
{
    public string MigrationsAssembly { get; } = migrationsAssembly;
    public bool SupportsRetryOnFailure { get; } = SupportsRetryOnFailure;
    public int? CommandTimeoutSeconds { get; set; }
    public bool EnableRetryOnFailure { get; set; }
    public int MaxRetryCount { get; set; } = 6;
    public int MaxRetryDelaySeconds { get; set; } = 30;

    public void Validate(string providerName)
    {
        if (CommandTimeoutSeconds is <= 0)
            throw new InvalidOperationException(
                $"Database:{providerName}:CommandTimeoutSeconds must be greater than zero when set.");
        if (MaxRetryCount <= 0)
            throw new InvalidOperationException(
                $"Database:{providerName}:MaxRetryCount must be greater than zero.");
        if (MaxRetryDelaySeconds <= 0)
            throw new InvalidOperationException(
                $"Database:{providerName}:MaxRetryDelaySeconds must be greater than zero.");
    }
}
