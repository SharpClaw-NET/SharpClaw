namespace SharpClaw.Shared.DurableStorage;


public sealed record DurableOperationalStreamIdentityGap(
    DurableStreamKind Kind,
    string StreamHash,
    string Reason);
