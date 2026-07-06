using JSONColdStore;

namespace SharpClaw.Runtime.INF.Persistence;

internal static class JsonColdStoreRegistration
{
    public static void ConfigureStore(
        JsonColdStoreOptionsBuilder store,
        JsonColdStoreStorageOptions options,
        JsonColdStoreEncryptionKey? encryptionKey)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(options);

        store.UseCompression(options.Compression);
        store.UseStartupMode(options.StartupMode);
        store.UseFullScanPolicy(options.FullScanPolicy);
        store.UseFsyncOnWrite(options.FsyncOnWrite);
        store.UseFlushRetry(
            options.FlushRetryMaxRetries,
            TimeSpan.FromMilliseconds(options.FlushRetryBaseDelayMilliseconds));
        store.UseTransactionReplay(options.TransactionReplayMaxRetries);
        store.UseReadRetry(
            options.ReadRetryMaxRetries,
            TimeSpan.FromMilliseconds(options.ReadRetryBaseDelayMilliseconds));
        store.UseQuarantine(TimeSpan.FromDays(Math.Max(0, options.QuarantineMaxAgeDays)));
        store.UseIndexMaintenance(TimeSpan.FromMinutes(Math.Max(0, options.IndexRescanIntervalMinutes)));
        store.UseEventLog(options.EnableEventLog, TimeSpan.FromDays(Math.Max(0, options.EventLogRetentionDays)));

        if (options.EnableChecksums)
            store.UseChecksums(verifyOnStartup: true, verifyOnRead: options.VerifyChecksumsOnRead);
        else
            store.DisableChecksums();

        if (options.EnableSnapshots)
        {
            store.UseSnapshots(
                enabled: true,
                interval: TimeSpan.FromHours(Math.Max(1, options.SnapshotIntervalHours)),
                retentionCount: Math.Max(1, options.SnapshotRetentionCount));
        }

        if (!options.EncryptAtRest)
            return;

        if (encryptionKey is null)
            throw new InvalidOperationException(
                "JSONColdStore encryption is enabled, but no encryption key is registered.");

        store.UseEncryptionKey(encryptionKey);
    }

    public static string GetModuleDirectory(JsonColdStoreStorageOptions options, Type dbContextType)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(dbContextType);

        var unsafeName = dbContextType.FullName ?? dbContextType.Name;
        var safeName = string.Join(
            "_",
            unsafeName.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));

        return Path.Combine(options.DataDirectory, "modules", safeName);
    }
}
