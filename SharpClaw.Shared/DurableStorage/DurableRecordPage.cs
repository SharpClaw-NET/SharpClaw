namespace SharpClaw.Shared.DurableStorage;


public sealed record DurableRecordPage(
    IReadOnlyList<DurableRecord> Records,
    long? NextSequence,
    bool HasMore,
    int ReturnedBytes,
    long SnapshotLastSequence,
    long FirstAvailableSequence,
    long ExpiredRecordCount);
