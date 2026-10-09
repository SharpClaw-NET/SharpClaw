namespace SharpClaw.Shared.DurableStorage;


public sealed record DurableRetentionResult(
    int DeletedSegments,
    long ReclaimedBytes,
    long RemainingEncodedBytes,
    long AvailableFreeBytes,
    bool QuotaSatisfied,
    DateTimeOffset CompletedAt);
