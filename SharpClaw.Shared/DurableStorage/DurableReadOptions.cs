namespace SharpClaw.Shared.DurableStorage;


public sealed record DurableReadOptions(
    int Take = 200,
    int MaxBytes = 262_144,
    string? MinimumLevel = null,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    string? Contains = null,
    long? ThroughSequence = null,
    long MaxScanBytes = 16 * 1024 * 1024);
