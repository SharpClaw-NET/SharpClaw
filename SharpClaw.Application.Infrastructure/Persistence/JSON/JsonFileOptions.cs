using SharpClaw.Contracts.Entities.Core.Jobs;
using SharpClaw.Contracts.Entities.Core.Messages;
using SharpClaw.Contracts.Entities.Core.Tasks;

namespace SharpClaw.Infrastructure.Persistence.JSON;

public sealed class JsonFileOptions
{
    /// <summary>
    /// Directory where JSON data files are stored.
    /// Defaults to a "data" folder next to the application.
    /// </summary>
    public string DataDirectory { get; set; } = Path.Combine(
        Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location)!,
        "Data");

    /// <summary>
    /// When true (default), all entity and join-table files are AES-256-GCM
    /// encrypted on disk. The read path auto-detects plaintext regardless of
    /// this flag, so legacy data is always loadable and toggling to false is
    /// non-destructive (new writes become plaintext, existing encrypted files
    /// still load).
    /// </summary>
    public bool EncryptAtRest { get; set; } = true;

    /// <summary>
    /// When true (default), all writes are flushed to durable storage (fsync)
    /// before the atomic rename, preventing data loss on power failure.
    /// </summary>
    public bool FsyncOnWrite { get; set; } = true;

    /// <summary>
    /// Interval in minutes between periodic cold index rescan cycles.
    /// Default is <c>60</c>. Set to <c>0</c> to disable periodic rescans
    /// (startup-only rebuild).
    /// </summary>
    public int IndexRescanIntervalMinutes { get; set; } = 60;

    /// <summary>
    /// Maximum age in days for quarantined files before automatic purge
    /// during startup. Default is <c>30</c>. Set to <c>0</c> to keep
    /// quarantined files forever (no automatic purge).
    /// <para>
    /// <b>Phase F (RGAP-14):</b> Corrupt or unreadable entity files are
    /// moved to <c>_quarantine/{entityType}/</c> after 3 retry attempts
    /// with exponential backoff. This setting controls how long those
    /// quarantined files are retained.
    /// </para>
    /// </summary>
    public int QuarantineMaxAgeDays { get; set; } = 30;

    /// <summary>
    /// When true (default), SHA-256 checksums are computed on write and stored in
    /// <c>_checksums.json</c> per entity directory. An HMAC-SHA256 signature in
    /// <c>_checksums.sig</c> guards manifest integrity (RGAP-6).
    /// Startup full-scan verification runs when enabled.
    /// </summary>
    public bool EnableChecksums { get; set; } = true;

    /// <summary>
    /// When true, every read verifies the file's SHA-256 against the manifest.
    /// Mismatches are quarantined (Phase F). Default is <c>false</c> because
    /// the startup full-scan already catches corruption; enabling this adds
    /// per-read overhead for defense-in-depth.
    /// </summary>
    public bool VerifyChecksumsOnRead { get; set; } = false;

    /// <summary>
    /// When true, an append-only JSONL event log records every entity change
    /// (created, updated, deleted) after each flush cycle. Enables point-in-time
    /// recovery and audit trail. Default is <c>false</c>.
    /// <para>
    /// <b>Phase H (RGAP-7):</b> When <see cref="EncryptAtRest"/> is also enabled,
    /// each JSONL line is individually encrypted (serialize → encrypt → base64 → line),
    /// preserving append-only semantics.
    /// </para>
    /// </summary>
    public bool EnableEventLog { get; set; } = false;

    /// <summary>
    /// Number of days to retain daily event log files before automatic purge
    /// during startup. Default is <c>7</c>. Set to <c>0</c> to keep logs forever.
    /// </summary>
    public int EventLogRetentionDays { get; set; } = 7;

    /// <summary>
    /// When true, periodic full-state ZIP snapshots are created for disaster recovery.
    /// Default is <c>false</c>.
    /// <para>
    /// <b>Phase I:</b> Snapshots capture all entity directories while holding all
    /// directory locks for consistency. The quarantine retry chain will attempt
    /// restoration from the latest snapshot before quarantining (Phase F cross-ref).
    /// </para>
    /// </summary>
    public bool EnableSnapshots { get; set; } = false;

    /// <summary>
    /// Interval in hours between automatic snapshot creation. Default is <c>24</c>.
    /// Only applies when <see cref="EnableSnapshots"/> is <c>true</c>.
    /// </summary>
    public int SnapshotIntervalHours { get; set; } = 24;

    /// <summary>
    /// Maximum number of snapshot files to retain. Oldest snapshots beyond this
    /// count are deleted. Default is <c>3</c>. Minimum effective value is <c>1</c>.
    /// </summary>
    public int SnapshotRetentionCount { get; set; } = 3;

    /// <summary>
    /// When true (default), <see cref="SharpClawDbContext.SaveChangesAsync"/>
    /// enqueues flush intents to a background <see cref="FlushWorker"/> instead
    /// of writing to disk synchronously. A write-through overlay in
    /// <see cref="ColdEntityStore"/> ensures reads always see the latest data.
    /// <para>
    /// <b>Phase K (CQRS-lite):</b> Improves perceived write latency by
    /// decoupling the EF commit from file I/O. The background worker processes
    /// the queue via the existing two-phase commit pipeline.
    /// </para>
    /// </summary>
    public bool AsyncFlush { get; set; } = true;

    /// <summary>
    /// Entity types that are skipped during <see cref="JsonFilePersistenceService.LoadAsync"/>
    /// to reduce memory pressure at startup. These are high-volume, append-heavy
    /// tables whose data will be loaded on demand in a future phase.
    /// </summary>
    public HashSet<Type> ColdEntityTypes { get; } =
    [
        typeof(ChatMessageDB),
        typeof(AgentJobDB),
        typeof(AgentJobLogEntryDB),
        typeof(TaskInstanceDB),
        typeof(TaskExecutionLogDB),
        typeof(TaskOutputEntryDB),
    ];
}
