namespace SharpClaw.Shared.DurableStorage;


public sealed record DurableStreamSummary(
    long RecordCount,
    long? LastSequence,
    DateTimeOffset? LastTimestamp,
    long EncodedBytes,
    long FirstAvailableSequence,
    long ExpiredRecordCount);
