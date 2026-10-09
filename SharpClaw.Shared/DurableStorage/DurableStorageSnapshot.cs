namespace SharpClaw.Shared.DurableStorage;


public sealed record DurableStorageSnapshot(
    bool IsHealthy,
    string? DegradedReason,
    long EncodedBytes,
    int ActiveStreams,
    int ResidentStreams,
    long SealedSegments,
    DateTimeOffset? LastSuccessfulFlush);
