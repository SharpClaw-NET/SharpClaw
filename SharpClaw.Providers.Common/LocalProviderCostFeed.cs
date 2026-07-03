using SharpClaw.Contracts.Providers;

namespace SharpClaw.Providers.Common;

/// <summary>
/// Built-in <see cref="IProviderCostFeed"/> for providers that run entirely
/// on the user's machine and incur no cloud cost. Always reports zero with
/// no daily breakdown. Local provider plugins register an instance via
/// a cost-feed factory to short-circuit cost reporting without the cost
/// service needing to know about provider identity.
/// </summary>
public sealed class LocalProviderCostFeed : IProviderCostFeed
{
    public static readonly LocalProviderCostFeed Instance = new();

    public Task<ProviderCostResult?> GetCostsAsync(
        DateTimeOffset startTime,
        DateTimeOffset? endTime,
        CancellationToken ct = default)
        => Task.FromResult<ProviderCostResult?>(
            new ProviderCostResult(0m, "usd", []));
}
