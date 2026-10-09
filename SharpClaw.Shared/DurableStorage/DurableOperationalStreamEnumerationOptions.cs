namespace SharpClaw.Shared.DurableStorage;


public sealed class DurableOperationalStreamEnumerationOptions
{
    public const int HardMaximumEntries = 1024;
    public const long HardMaximumScanBytes = 64L * 1024 * 1024;
    public static readonly TimeSpan HardMaximumDuration = TimeSpan.FromSeconds(30);

    public int MaxEntries { get; init; } = 100;
    public long MaxScanBytes { get; init; } = 4L * 1024 * 1024;
    public TimeSpan MaxDuration { get; init; } = TimeSpan.FromSeconds(2);
}
