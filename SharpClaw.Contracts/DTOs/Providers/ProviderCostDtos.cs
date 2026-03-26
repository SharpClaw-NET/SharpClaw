using SharpClaw.Contracts.Enums;

namespace SharpClaw.Contracts.DTOs.Providers;

public sealed record ProviderCostResponse(
    Guid ProviderId,
    string ProviderName,
    ProviderType ProviderType,
    bool IsLocal,
    bool CostApiSupported,
    decimal TotalCost,
    string Currency,
    DateTimeOffset PeriodStart,
    DateTimeOffset PeriodEnd,
    IReadOnlyList<CostDailyBucket>? DailyBreakdown,
    string? Note);

public sealed record CostDailyBucket(
    DateTimeOffset Start,
    DateTimeOffset End,
    decimal Amount,
    string Currency);

public sealed record ProviderCostTotalResponse(
    decimal TotalCost,
    string Currency,
    DateTimeOffset PeriodStart,
    DateTimeOffset PeriodEnd,
    IReadOnlyList<ProviderCostResponse> Providers);

/// <summary>
/// Simplified cost summary returned by <c>GET /providers/cost/total?simple=true</c>.
/// </summary>
public sealed record ProviderCostSimpleResponse(
    decimal TotalCost,
    string Currency,
    DateTimeOffset PeriodStart,
    DateTimeOffset PeriodEnd,
    string Summary,
    IReadOnlyList<string>? UntrackedProviders);
