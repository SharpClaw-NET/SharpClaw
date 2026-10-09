namespace SharpClaw.Shared.DurableStorage;


public sealed record DurableOperationalStreamCatalog(
    IReadOnlyList<DurableOperationalStreamSummary> Streams,
    IReadOnlyList<DurableOperationalStreamIdentityGap> IdentityGaps,
    bool HasMore,
    int ScannedDirectories,
    long ScannedBytes);
