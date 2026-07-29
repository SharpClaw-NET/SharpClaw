using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SharpClaw.Contracts.DTOs.Diagnostics;
using SharpClaw.Contracts.Enums;
using SharpClaw.Shared.DurableStorage;

namespace SharpClaw.Runtime.INF.DurableStorage;

public sealed record DurableLogQuery(
    int Take = 200,
    int MaxBytes = 262_144,
    string? MinimumLevel = null,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    string? Contains = null,
    long MaxScanBytes = 16 * 1024 * 1024);

/// <summary>
/// Runtime-facing facade over the provider-independent segmented store. It
/// owns cursor authentication, public response mapping, and job/task artifact
/// externalization; callers never construct paths or sequences.
/// </summary>
public sealed class ExecutionDiagnosticStore(
    DurableSegmentStore records,
    DurableCursorCodec cursors,
    IExecutionArtifactStore artifacts)
{
    private const int InlineOutputBytes = 64 * 1024;
    private const int InlineLogBytes = 192 * 1024;
    private const int PreviewCharacters = 2048;

    public async ValueTask<DurableAppendReceipt> AppendJobLogAsync(
        Guid jobId,
        string message,
        string level,
        string eventName = "JobDiagnostic",
        string? exceptionType = null,
        string? correlationId = null,
        Guid? recordId = null,
        DateTimeOffset? timestamp = null,
        DurableWriteMode writeMode = DurableWriteMode.Durable,
        CancellationToken cancellationToken = default)
    {
        return await AppendOwnerLogAsync(
            DurableStreamKey.Job(jobId),
            ExecutionOwnerKind.AgentJob,
            jobId,
            message,
            level,
            eventName,
            exceptionType,
            correlationId,
            recordId,
            timestamp,
            writeMode,
            cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<DurableLogPageResponse> ReadJobLogsAsync(
        Guid jobId,
        string? cursor,
        DurableLogQuery query,
        CancellationToken cancellationToken = default) =>
        ReadLogsAsync(
            DurableStreamKey.Job(jobId),
            cursor,
            query,
            cancellationToken);

    public ValueTask<DurableLogPageResponse> ReadOperationalModuleLogsAsync(
        string moduleId,
        Guid bootId,
        string? cursor,
        DurableLogQuery query,
        CancellationToken cancellationToken = default) =>
        ReadLogsAsync(
            DurableStreamKey.Module(moduleId, bootId),
            cursor,
            query,
            cancellationToken);

    public ValueTask<DurableLogPageResponse> ReadProcessLogsAsync(
        string appName,
        Guid bootId,
        string? cursor,
        DurableLogQuery query,
        CancellationToken cancellationToken = default) =>
        ReadLogsAsync(
            DurableStreamKey.Process(appName, bootId),
            cursor,
            query,
            cancellationToken);

    public ValueTask<DurableStreamSummary> GetJobLogSummaryAsync(
        Guid jobId,
        CancellationToken cancellationToken = default) =>
        records.GetSummaryAsync(DurableStreamKey.Job(jobId), cancellationToken);

    public ValueTask SealJobAsync(
        Guid jobId,
        CancellationToken cancellationToken = default) =>
        records.SealAsync(DurableStreamKey.Job(jobId), cancellationToken);

    public DurableStorageSnapshot GetSnapshot() => records.GetSnapshot();

    private async ValueTask<DurableAppendReceipt> AppendOwnerLogAsync(
        DurableStreamKey stream,
        ExecutionOwnerKind ownerKind,
        Guid ownerId,
        string message,
        string level,
        string eventName,
        string? exceptionType,
        string? correlationId,
        Guid? recordId,
        DateTimeOffset? timestamp,
        DurableWriteMode writeMode,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        var idempotent = recordId is { } suppliedId && suppliedId != Guid.Empty;
        var resolvedRecordId = idempotent ? recordId!.Value : Guid.NewGuid();
        var externalize = Encoding.UTF8.GetByteCount(message) > InlineLogBytes;
        if (idempotent && externalize)
        {
            var existing = await records.FindIdempotentAppendAsync(
                    stream,
                    resolvedRecordId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (existing is not null)
                return existing;
        }
        DurableArtifactReference? artifactReference = null;
        var storedMessage = message;
        if (externalize)
        {
            await using var content = new MemoryStream(
                Encoding.UTF8.GetBytes(message),
                writable: false);
            var descriptor = await artifacts.PutAsync(
                content,
                new ArtifactWriteRequest(
                    ownerKind,
                    ownerId,
                    "text/plain; charset=utf-8",
                    BoundPreview(message)),
                cancellationToken).ConfigureAwait(false);
            artifactReference = ToArtifactReference(descriptor);
            storedMessage = BoundPreview(message);
        }

        return await records.AppendAsync(
            stream,
            new DurableRecordWrite(
                resolvedRecordId,
                timestamp ?? DateTimeOffset.UtcNow,
                NormalizeLevel(level),
                eventName,
                storedMessage,
                exceptionType,
                correlationId,
                artifactReference,
                Idempotent: idempotent),
            writeMode,
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<DurableLogPageResponse> ReadLogsAsync(
        DurableStreamKey stream,
        string? cursor,
        DurableLogQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var fingerprint = BuildFilterFingerprint(query);
        var (nextSequence, throughSequence) = DecodeCursor(
            stream,
            cursor,
            fingerprint);
        var page = await records.ReadAsync(
            stream,
            nextSequence,
            ToReadOptions(query, throughSequence),
            cancellationToken).ConfigureAwait(false);
        return new DurableLogPageResponse(
            page.Records.Select(ToLogResponse).ToArray(),
            EncodeNextCursor(stream, page, fingerprint),
            page.HasMore,
            page.Records.Count,
            page.ReturnedBytes,
            page.SnapshotLastSequence,
            page.FirstAvailableSequence,
            page.ExpiredRecordCount);
    }

    private (long NextSequence, long? ThroughSequence) DecodeCursor(
        DurableStreamKey stream,
        string? cursor,
        string fingerprint)
    {
        if (string.IsNullOrWhiteSpace(cursor))
            return (1, null);
        var decoded = cursors.Decode(cursor, stream, fingerprint);
        return (decoded.NextSequence, decoded.SnapshotLastSequence);
    }

    private string? EncodeNextCursor(
        DurableStreamKey stream,
        DurableRecordPage page,
        string fingerprint)
    {
        return page.HasMore && page.NextSequence is { } next
            ? cursors.Encode(stream, next, page.SnapshotLastSequence, fingerprint)
            : null;
    }

    private static DurableReadOptions ToReadOptions(
        DurableLogQuery query,
        long? throughSequence) =>
        new(
            query.Take,
            query.MaxBytes,
            query.MinimumLevel,
            query.From,
            query.To,
            query.Contains,
            throughSequence,
            query.MaxScanBytes);

    private static string BuildFilterFingerprint(DurableLogQuery query)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            query.MinimumLevel,
            query.From,
            query.To,
            query.Contains,
            query.MaxScanBytes,
        });
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private static DurableRecordWrite BuildBoundedRecord(
        string message,
        string level,
        string eventName,
        string? exceptionType,
        string? correlationId)
    {
        ArgumentNullException.ThrowIfNull(message);
        var bounded = Encoding.UTF8.GetByteCount(message) <= InlineLogBytes
            ? message
            : BoundPreview(message) + " [record truncated]";
        return new DurableRecordWrite(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            NormalizeLevel(level),
            eventName,
            bounded,
            exceptionType,
            correlationId);
    }

    private static DurableLogRecordResponse ToLogResponse(DurableRecord record) =>
        new(
            record.Sequence,
            record.RecordId,
            record.Timestamp,
            record.Level,
            record.EventName,
            record.Message,
            record.ExceptionType,
            record.CorrelationId,
            ToArtifactResponse(record.Artifact));

    private static ExecutionArtifactResponse? ToArtifactResponse(
        DurableArtifactReference? artifact) =>
        artifact is null
            ? null
            : new ExecutionArtifactResponse(
                artifact.Id,
                artifact.MediaType,
                artifact.Length,
                artifact.Sha256,
                artifact.Preview);

    private static DurableArtifactReference ToArtifactReference(
        ExecutionArtifactDescriptor descriptor) =>
        new(
            descriptor.Id,
            descriptor.MediaType,
            descriptor.Length,
            descriptor.Sha256,
            descriptor.Preview);

    private static string BoundPreview(string value) =>
        value.Length <= PreviewCharacters
            ? value
            : value[..PreviewCharacters];

    private static string NormalizeLevel(string level) =>
        string.IsNullOrWhiteSpace(level) ? "Information" : level.Trim();
}
