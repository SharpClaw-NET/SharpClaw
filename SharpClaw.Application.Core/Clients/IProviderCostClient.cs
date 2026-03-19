namespace SharpClaw.Application.Core.Clients;

/// <summary>
/// Optional interface for provider API clients that expose a cost or usage
/// billing endpoint. Providers that do not offer such an API simply do not
/// implement this interface; the cost service will report them as unsupported.
/// </summary>
public interface IProviderCostClient
{
    /// <summary>
    /// Fetches aggregated cost data from the provider's billing/usage API.
    /// </summary>
    /// <returns>
    /// A <see cref="ProviderCostResult"/> with daily buckets and totals,
    /// or <see langword="null"/> if the API key lacks the required permissions
    /// (e.g. OpenAI admin key requirement).
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
