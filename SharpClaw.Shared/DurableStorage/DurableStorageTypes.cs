namespace SharpClaw.Shared.DurableStorage;

public enum DurableStreamKind
{
    JobLog,
    ProcessLog,
    ModuleLog,
}

public enum DurableWriteMode
{
    Buffered,
    Durable,
}

public readonly record struct DurableStreamKey
{
    private DurableStreamKey(DurableStreamKind kind, string canonicalValue)
    {
        Kind = kind;
        CanonicalValue = canonicalValue;
    }

    public DurableStreamKind Kind { get; }
    public string CanonicalValue { get; }

    public static DurableStreamKey Job(Guid jobId) =>
        new(DurableStreamKind.JobLog, $"job/{jobId:D}");

    public static DurableStreamKey Process(string appName, Guid bootId) =>
        new(
            DurableStreamKind.ProcessLog,
            $"process/{NormalizeLogicalName(appName)}/{bootId:D}");

    public static DurableStreamKey Module(string moduleId, Guid bootId) =>
        new(
            DurableStreamKind.ModuleLog,
            $"module/{NormalizeLogicalName(moduleId)}/{bootId:D}");

    public static bool TryParseOperational(
        string? canonicalValue,
        out DurableStreamKey key,
        out string? appName,
        out string? moduleId,
        out Guid bootId)
    {
        key = default;
        appName = null;
        moduleId = null;
        bootId = Guid.Empty;
        if (string.IsNullOrWhiteSpace(canonicalValue))
            return false;

        var firstSeparator = canonicalValue.IndexOf('/');
        var lastSeparator = canonicalValue.LastIndexOf('/');
        if (firstSeparator <= 0
            || lastSeparator <= firstSeparator + 1
            || lastSeparator == canonicalValue.Length - 1
            || !Guid.TryParseExact(
                canonicalValue[(lastSeparator + 1)..],
                "D",
                out bootId))
        {
            bootId = Guid.Empty;
            return false;
        }

        var kind = canonicalValue[..firstSeparator];
        var logicalName = canonicalValue[(firstSeparator + 1)..lastSeparator];
        try
        {
            if (kind.Equals("process", StringComparison.Ordinal))
            {
                key = Process(logicalName, bootId);
                appName = logicalName;
            }
            else if (kind.Equals("module", StringComparison.Ordinal))
            {
                key = Module(logicalName, bootId);
                moduleId = logicalName;
            }
            else
            {
                return false;
            }

            return string.Equals(
                key.CanonicalValue,
                canonicalValue,
                StringComparison.Ordinal);
        }
        catch (ArgumentException)
        {
            key = default;
            appName = null;
            moduleId = null;
            bootId = Guid.Empty;
            return false;
        }
    }

    private static string NormalizeLogicalName(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var normalized = value.Trim().ToLowerInvariant();
        if (normalized.Length > 256 || normalized.Any(char.IsControl))
            throw new ArgumentException("Logical stream name is invalid.", nameof(value));
        return normalized;
    }
}

public sealed record DurableRecordWrite(
    Guid RecordId,
    DateTimeOffset Timestamp,
    string Level,
    string EventName,
    string Message,
    string? ExceptionType = null,
    string? CorrelationId = null,
    DurableArtifactReference? Artifact = null,
    bool Idempotent = false,
    string? ExceptionText = null,
    string? MessageTemplate = null,
    string? Category = null,
    int? EventIdId = null,
    string? EventIdName = null,
    string? TraceId = null,
    string? SpanId = null,
    IReadOnlyDictionary<string, string>? Properties = null);

public sealed record DurableRecord(
    long Sequence,
    Guid RecordId,
    DateTimeOffset Timestamp,
    string Level,
    string EventName,
    string Message,
    string? ExceptionType,
    string? CorrelationId,
    DurableArtifactReference? Artifact,
    string? ExceptionText = null,
    string? MessageTemplate = null,
    string? Category = null,
    int? EventIdId = null,
    string? EventIdName = null,
    string? TraceId = null,
    string? SpanId = null,
    IReadOnlyDictionary<string, string>? Properties = null);

public sealed record DurableArtifactReference(
    Guid Id,
    string MediaType,
    long Length,
    string Sha256,
    string? Preview = null);

public sealed record DurableAppendReceipt(
    long Sequence,
    long RecordCount,
    DateTimeOffset Timestamp);

public sealed record DurableReadOptions(
    int Take = 200,
    int MaxBytes = 262_144,
    string? MinimumLevel = null,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    string? Contains = null,
    long? ThroughSequence = null,
    long MaxScanBytes = 16 * 1024 * 1024);

public sealed record DurableRecordPage(
    IReadOnlyList<DurableRecord> Records,
    long? NextSequence,
    bool HasMore,
    int ReturnedBytes,
    long SnapshotLastSequence,
    long FirstAvailableSequence,
    long ExpiredRecordCount);

public sealed record DurableStreamSummary(
    long RecordCount,
    long? LastSequence,
    DateTimeOffset? LastTimestamp,
    long EncodedBytes,
    long FirstAvailableSequence,
    long ExpiredRecordCount);

public sealed record DurableOperationalStreamSummary(
    DurableStreamKey Stream,
    string? AppName,
    string? ModuleId,
    Guid BootId,
    bool HasActiveSegment,
    bool HasSealedSegments,
    long RecordCount,
    long EncodedBytes,
    long FirstSequence,
    long? LastSequence,
    long FirstAvailableSequence,
    long ExpiredRecordCount,
    DateTimeOffset? LastTimestamp);

public sealed record DurableOperationalStreamIdentityGap(
    DurableStreamKind Kind,
    string StreamHash,
    string Reason);

public sealed class DurableOperationalStreamEnumerationOptions
{
    public const int HardMaximumEntries = 1024;
    public const long HardMaximumScanBytes = 64L * 1024 * 1024;
    public static readonly TimeSpan HardMaximumDuration = TimeSpan.FromSeconds(30);

    public int MaxEntries { get; init; } = 100;
    public long MaxScanBytes { get; init; } = 4L * 1024 * 1024;
    public TimeSpan MaxDuration { get; init; } = TimeSpan.FromSeconds(2);
}

public sealed record DurableOperationalStreamCatalog(
    IReadOnlyList<DurableOperationalStreamSummary> Streams,
    IReadOnlyList<DurableOperationalStreamIdentityGap> IdentityGaps,
    bool HasMore,
    int ScannedDirectories,
    long ScannedBytes);

public sealed class DurableRetentionOptions
{
    public TimeSpan JobLogAge { get; init; } = TimeSpan.FromDays(30);
    public TimeSpan ProcessLogAge { get; init; } = TimeSpan.FromDays(14);
    public TimeSpan ModuleLogAge { get; init; } = TimeSpan.FromDays(14);
    public long MaximumEncodedBytes { get; init; } = 10L * 1024 * 1024 * 1024;
    public long MinimumFreeBytes { get; init; } = 1024L * 1024 * 1024;
    public int MaximumDeletesPerRun { get; init; } = 10_000;

    public TimeSpan GetMaximumAge(DurableStreamKind kind) => kind switch
    {
        DurableStreamKind.JobLog => JobLogAge,
        DurableStreamKind.ProcessLog => ProcessLogAge,
        DurableStreamKind.ModuleLog => ModuleLogAge,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}

public sealed record DurableRetentionResult(
    int DeletedSegments,
    long ReclaimedBytes,
    long RemainingEncodedBytes,
    long AvailableFreeBytes,
    bool QuotaSatisfied,
    DateTimeOffset CompletedAt);

public sealed class DurableStorageOptions
{
    public const int HardMaximumPageBytes = 16 * 1024 * 1024;
    public const long HardMaximumReadScanBytes = 64L * 1024 * 1024;

    public required string RootDirectory { get; init; }
    public byte[]? EncryptionKey { get; init; }
    public long SegmentMaxBytes { get; init; } = 8 * 1024 * 1024;
    public TimeSpan SegmentMaxAge { get; init; } = TimeSpan.FromMinutes(5);
    public int MaxRecordBytes { get; init; } = 256 * 1024;
    public int MaxPageRecords { get; init; } = 1000;
    public int MaxPageBytes { get; init; } = 1024 * 1024;
    public long MaxReadScanBytes { get; init; } = 16L * 1024 * 1024;
    public bool AcquireWriterLease { get; init; } = true;
}

public sealed record DurableStorageSnapshot(
    bool IsHealthy,
    string? DegradedReason,
    long EncodedBytes,
    int ActiveStreams,
    int ResidentStreams,
    long SealedSegments,
    DateTimeOffset? LastSuccessfulFlush);
