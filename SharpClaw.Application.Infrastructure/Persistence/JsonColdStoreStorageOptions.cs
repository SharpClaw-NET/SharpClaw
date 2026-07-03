using JSONColdStore;

namespace SharpClaw.Infrastructure.Persistence;

public sealed class JsonColdStoreStorageOptions
{
    public string DataDirectory { get; set; } = Path.Combine(
        Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location)!,
        "Data");

    public bool EncryptAtRest { get; set; } = true;
    public JsonColdStoreCompression Compression { get; set; } = JsonColdStoreCompression.Brotli;
    public JsonColdStoreScanPolicy FullScanPolicy { get; set; } = JsonColdStoreScanPolicy.AllowSilentScans;
    public bool FsyncOnWrite { get; set; } = true;
    public int IndexRescanIntervalMinutes { get; set; } = 60;
    public int QuarantineMaxAgeDays { get; set; } = 30;
    public bool EnableChecksums { get; set; } = true;
    public bool VerifyChecksumsOnRead { get; set; }
    public bool EnableEventLog { get; set; }
    public int EventLogRetentionDays { get; set; } = 7;
    public bool EnableSnapshots { get; set; }
    public int SnapshotIntervalHours { get; set; } = 24;
    public int SnapshotRetentionCount { get; set; } = 3;
}
