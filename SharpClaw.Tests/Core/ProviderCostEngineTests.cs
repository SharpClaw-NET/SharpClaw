using SharpClaw.Contracts.DTOs.Providers;
using SharpClaw.Contracts.Providers;

namespace SharpClaw.Tests.Core;

[TestFixture]
public sealed class ProviderCostEngineTests
{
    private readonly ProviderCostEngine _engine = new();

    [Test]
    public void ResolvePeriod_WhenNoExplicitDates_UsesNowMinusDays()
    {
        var now = new DateTimeOffset(2026, 6, 28, 12, 30, 0, TimeSpan.Zero);

        var period = _engine.ResolvePeriod(
            days: 7,
            startDate: null,
            endDate: null,
            now: now);

        period.End.Should().Be(now);
        period.Start.Should().Be(now.AddDays(-7));
    }

    [Test]
    public void ResolvePeriod_WhenExplicitDatesProvided_UsesThem()
    {
        var start = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
        var end = new DateTimeOffset(2026, 6, 20, 0, 0, 0, TimeSpan.Zero);

        var period = _engine.ResolvePeriod(
            days: 7,
            startDate: start,
            endDate: end,
            now: end.AddDays(100));

        period.Start.Should().Be(start);
        period.End.Should().Be(end);
    }

    [Test]
    public void CreateFeedResponse_WhenRemoteProvider_ProjectsProviderFeed()
    {
        var provider = new ProviderCostProvider(
            Guid.NewGuid(),
            "OpenAI",
            "openai");
        var start = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
        var end = new DateTimeOffset(2026, 6, 2, 0, 0, 0, TimeSpan.Zero);
        var result = new ProviderCostResult(
            12.34m,
            "usd",
            [new ProviderCostDailyBucket(start, end, 12.34m)]);

        var response = _engine.CreateFeedResponse(
            provider,
            isLocal: false,
            start,
            end,
            result);

        response.ProviderId.Should().Be(provider.Id);
        response.ProviderName.Should().Be("OpenAI");
        response.ProviderKey.Should().Be("openai");
        response.IsLocal.Should().BeFalse();
        response.CostApiSupported.Should().BeTrue();
        response.TotalCost.Should().Be(12.34m);
        response.Currency.Should().Be("usd");
        response.Note.Should().BeNull();
        response.DailyBreakdown.Should().Equal(
            new CostDailyBucket(start, end, 12.34m, "usd"));
    }

    [Test]
    public void CreateFeedResponse_WhenLocalProvider_AddsNoCloudCostNote()
    {
        var provider = new ProviderCostProvider(
            Guid.NewGuid(),
            "Local",
            "local");
        var now = new DateTimeOffset(2026, 6, 28, 0, 0, 0, TimeSpan.Zero);
        var result = new ProviderCostResult(0, "usd", []);

        var response = _engine.CreateFeedResponse(
            provider,
            isLocal: true,
            now.AddDays(-1),
            now,
            result);

        response.IsLocal.Should().BeTrue();
        response.Note.Should().Be("Local provider - no cloud API costs incurred.");
    }

    [Test]
    public void CreatePermissionDeniedResponse_UsesFallbackCurrencyAndPluginNote()
    {
        var provider = new ProviderCostProvider(
            Guid.NewGuid(),
            "OpenAI",
            "openai");
        var now = new DateTimeOffset(2026, 6, 28, 0, 0, 0, TimeSpan.Zero);

        var response = _engine.CreatePermissionDeniedResponse(
            provider,
            isLocal: false,
            now.AddDays(-1),
            now,
            "Use an admin key.");

        response.CostApiSupported.Should().BeTrue();
        response.TotalCost.Should().Be(0);
        response.Currency.Should().Be(ProviderCostEngine.DefaultFallbackCurrency);
        response.DailyBreakdown.Should().BeNull();
        response.Note.Should().Be("Use an admin key.");
    }

    [Test]
    public void CreateUnsupportedResponse_UsesFallbackCurrencyAndUnsupportedNote()
    {
        var provider = new ProviderCostProvider(
            Guid.NewGuid(),
            "Custom",
            "custom");
        var now = new DateTimeOffset(2026, 6, 28, 0, 0, 0, TimeSpan.Zero);

        var response = _engine.CreateUnsupportedResponse(
            provider,
            isLocal: false,
            now.AddDays(-1),
            now);

        response.CostApiSupported.Should().BeFalse();
        response.TotalCost.Should().Be(0);
        response.Currency.Should().Be(ProviderCostEngine.DefaultFallbackCurrency);
        response.Note.Should().Be(
            "Provider key 'custom' does not expose a cost API. "
            + "Check the provider's dashboard for billing information.");
    }

    [Test]
    public void CreateTotalResponse_SumsCostsAndChoosesFirstPositiveSupportedCurrency()
    {
        var periodStart = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
        var periodEnd = new DateTimeOffset(2026, 6, 2, 0, 0, 0, TimeSpan.Zero);
        var providers = new[]
        {
            new ProviderCostResponse(
                Guid.NewGuid(),
                "Unsupported",
                "custom",
                IsLocal: false,
                CostApiSupported: false,
                TotalCost: 0,
                Currency: "usd",
                PeriodStart: periodStart,
                PeriodEnd: periodEnd,
                DailyBreakdown: null,
                Note: null),
            new ProviderCostResponse(
                Guid.NewGuid(),
                "OpenAI",
                "openai",
                IsLocal: false,
                CostApiSupported: true,
                TotalCost: 4.50m,
                Currency: "eur",
                PeriodStart: periodStart,
                PeriodEnd: periodEnd,
                DailyBreakdown: null,
                Note: null),
            new ProviderCostResponse(
                Guid.NewGuid(),
                "Anthropic",
                "anthropic",
                IsLocal: false,
                CostApiSupported: true,
                TotalCost: 1.25m,
                Currency: "usd",
                PeriodStart: periodStart,
                PeriodEnd: periodEnd,
                DailyBreakdown: null,
                Note: null)
        };

        var response = _engine.CreateTotalResponse(
            periodStart,
            periodEnd,
            providers);

        response.TotalCost.Should().Be(5.75m);
        response.Currency.Should().Be("eur");
        response.Providers.Should().Equal(providers);
    }

    [Test]
    public void CreateTotalResponse_WhenNoPositiveSupportedCost_UsesFallbackCurrency()
    {
        var periodStart = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
        var periodEnd = new DateTimeOffset(2026, 6, 2, 0, 0, 0, TimeSpan.Zero);

        var response = _engine.CreateTotalResponse(
            periodStart,
            periodEnd,
            []);

        response.TotalCost.Should().Be(0);
        response.Currency.Should().Be(ProviderCostEngine.DefaultFallbackCurrency);
        response.Providers.Should().BeEmpty();
    }
}
