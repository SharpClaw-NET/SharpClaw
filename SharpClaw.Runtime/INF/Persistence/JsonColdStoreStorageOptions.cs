using JSONColdStore;

namespace SharpClaw.Runtime.INF.Persistence;

public sealed class JsonColdStoreStorageOptions
{
    public string DataDirectory { get; set; } = Path.Combine(
        Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location)!,
        "Data");

    public bool EncryptAtRest { get; set; } = true;
    public JsonColdStoreCompression Compression { get; set; } = JsonColdStoreCompression.Brotli;
    public JsonColdStoreStartupMode StartupMode { get; set; } = JsonColdStoreStartupMode.MetadataOnly;
    public JsonColdStoreScanPolicy FullScanPolicy { get; set; } = JsonColdStoreScanPolicy.AllowSilentScans;
    public bool FsyncOnWrite { get; set; } = true;
    public int FlushRetryMaxRetries { get; set; } = 3;
    public int FlushRetryBaseDelayMilliseconds { get; set; } = 200;
    public int TransactionReplayMaxRetries { get; set; } = 3;
    public int ReadRetryMaxRetries { get; set; } = 3;
    public int ReadRetryBaseDelayMilliseconds { get; set; } = 25;
    public int IndexRescanIntervalMinutes { get; set; } = 60;
    public int QuarantineMaxAgeDays { get; set; } = 30;
    public bool EnableChecksums { get; set; } = true;
    public bool VerifyChecksumsOnRead { get; set; }
    public bool EnableEventLog { get; set; }
    public int EventLogRetentionDays { get; set; } = 7;
    public bool EnableSnapshots { get; set; }
    public int SnapshotIntervalHours { get; set; } = 24;
    public int SnapshotRetentionCount { get; set; } = 3;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(DataDirectory))
            throw new InvalidOperationException("JSONColdStore data directory must not be blank.");
        if (FlushRetryMaxRetries <= 0)
            throw new InvalidOperationException("Database:JsonFile:FlushRetryMaxRetries must be greater than zero.");
        if (FlushRetryBaseDelayMilliseconds <= 0)
            throw new InvalidOperationException("Database:JsonFile:FlushRetryBaseDelayMilliseconds must be greater than zero.");
        if (TransactionReplayMaxRetries <= 0)
            throw new InvalidOperationException("Database:JsonFile:TransactionReplayMaxRetries must be greater than zero.");
        if (ReadRetryMaxRetries <= 0)
            throw new InvalidOperationException("Database:JsonFile:ReadRetryMaxRetries must be greater than zero.");
        if (ReadRetryBaseDelayMilliseconds <= 0)
            throw new InvalidOperationException("Database:JsonFile:ReadRetryBaseDelayMilliseconds must be greater than zero.");
        if (IndexRescanIntervalMinutes < 0)
            throw new InvalidOperationException("Database:JsonFile:IndexRescanIntervalMinutes must be zero or greater.");
        if (QuarantineMaxAgeDays < 0)
            throw new InvalidOperationException("Database:JsonFile:QuarantineMaxAgeDays must be zero or greater.");
        if (EventLogRetentionDays < 0)
            throw new InvalidOperationException("Database:JsonFile:EventLogRetentionDays must be zero or greater.");
        if (SnapshotIntervalHours <= 0)
            throw new InvalidOperationException("Database:JsonFile:SnapshotIntervalHours must be greater than zero.");
        if (SnapshotRetentionCount <= 0)
            throw new InvalidOperationException("Database:JsonFile:SnapshotRetentionCount must be greater than zero.");
    }
}
