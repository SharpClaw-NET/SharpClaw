namespace SharpClaw.Shared.DurableStorage;


public sealed class DurableStorageOptions
{
    public const int HardMaximumPageBytes = 16 * 1024 * 1024;
    public const long HardMaximumReadScanBytes = 64L * 1024 * 1024;

    public required string RootDirectory { get; init; }
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1819", Justification = "Preserve the published byte-array encryption-key property and its existing ownership contract.")]
    public byte[]? EncryptionKey { get; init; }
    public long SegmentMaxBytes { get; init; } = 8 * 1024 * 1024;
    public TimeSpan SegmentMaxAge { get; init; } = TimeSpan.FromMinutes(5);
    public int MaxRecordBytes { get; init; } = 256 * 1024;
    public int MaxPageRecords { get; init; } = 1000;
    public int MaxPageBytes { get; init; } = 1024 * 1024;
    public long MaxReadScanBytes { get; init; } = 16L * 1024 * 1024;
    public bool AcquireWriterLease { get; init; } = true;
}
