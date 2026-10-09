namespace SharpClaw.Shared.DurableStorage;


public sealed record DurableRecord(
    long Sequence,
    Guid RecordId,
    DateTimeOffset Timestamp,
    string Level,
    string EventName,
    string Message,
    string? ExceptionType,
    string? CorrelationId,
    DurableArtifactReference? Artifact,
    string? ExceptionText = null,
    string? MessageTemplate = null,
    string? Category = null,
    int? EventIdId = null,
    string? EventIdName = null,
    string? TraceId = null,
    string? SpanId = null,
    IReadOnlyDictionary<string, string>? Properties = null);
