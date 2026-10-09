namespace SharpClaw.Shared.DurableStorage;


public sealed record DurableAppendReceipt(
    long Sequence,
    long RecordCount,
    DateTimeOffset Timestamp);
