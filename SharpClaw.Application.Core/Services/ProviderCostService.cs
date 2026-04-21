using Microsoft.EntityFrameworkCore;
using SharpClaw.Application.Core.Clients;
using SharpClaw.Contracts.DTOs.Providers;
using SharpClaw.Contracts.Enums;
using SharpClaw.Contracts.Persistence;
using SharpClaw.Infrastructure.Persistence;
using SharpClaw.Utils.Security;

namespace SharpClaw.Application.Services;

public sealed class ProviderCostService(
    SharpClawDbContext db,
    EncryptionOptions encryptionOptions,
    ProviderApiClientFactory clientFactory,
    IHttpClientFactory httpClientFactory)
{
    /// <summary>
    /// Fetches cost data for a single provider. If the provider exposes a
    /// billing API (e.g. OpenAI) and the API key has sufficient privileges,
    /// real cost data is returned. Otherwise a response is returned with
    /// <see cref="ProviderCostResponse.CostApiSupported"/> = <see langword="false"/>
    /// and an explanatory note.
    /// </summary>
    public async Task<ProviderCostResponse?> GetCostAsync(
        Guid providerId, int days = 30,
        DateTimeOffset? startDate = null, DateTimeOffset? endDate = null,
        CancellationToken ct = default)
    {
        var provider = await db.Providers.FindAsync([providerId], ct);
        if (provider is null) return null;

        var (periodStart, periodEnd) = ResolvePeriod(days, startDate, endDate);

        // LlamaSharp and Whisper providers have zero cloud cost
        if (provider.ProviderType is ProviderType.LlamaSharp or ProviderType.Whisper)
        {
            return new ProviderCostResponse(
                provider.Id, provider.Name, provider.ProviderType,
                IsLocal: true, CostApiSupported: false,
                TotalCost: 0, Currency: "usd",
                PeriodStart: periodStart, PeriodEnd: periodEnd,
                DailyBreakdown: null,
                Note: "Local provider — no cloud API costs incurred.");
        }

        // Try the provider's cost API (if implemented)
        var client = GetClientSafe(provider.ProviderType, provider.ApiEndpoint);
        if (client is IProviderCostClient costClient
            && !string.IsNullOrEmpty(provider.EncryptedApiKey))
        {
            var apiKey = ApiKeyEncryptor.DecryptOrPassthrough(provider.EncryptedApiKey, encryptionOptions.Key);
            using var httpClient = httpClientFactory.CreateClient();

            var result = await costClient.GetCostsAsync(httpClient, apiKey, periodStart, periodEnd, ct);
            if (result is not null)
            {
                return new ProviderCostResponse(
                    provider.Id, provider.Name, provider.ProviderType,
                    IsLocal: false, CostApiSupported: true,
                    TotalCost: result.TotalAmount,
                    Currency: result.Currency,
                    PeriodStart: periodStart, PeriodEnd: periodEnd,
                    DailyBreakdown: result.DailyBuckets
                        .Select(b => new CostDailyBucket(b.Start, b.End, b.Amount, result.Currency))
                        .ToList(),
                    Note: null);
            }

            // API returned null — key likely lacks admin permissions
            return new ProviderCostResponse(
                provider.Id, provider.Name, provider.ProviderType,
                IsLocal: false, CostApiSupported: true,
                TotalCost: 0, Currency: "usd",
                PeriodStart: periodStart, PeriodEnd: periodEnd,
                DailyBreakdown: null,
                Note: "Cost API is available for this provider but the current API key "
                    + "lacks the required permissions (e.g. OpenAI requires an admin key). "
                    + "Update the API key to an admin key to retrieve cost data.");
        }

        // Provider does not implement a cost API
        return new ProviderCostResponse(
            provider.Id, provider.Name, provider.ProviderType,
            IsLocal: false, CostApiSupported: false,
            TotalCost: 0, Currency: "usd",
            PeriodStart: periodStart, PeriodEnd: periodEnd,
            DailyBreakdown: null,
            Note: $"Provider type '{provider.ProviderType}' does not expose a cost API. "
                + "Check the provider's dashboard for billing information.");
    }

    /// <summary>
    /// Fetches cost data for configured providers and aggregates the results
    /// into a total report.
    /// </summary>
    /// <param name="days">Number of days to look back (default 30).</param>
    /// <param name="startDate">Explicit period start (overrides <paramref name="days"/>).</param>
    /// <param name="endDate">Explicit period end.</param>
    /// <param name="includeAll">
    /// When <see langword="false"/> (default), only providers that have an API
    /// key configured are included. When <see langword="true"/>, all providers
    /// are included regardless of API key status.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<ProviderCostTotalResponse> GetTotalCostAsync(
        int days = 30,
        DateTimeOffset? startDate = null, DateTimeOffset? endDate = null,
        bool includeAll = false,
        CancellationToken ct = default)
    {
        var (periodStart, periodEnd) = ResolvePeriod(days, startDate, endDate);

        var query = db.Providers.AsQueryable();
        if (!includeAll)
            query = query.Where(p => p.EncryptedApiKey != null && p.EncryptedApiKey != "");

        var providers = await query.ToListAsync(ct);
        var results = new List<ProviderCostResponse>(providers.Count);

        foreach (var provider in providers)
        {
            var cost = await GetCostAsync(provider.Id, days, startDate, endDate, ct);
            if (cost is not null)
                results.Add(cost);
        }

        var totalCost = results.Sum(r => r.TotalCost);
        var currency = results.FirstOrDefault(r => r.CostApiSupported && r.TotalCost > 0)?.Currency ?? "usd";

        return new ProviderCostTotalResponse(totalCost, currency, periodStart, periodEnd, results);
    }

    private static (DateTimeOffset Start, DateTimeOffset End) ResolvePeriod(
        int days, DateTimeOffset? startDate, DateTimeOffset? endDate)
    {
        var end = endDate ?? DateTimeOffset.UtcNow;
        var start = startDate ?? end.AddDays(-days);
        return (start, end);
    }

    private IProviderApiClient? GetClientSafe(ProviderType providerType, string? apiEndpoint)
    {
        try
        {
            return clientFactory.GetClient(providerType, apiEndpoint);
        }
        catch
        {
            return null;
        }
    }
}
