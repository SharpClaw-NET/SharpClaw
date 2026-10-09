namespace SharpClaw.Shared.DurableStorage;


public sealed record DurableArtifactReference(
    Guid Id,
    string MediaType,
    long Length,
    string Sha256,
    string? Preview = null);
