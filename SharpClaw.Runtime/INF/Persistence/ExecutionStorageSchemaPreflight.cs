using System.Text;
using Microsoft.EntityFrameworkCore;
using SharpClaw.Contracts.Entities.Core.Jobs;
using SharpClaw.Contracts.Enums;

namespace SharpClaw.Runtime.INF.Persistence;

/// <summary>
/// Refuses execution persistence stores that predate durable diagnostics. The
/// rework intentionally has no runtime compatibility path: existing data must
/// be upgraded before the current model can read or write it.
/// </summary>
public static class ExecutionStorageSchemaPreflight
{
    private const string JsonSchemaMarker =
        ".sharpclaw-durable-execution-schema-v1";

    private static readonly string[] LegacyDiagnosticFullNames =
    [
        "SharpClaw.Contracts.Entities.Core.Jobs.AgentJobLogEntryDB",
    ];

    public static void ValidateJsonStoreBeforeInitialization(
        string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        if (!Directory.Exists(dataDirectory))
            return;

        var legacyCollections = LegacyDiagnosticFullNames
            .Where(fullName => HasJsonColdStoreRecords(
                dataDirectory,
                fullName,
                fullName[(fullName.LastIndexOf('.') + 1)..]))
            .ToArray();
        if (legacyCollections.Length > 0)
        {
            throw UpgradeRequired(
                "legacy diagnostic collections are still present: "
                + string.Join(", ", legacyCollections));
        }

        if (File.Exists(Path.Combine(dataDirectory, JsonSchemaMarker)))
            return;

        var affectedCollections = new[]
        {
            (typeof(AgentJobDB).FullName!, typeof(AgentJobDB).Name),
        };
        var populated = affectedCollections
            .Where(candidate => HasJsonColdStoreRecords(
                dataDirectory,
                candidate.Item1,
                candidate.Item2))
            .Select(candidate => candidate.Item1)
            .ToArray();
        if (populated.Length > 0)
        {
            throw UpgradeRequired(
                "unversioned execution records are present: "
                + string.Join(", ", populated));
        }
    }

    public static void MarkJsonStoreInitialized(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        Directory.CreateDirectory(dataDirectory);
        var marker = Path.Combine(dataDirectory, JsonSchemaMarker);
        if (File.Exists(marker))
            return;

        var temporary = marker + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(
                temporary,
                "SharpClaw durable execution schema v1\n",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporary, marker, overwrite: false);
        }
        catch (IOException) when (File.Exists(marker))
        {
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    public static async Task ValidateRelationalStoreAsync(
        SharpClawDbContext db,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        try
        {
            _ = await db.AgentJobs
                .AsNoTracking()
                .Select(job => new
                {
                    ResultArtifactId = EF.Property<Guid?>(
                        job,
                        ExecutionMetadataColumns.ResultArtifactId),
                    ResultMediaType = EF.Property<string?>(
                        job,
                        ExecutionMetadataColumns.ResultMediaType),
                    ResultLength = EF.Property<long?>(
                        job,
                        ExecutionMetadataColumns.ResultLength),
                    ResultSha256 = EF.Property<string?>(
                        job,
                        ExecutionMetadataColumns.ResultSha256),
                    ResultPreview = EF.Property<string?>(
                        job,
                        ExecutionMetadataColumns.ResultPreview),
                    ErrorCode = EF.Property<string?>(
                        job,
                        ExecutionMetadataColumns.ErrorCode),
                    ErrorMessage = EF.Property<string?>(
                        job,
                        ExecutionMetadataColumns.ErrorMessage),
                    Completeness = EF.Property<DiagnosticCompleteness>(
                        job,
                        ExecutionMetadataColumns.DiagnosticCompleteness),
                    FinalLogSequence = EF.Property<long?>(
                        job,
                        ExecutionMetadataColumns.FinalLogSequence),
                    LogRecordCount = EF.Property<long>(
                        job,
                        ExecutionMetadataColumns.LogRecordCount),
                })
                .Take(1)
                .ToListAsync(cancellationToken);

            _ = await db.ExecutionAuditEvents
                .AsNoTracking()
                .Select(audit => audit.Id)
                .Take(1)
                .ToListAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw UpgradeRequired(
                "the relational execution metadata columns or audit table "
                + "could not be queried",
                ex);
        }
    }

    private static bool HasJsonColdStoreRecords(
        string dataDirectory,
        string fullName,
        string simpleName)
    {
        var currentRecords = Path.Combine(
            dataDirectory,
            "entities",
            EncodePathSegment(fullName),
            "records");
        if (Directory.Exists(currentRecords)
            && Directory.EnumerateFiles(
                    currentRecords,
                    "*.jcs",
                    SearchOption.TopDirectoryOnly)
                .Any())
        {
            return true;
        }

        var legacyRecords = Path.Combine(dataDirectory, simpleName);
        return Directory.Exists(legacyRecords)
            && Directory.EnumerateFiles(
                    legacyRecords,
                    "*.json",
                    SearchOption.TopDirectoryOnly)
                .Any();
    }

    private static string EncodePathSegment(string value) =>
        "n_" + Convert.ToBase64String(Encoding.UTF8.GetBytes(value))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static InvalidOperationException UpgradeRequired(
        string reason,
        Exception? innerException = null) =>
        new(
            "SharpClaw cannot start because a durable execution storage "
            + $"upgrade is required: {reason}. No migration or compatibility "
            + "fallback was generated by this rework; upgrade the store "
            + "before starting this version.",
            innerException);
}
