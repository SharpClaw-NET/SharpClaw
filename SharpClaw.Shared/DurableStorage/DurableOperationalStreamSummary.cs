namespace SharpClaw.Shared.DurableStorage;


public sealed record DurableOperationalStreamSummary(
    DurableStreamKey Stream,
    string? AppName,
    string? SourceId,
    Guid BootId,
    bool HasActiveSegment,
    bool HasSealedSegments,
    long RecordCount,
    long EncodedBytes,
    long FirstSequence,
    long? LastSequence,
    long FirstAvailableSequence,
    long ExpiredRecordCount,
    DateTimeOffset? LastTimestamp);
