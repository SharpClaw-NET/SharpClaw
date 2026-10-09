namespace SharpClaw.Shared.DurableStorage;


public sealed record DurableRecordWrite(
    Guid RecordId,
    DateTimeOffset Timestamp,
    string Level,
    string EventName,
    string Message,
    string? ExceptionType = null,
    string? CorrelationId = null,
    DurableArtifactReference? Artifact = null,
    bool Idempotent = false,
    string? ExceptionText = null,
    string? MessageTemplate = null,
    string? Category = null,
    int? EventIdId = null,
    string? EventIdName = null,
    string? TraceId = null,
    string? SpanId = null,
    IReadOnlyDictionary<string, string>? Properties = null);
