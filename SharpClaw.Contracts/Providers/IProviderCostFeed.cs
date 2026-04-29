namespace SharpClaw.Contracts.Providers;

/// <summary>
/// Optional live-cost reporting surface contributed by a provider plugin.
/// Plugins for providers that expose a billing/usage API (e.g. OpenAI's
/// Organization Costs endpoint) return a non-null
/// <see cref="IProviderPlugin.CostFeed"/>; everyone else returns
/// <see langword="null"/>. The Core cost service consults the plugin
/// instead of casting the API client, keeping protocol-shape concerns
/// out of the pipeline.
/// </summary>
public interface IProviderCostFeed
{
    /// <summary>
    /// Fetches aggregated cost data from the provider's billing/usage API.
    /// </summary>
    /// <returns>
    /// A <see cref="ProviderCostResult"/> with daily buckets and totals,
    /// or <see langword="null"/> if the API key lacks the required
    /// permissions (e.g. OpenAI admin key requirement) or the request
    /// otherwise fails in a recoverable way.
    /// </returns>
    Task<ProviderCostResult?> GetCostsAsync(
        HttpClient httpClient,
        string apiKey,
        DateTimeOffset startTime,
        DateTimeOffset? endTime,
        CancellationToken ct = default);
}

public sealed record ProviderCostResult(
    decimal TotalAmount,
    string Currency,
    IReadOnlyList<ProviderCostDailyBucket> DailyBuckets);

public sealed record ProviderCostDailyBucket(
    DateTimeOffset Start,
    DateTimeOffset End,
    decimal Amount);
