namespace SharpClaw.Shared.DurableStorage;


public sealed class DurableRetentionOptions
{
    public TimeSpan JobLogAge { get; init; } = TimeSpan.FromDays(30);
    public TimeSpan ProcessLogAge { get; init; } = TimeSpan.FromDays(14);
    public TimeSpan RegistrationLogAge { get; init; } = TimeSpan.FromDays(14);
    public long MaximumEncodedBytes { get; init; } = 10L * 1024 * 1024 * 1024;
    public long MinimumFreeBytes { get; init; } = 1024L * 1024 * 1024;
    public int MaximumDeletesPerRun { get; init; } = 10_000;

    public TimeSpan GetMaximumAge(DurableStreamKind kind) => kind switch
    {
        DurableStreamKind.JobLog => JobLogAge,
        DurableStreamKind.ProcessLog => ProcessLogAge,
        DurableStreamKind.RegistrationLog => RegistrationLogAge,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}
