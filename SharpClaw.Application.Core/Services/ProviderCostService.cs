using SharpClaw.Contracts.DTOs.Providers;
using SharpClaw.Core.Providers;

namespace SharpClaw.Application.Services;

public sealed class ProviderCostService(
    ProviderCostEngine providerCosts,
    EfProviderCostHost providerCostHost)
{
    /// <summary>
    /// Fetches cost data for a single provider. If the provider exposes a
    /// billing API and the API key has sufficient privileges, real cost data
    /// is returned. Otherwise the Core workflow returns the supported,
    /// permission-denied, or unsupported response shape.
    /// </summary>
    public async Task<ProviderCostResponse?> GetCostAsync(
        Guid providerId,
        int days = 30,
        DateTimeOffset? startDate = null,
        DateTimeOffset? endDate = null,
        CancellationToken ct = default)
    {
        return await providerCosts.GetCostAsync(
            providerId,
            days,
            startDate,
            endDate,
            providerCostHost,
            ct);
    }

    /// <summary>
    /// Fetches cost data for configured providers and aggregates the results
    /// into a total report.
    /// </summary>
    public async Task<ProviderCostTotalResponse> GetTotalCostAsync(
        int days = 30,
        DateTimeOffset? startDate = null,
        DateTimeOffset? endDate = null,
        bool includeAll = false,
        CancellationToken ct = default)
    {
        return await providerCosts.GetTotalCostAsync(
            days,
            startDate,
            endDate,
            includeAll,
            providerCostHost,
            ct);
    }
}
