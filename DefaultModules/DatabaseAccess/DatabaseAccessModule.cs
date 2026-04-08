using System.Data.Common;
using System.Text;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using SharpClaw.Application.Infrastructure.Models.Resources;
using SharpClaw.Application.Services;
using SharpClaw.Contracts.DTOs.Databases;
using SharpClaw.Contracts.Enums;
using SharpClaw.Contracts.Modules;
using SharpClaw.Infrastructure.Persistence;
using SharpClaw.Utils.Security;

namespace SharpClaw.Modules.DatabaseAccess;

/// <summary>
/// Default module: register and query internal/external databases.
/// Supports PostgreSQL, MySQL, SQLite, and MSSQL with read-only
/// safety by default.
/// </summary>
public sealed class DatabaseAccessModule : ISharpClawModule
{
    public string Id => "sharpclaw_database_access";
    public string DisplayName => "Database Access";
    public string ToolPrefix => "db";

    // ═══════════════════════════════════════════════════════════════
    // DI Registration
    // ═══════════════════════════════════════════════════════════════

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddScoped<DatabaseResourceService>();
    }

    // ═══════════════════════════════════════════════════════════════
    // Contracts
    // ═══════════════════════════════════════════════════════════════

    public IReadOnlyList<ModuleContractExport> ExportedContracts => [];

    // ═══════════════════════════════════════════════════════════════
    // CLI Commands
    // ═══════════════════════════════════════════════════════════════

    public IReadOnlyList<ModuleCliCommand> GetCliCommands() =>
    [
        new(
            Name: "db",
            Aliases: ["database-access"],
            Scope: ModuleCliScope.TopLevel,
            Description: "Database Access module commands",
            UsageLines:
            [
                "db list-internal                List registered internal databases",
                "db list-external                List registered external databases",
            ],
            Handler: HandleDbCommandAsync),
    ];

    private static readonly JsonSerializerOptions CliJsonPrint = new()
    {
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private static async Task HandleDbCommandAsync(
        string[] args, IServiceProvider sp, CancellationToken ct)
    {
        if (args.Length < 2)
        {
            PrintDbUsage();
            return;
        }

        var sub = args[1].ToLowerInvariant();
        switch (sub)
        {
            case "list-internal":
            {
                var svc = sp.GetRequiredService<DatabaseResourceService>();
                var list = await svc.ListInternalAsync(ct);
                Console.WriteLine(JsonSerializer.Serialize(list, CliJsonPrint));
                break;
            }
            case "list-external":
            {
                var svc = sp.GetRequiredService<DatabaseResourceService>();
                var list = await svc.ListExternalAsync(ct);
                Console.WriteLine(JsonSerializer.Serialize(list, CliJsonPrint));
                break;
            }
            default:
                Console.Error.WriteLine($"Unknown db command: {sub}");
                PrintDbUsage();
                break;
        }
    }

    private static void PrintDbUsage()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  db list-internal    List registered internal databases");
        Console.WriteLine("  db list-external    List registered external databases");
    }

    // ═══════════════════════════════════════════════════════════════
    // Tool Definitions
    // ═══════════════════════════════════════════════════════════════

    public IReadOnlyList<ModuleToolDefinition> GetToolDefinitions()
    {
        var globalPerm = new ModuleToolPermission(
            IsPerResource: false, Check: null, DelegateTo: "RegisterDatabase");
        var internalPerm = new ModuleToolPermission(
            IsPerResource: true, Check: null, DelegateTo: "AccessInternalDatabases");
        var externalPerm = new ModuleToolPermission(
            IsPerResource: true, Check: null, DelegateTo: "AccessExternalDatabase");

        return
        [
            new("register_database",
                "Register a new internal or external database resource.",
                RegisterDatabaseSchema(), globalPerm),
            new("access_internal_databases",
                "Query an internal (SharpClaw-managed) database.",
                ResourceOnlySchema(), internalPerm),
            new("access_external_database",
                "Execute a query against a registered external database. " +
                "The query language must match the database type (e.g. SQL for MySQL/PostgreSQL/MSSQL, " +
                "MongoDB query JSON for MongoDB, Redis commands for Redis). " +
                "Provide the targetId of the registered database and the raw query string.",
                AccessExternalDatabaseSchema(), externalPerm),
        ];
    }

    // ═══════════════════════════════════════════════════════════════
    // Tool Execution
    // ═══════════════════════════════════════════════════════════════

    public async Task<string> ExecuteToolAsync(
        string toolName, JsonElement parameters, AgentJobContext job,
        IServiceProvider sp, CancellationToken ct)
    {
        return toolName switch
        {
            "register_database" => await RegisterDatabaseAsync(parameters, sp, ct),
            "access_internal_databases" => await AccessInternalDatabaseAsync(job, parameters, sp, ct),
            "access_external_database" => await AccessExternalDatabaseAsync(job, parameters, sp, ct),
            _ => throw new InvalidOperationException($"Unknown Database Access tool: {toolName}"),
        };
    }

    // ── register_database ─────────────────────────────────────────

    private static async Task<string> RegisterDatabaseAsync(
        JsonElement parameters, IServiceProvider sp, CancellationToken ct)
    {
        var svc = sp.GetRequiredService<DatabaseResourceService>();
        var name = Str(parameters, "name")
            ?? throw new InvalidOperationException("register_database requires 'name'.");
        var dbTypeStr = Str(parameters, "databaseType")
            ?? throw new InvalidOperationException("register_database requires 'databaseType'.");
        var dbType = Enum.Parse<DatabaseType>(dbTypeStr, ignoreCase: true);
        var location = Str(parameters, "location")
            ?? throw new InvalidOperationException("register_database requires 'location' (connection string or file path).");

        var isExternal = Bool(parameters, "isExternal") ?? false;
        var description = Str(parameters, "description");

        if (isExternal)
        {
            var result = await svc.CreateExternalAsync(
                new CreateExternalDatabaseRequest(name, dbType, location, description), ct);
            return $"External database registered: {result.Name} (id: {result.Id}, type: {result.DatabaseType})";
        }
        else
        {
            var result = await svc.CreateInternalAsync(
                new CreateInternalDatabaseRequest(name, dbType, location, description), ct);
            return $"Internal database registered: {result.Name} (id: {result.Id}, type: {result.DatabaseType})";
        }
    }

    // ── access_internal_databases ─────────────────────────────────

    private static async Task<string> AccessInternalDatabaseAsync(
        AgentJobContext job, JsonElement parameters,
        IServiceProvider sp, CancellationToken ct)
    {
        var dbCtx = sp.GetRequiredService<SharpClawDbContext>();
        var encOpts = sp.GetRequiredService<EncryptionOptions>();

        var entity = await ResolveInternalDatabaseAsync(job.ResourceId, dbCtx, ct);
        var query = Str(parameters, "query")
            ?? throw new InvalidOperationException("access_internal_databases requires 'query'.");

        var timeout = Int(parameters, "timeout") ?? 30;
        timeout = Math.Clamp(timeout, 1, 120);

        // Internal databases use Path as connection detail.
        var connectionString = BuildInternalConnectionString(entity);
        return await ExecuteQueryAsync(entity.DatabaseType, connectionString, query, timeout, ct);
    }

    // ── access_external_database ──────────────────────────────────

    private static async Task<string> AccessExternalDatabaseAsync(
        AgentJobContext job, JsonElement parameters,
        IServiceProvider sp, CancellationToken ct)
    {
        var dbCtx = sp.GetRequiredService<SharpClawDbContext>();
        var encOpts = sp.GetRequiredService<EncryptionOptions>();

        var entity = await ResolveExternalDatabaseAsync(job.ResourceId, dbCtx, ct);
        var query = Str(parameters, "query")
            ?? throw new InvalidOperationException("access_external_database requires 'query'.");

        var timeout = Int(parameters, "timeout") ?? 30;
        timeout = Math.Clamp(timeout, 1, 120);

        var connectionString = ApiKeyEncryptor.Decrypt(
            entity.EncryptedConnectionString, encOpts.Key);

        return await ExecuteQueryAsync(entity.DatabaseType, connectionString, query, timeout, ct);
    }

    // ═══════════════════════════════════════════════════════════════
    // Query Execution
    // ═══════════════════════════════════════════════════════════════

    private const int MaxResultBytes = 64 * 1024;

    private static async Task<string> ExecuteQueryAsync(
        DatabaseType dbType, string connectionString, string query,
        int timeoutSeconds, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        await using var connection = CreateConnection(dbType, connectionString);
        await connection.OpenAsync(cts.Token);

        await using var command = connection.CreateCommand();
        command.CommandText = query;
        command.CommandTimeout = timeoutSeconds;

        await using var reader = await command.ExecuteReaderAsync(cts.Token);
        return await FormatResultsAsync(reader, cts.Token);
    }

    private static DbConnection CreateConnection(DatabaseType dbType, string connectionString)
    {
        return dbType switch
        {
            DatabaseType.PostgreSQL or DatabaseType.CockroachDB
                => new Npgsql.NpgsqlConnection(connectionString),
            DatabaseType.MySQL or DatabaseType.MariaDB
                => new MySqlConnector.MySqlConnection(connectionString),
            DatabaseType.SQLite
                => new Microsoft.Data.Sqlite.SqliteConnection(connectionString),
            DatabaseType.MSSQL
                => new Microsoft.Data.SqlClient.SqlConnection(connectionString),
            _ => throw new NotSupportedException(
                $"Database type '{dbType}' is not yet supported by this module. " +
                $"Supported types: PostgreSQL, MySQL, MariaDB, SQLite, MSSQL, CockroachDB."),
        };
    }

    private static async Task<string> FormatResultsAsync(
        DbDataReader reader, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var totalRows = 0;

        do
        {
            var fieldCount = reader.FieldCount;
            if (fieldCount == 0) continue;

            // Header
            var columns = new string[fieldCount];
            for (var i = 0; i < fieldCount; i++)
                columns[i] = reader.GetName(i);

            sb.AppendLine("| " + string.Join(" | ", columns) + " |");
            sb.AppendLine("| " + string.Join(" | ", columns.Select(_ => "---")) + " |");

            // Rows
            while (await reader.ReadAsync(ct))
            {
                totalRows++;
                var values = new string[fieldCount];
                for (var i = 0; i < fieldCount; i++)
                {
                    var val = reader.IsDBNull(i) ? "NULL" : reader.GetValue(i)?.ToString() ?? "";
                    values[i] = val.Replace("|", "\\|").Replace("\n", " ").Replace("\r", "");
                }

                sb.AppendLine("| " + string.Join(" | ", values) + " |");

                if (sb.Length > MaxResultBytes)
                {
                    sb.AppendLine($"\n... (truncated at {MaxResultBytes / 1024} KB)");
                    return $"{sb}\n\n{totalRows}+ rows returned (truncated).";
                }
            }

            sb.AppendLine();
        }
        while (await reader.NextResultAsync(ct));

        if (totalRows == 0 && sb.Length == 0)
            return "Query executed successfully. No rows returned.";

        return $"{sb}\n{totalRows} row(s) returned.";
    }

    // ═══════════════════════════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════════════════════════

    private static async Task<InternalDatabaseDB> ResolveInternalDatabaseAsync(
        Guid? resourceId, SharpClawDbContext db, CancellationToken ct)
    {
        if (!resourceId.HasValue)
            throw new InvalidOperationException(
                "access_internal_databases requires a resource ID.");

        return await db.InternalDatabases
            .FirstOrDefaultAsync(e => e.Id == resourceId.Value, ct)
            ?? throw new InvalidOperationException(
                $"Internal database {resourceId} not found.");
    }

    private static async Task<ExternalDatabaseDB> ResolveExternalDatabaseAsync(
        Guid? resourceId, SharpClawDbContext db, CancellationToken ct)
    {
        if (!resourceId.HasValue)
            throw new InvalidOperationException(
                "access_external_database requires a resource ID.");

        return await db.ExternalDatabases
            .FirstOrDefaultAsync(e => e.Id == resourceId.Value, ct)
            ?? throw new InvalidOperationException(
                $"External database {resourceId} not found.");
    }

    private static string BuildInternalConnectionString(InternalDatabaseDB entity)
    {
        return entity.DatabaseType switch
        {
            DatabaseType.SQLite => $"Data Source={entity.Path}",
            _ => entity.Path,
        };
    }

    private static string? Str(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static int? Int(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetInt32()
            : null;

    private static bool? Bool(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null,
            }
            : null;

    // ═══════════════════════════════════════════════════════════════
    // JSON Schemas
    // ═══════════════════════════════════════════════════════════════

    private static JsonElement RegisterDatabaseSchema()
    {
        using var doc = JsonDocument.Parse("""
            {
                "type": "object",
                "properties": {
                    "name": {
                        "type": "string",
                        "description": "Display name for the database resource."
                    },
                    "databaseType": {
                        "type": "string",
                        "description": "Database engine type: MySQL, PostgreSQL, SQLite, MSSQL, MongoDB, Redis, CosmosDB, MariaDB, Oracle, CockroachDB, Firebird, Custom."
                    },
                    "location": {
                        "type": "string",
                        "description": "Connection string (external) or file path (internal)."
                    },
                    "isExternal": {
                        "type": "boolean",
                        "description": "True for external database (connection string is encrypted), false for internal."
                    },
                    "description": {
                        "type": "string",
                        "description": "Optional description."
                    }
                },
                "required": ["name", "databaseType", "location"]
            }
            """);
        return doc.RootElement.Clone();
    }

    private static JsonElement ResourceOnlySchema()
    {
        using var doc = JsonDocument.Parse("""
            {
                "type": "object",
                "properties": {
                    "targetId": {
                        "type": "string",
                        "description": "Resource GUID."
                    },
                    "query": {
                        "type": "string",
                        "description": "Raw query in the database's native language."
                    },
                    "timeout": {
                        "type": "integer",
                        "description": "Query timeout in seconds (default 30, max 120)."
                    }
                },
                "required": ["targetId", "query"]
            }
            """);
        return doc.RootElement.Clone();
    }

    private static JsonElement AccessExternalDatabaseSchema()
    {
        using var doc = JsonDocument.Parse("""
            {
                "type": "object",
                "properties": {
                    "targetId": {
                        "type": "string",
                        "description": "External database GUID."
                    },
                    "query": {
                        "type": "string",
                        "description": "Raw query in the database's native language. Must match the database type: SQL for MySQL/PostgreSQL/MSSQL/SQLite/MariaDB/CockroachDB/Oracle/Firebird, MongoDB query JSON for MongoDB, Redis commands for Redis, SQL for CosmosDB."
                    },
                    "timeout": {
                        "type": "integer",
                        "description": "Query timeout in seconds (default 30, max 120)."
                    }
                },
                "required": ["targetId", "query"]
            }
            """);
        return doc.RootElement.Clone();
    }
}
