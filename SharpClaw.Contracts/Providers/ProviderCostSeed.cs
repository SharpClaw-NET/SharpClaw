namespace SharpClaw.Contracts.Providers;

/// <summary>
/// Per-million-token cost seed contributed by a provider plugin.
/// Aggregated by the cost service at startup to populate
/// <c>ProviderCostDB</c> rows for new (provider, model) pairs without
/// overwriting operator edits.
/// </summary>
public sealed record ProviderCostSeed(
    string ModelName,
    decimal InputCostPerMillion,
    decimal OutputCostPerMillion,
    string Currency = "usd");
