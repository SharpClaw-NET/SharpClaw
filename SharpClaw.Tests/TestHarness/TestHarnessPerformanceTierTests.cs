using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SharpClaw.Application.API.Handlers;
using SharpClaw.Application.Core.Modules;
using SharpClaw.Application.Services;
using SharpClaw.Contracts.DTOs.AgentActions;
using SharpClaw.Contracts.DTOs.Chat;
using SharpClaw.Contracts.Entities.Core;
using SharpClaw.Contracts.Entities.Core.Access;
using SharpClaw.Contracts.Entities.Core.Clearance;
using SharpClaw.Contracts.Entities.Core.Context;
using SharpClaw.Contracts.Entities.Core.Jobs;
using SharpClaw.Contracts.Enums;
using SharpClaw.Contracts.Modules;
using SharpClaw.Contracts.Persistence;
using SharpClaw.Contracts.Providers;
using SharpClaw.Infrastructure.Persistence;
using SharpClaw.Modules.AgentOrchestration;
using SharpClaw.Modules.TestHarness;

namespace SharpClaw.Tests.TestHarness;

[TestFixture]
public sealed class TestHarnessPerformanceTierTests
{
    private const int HotPathSampleCount = 20;
    private const double HotPathAverageBudgetMs = 5;
    private const double HotPathOutlierBudgetMs = 25;

    private static readonly ConcurrentDictionary<string, Lazy<Task<long>>> Measurements = new();
    private static readonly ConcurrentDictionary<string, Lazy<Task<HotPathSampleMeasurement>>> HotPathMeasurements = new();
    private static readonly ConcurrentDictionary<string, Lazy<Task<SurfaceLatencyMeasurement>>> LatencyMeasurements = new();
    private static readonly ConcurrentDictionary<string, Lazy<Task<StreamingSurfaceSetMeasurement>>> SurfaceSetMeasurements = new();
    private static readonly ConcurrentDictionary<string, Lazy<Task<ToolRoundTripMeasurement>>> ToolMeasurements = new();
    private static readonly ConcurrentDictionary<string, Lazy<Task<AccessibleThreadsLoadMeasurement>>> AccessibleMeasurements = new();

    [TestCaseSource(nameof(StreamingAllSurfaceTiers))]
    [Category(HarnessTestCategories.PerformanceDiagnostic)]
    public async Task StreamingOverhead_1000ms_AllSurfaces_Tiered(int percent, int expectedMaxMs)
    {
        HarnessDiagnostics.RequireEnabled();
        var measured = await CachedSurfaceSetAsync("stream-all-1000", () => MeasureAllStreamingSurfacesAsync(1000));
        var allowedOverheadMs = expectedMaxMs - 1000;
        measured.MaxSharpClawOverheadMs.Should().BeLessThanOrEqualTo(
            allowedOverheadMs,
            $"all streaming surfaces must stay under {percent}% overhead for a 1000ms provider baseline; " +
            measured.Describe());
    }

    [TestCaseSource(nameof(ApiStreamBaselineTiers))]
    [Category(HarnessTestCategories.PerformanceDiagnostic)]
    public async Task StreamingOverhead_ApiStream_Baseline_Tiered(int providerMs, int percent)
    {
        HarnessDiagnostics.RequireEnabled();
        var measured = await CachedLatencyAsync(
            $"api-stream-{providerMs}",
            () => MeasureApiStreamAsync(providerMs));
        HarnessBudget.AssertSharpClawOverheadPercent(
            measured.ClientVisibleMs,
            measured.ProviderActualMs,
            providerMs,
            percent,
            $"api stream {providerMs}ms");
    }

    [Test]
    [Category(HarnessTestCategories.PerformanceDiagnostic)]
    public async Task StreamingOverhead_1000ms_SSE_Under100ms()
    {
        HarnessDiagnostics.RequireEnabled();
        var measured = await CachedLatencyAsync("sse-1000", () => MeasureSseAsync(1000));
        HarnessBudget.AssertSharpClawOverheadAbsolute(
            measured.ClientVisibleMs,
            measured.ProviderActualMs,
            measured.ProviderConfiguredMs,
            100,
            "SSE 1000ms");
    }

    [Test]
    [Category(HarnessTestCategories.PerformanceDiagnostic)]
    public async Task StreamingOverhead_1000ms_ApiStream_Under100ms()
    {
        HarnessDiagnostics.RequireEnabled();
        var measured = await CachedLatencyAsync("api-stream-1000", () => MeasureApiStreamAsync(1000));
        HarnessBudget.AssertSharpClawOverheadAbsolute(
            measured.ClientVisibleMs,
            measured.ProviderActualMs,
            measured.ProviderConfiguredMs,
            100,
            "API stream 1000ms");
    }

    [Test]
    [Category(HarnessTestCategories.PerformanceDiagnostic)]
    public async Task StreamingOverhead_1000ms_Gateway_Under100ms()
    {
        HarnessDiagnostics.RequireEnabled();
        var measured = await CachedLatencyAsync("gateway-1000", () => MeasureGatewayForwardingAsync(1000));
        HarnessBudget.AssertSharpClawOverheadAbsolute(
            measured.ClientVisibleMs,
            measured.ProviderActualMs,
            measured.ProviderConfiguredMs,
            100,
            "Gateway 1000ms");
    }

    [Test]
    [Category(HarnessTestCategories.PerformanceDiagnostic)]
    public async Task StreamingOverhead_1000ms_NonStreaming_Under100ms()
    {
        HarnessDiagnostics.RequireEnabled();
        var measured = await CachedLatencyAsync("non-stream-1000", () => MeasureNonStreamingAsync(1000));
        HarnessBudget.AssertSharpClawOverheadAbsolute(
            measured.ClientVisibleMs,
            measured.ProviderActualMs,
            measured.ProviderConfiguredMs,
            100,
            "non-streaming 1000ms");
    }

    [Test]
    [Category(HarnessTestCategories.PerformanceGate)]
    public async Task PerformanceGate_StreamingOverhead_1000ms_AllSurfaces_Under100ms()
    {
        var measured = await CachedSurfaceSetAsync("stream-all-1000", () => MeasureAllStreamingSurfacesAsync(1000));
        measured.MaxSharpClawOverheadMs.Should().BeLessThanOrEqualTo(
            100,
            "streaming surfaces must stay within a tight user-visible forwarding budget; " +
            measured.Describe());
    }

    [Test]
    [Category(HarnessTestCategories.PerformanceGate)]
    public async Task PerformanceGate_StreamingOverhead_100ms_ApiStream_Under25ms()
    {
        var measured = await CachedLatencyAsync("api-stream-100", () => MeasureApiStreamAsync(100));
        HarnessBudget.AssertSharpClawOverheadAbsolute(
            measured.ClientVisibleMs,
            measured.ProviderActualMs,
            measured.ProviderConfiguredMs,
            25,
            "API stream 100ms gate");
    }

    [Test]
    [Category(HarnessTestCategories.PerformanceGate)]
    public async Task PerformanceGate_NonStreamingOverhead_1000ms_Under50ms()
    {
        var measured = await CachedLatencyAsync("non-stream-1000", () => MeasureNonStreamingAsync(1000));
        HarnessBudget.AssertSharpClawOverheadAbsolute(
            measured.ClientVisibleMs,
            measured.ProviderActualMs,
            measured.ProviderConfiguredMs,
            50,
            "non-streaming 1000ms gate");
    }

    [Test]
    [Category(HarnessTestCategories.PerformanceGate)]
    public async Task PerformanceGate_StreamingOverhead_Large1000ChunkStream_Under100ms()
    {
        var measured = await MeasureApiStreamAsync(0, Enumerable.Repeat("x", 1000).ToArray());
        measured.SharpClawOverheadMs.Should().BeLessThanOrEqualTo(100, measured.Describe());
    }

    [Test]
    [Category(HarnessTestCategories.PerformanceGate)]
    public async Task PerformanceGate_StreamingOverhead_ApiSse1000TinyChunks_Under250ms()
    {
        var measured = await MeasureApiSseAsync(Enumerable.Repeat("x", 1000).ToArray());
        measured.SharpClawOverheadMs.Should().BeLessThanOrEqualTo(250, measured.Describe());
    }

    [Test]
    [Category(HarnessTestCategories.PerformanceDiagnostic)]
    public async Task StreamingOverhead_FewLargeChunksVersusManyTinyChunks_WithinSameBand()
    {
        HarnessDiagnostics.RequireEnabled();
        var fewLarge = await MeasureApiStreamAsync(250, [new string('a', 16_000), new string('b', 16_000)]);
        var manyTiny = await MeasureApiStreamAsync(250, Enumerable.Repeat("x", 500).ToArray());

        Math.Abs(fewLarge.SharpClawOverheadMs - manyTiny.SharpClawOverheadMs)
            .Should().BeLessThan(250, $"few-large={fewLarge.Describe()}, many-tiny={manyTiny.Describe()}");
    }

    [Test]
    [Category(HarnessTestCategories.PerformanceGate)]
    public async Task StreamingOverhead_FirstTokenLatencyVersusCompletionLatency_WithinSameBudget()
    {
        var firstToken = await MeasureApiStreamAsync(
            500,
            ["first", "last"],
            firstTokenDelayMs: 500,
            completionDelayMs: 0);
        var completion = await MeasureApiStreamAsync(
            500,
            ["first", "last"],
            firstTokenDelayMs: 0,
            completionDelayMs: 500);

        HarnessBudget.AssertSharpClawOverheadAbsolute(
            firstToken.ClientVisibleMs,
            firstToken.ProviderActualMs,
            firstToken.ProviderConfiguredMs,
            50,
            "first-token latency");
        HarnessBudget.AssertSharpClawOverheadAbsolute(
            completion.ClientVisibleMs,
            completion.ProviderActualMs,
            completion.ProviderConfiguredMs,
            50,
            "completion latency");
    }

    [TestCase(10)]
    [TestCase(25)]
    [TestCase(50)]
    [TestCase(100)]
    [Category(HarnessTestCategories.PerformanceDiagnostic)]
    public async Task AccessibleThreads_List_0Threads_TieredAbsolute(int maxMs)
    {
        HarnessDiagnostics.RequireEnabled();
        var measured = await CachedAsync("accessible-0", MeasureAccessibleThreadsZeroAsync);
        measured.Should().BeLessThanOrEqualTo(maxMs);
    }

    [Test]
    [Category(HarnessTestCategories.PerformanceGate)]
    public async Task PerformanceGate_AccessibleThreads_List_0Threads_Under10ms()
    {
        var measured = await CachedAsync("accessible-0", MeasureAccessibleThreadsZeroAsync);
        measured.Should().BeLessThanOrEqualTo(10);
    }

    [Test]
    [Category(HarnessTestCategories.PerformanceGate)]
    public async Task PerformanceGate_AccessibleThreads_List_1000Threads_ColdAndRepeatUnder500ms()
    {
        var measured = await CachedAccessibleThreadsAsync(
            "accessible-1000",
            () => MeasureAccessibleThreadsLoadAsync(1000));

        measured.ResultCount.Should().Be(1000);
        measured.ColdMs.Should().BeLessThanOrEqualTo(500, measured.Describe());
        measured.RepeatMs.Should().BeLessThanOrEqualTo(500, measured.Describe());
    }

    [TestCase(5)]
    [TestCase(10)]
    [TestCase(25)]
    [TestCase(50)]
    [Category(HarnessTestCategories.PerformanceDiagnostic)]
    public async Task InlineToolPermissionCheck_TieredAbsolute(int maxMs)
    {
        HarnessDiagnostics.RequireEnabled();
        var measured = await CachedAsync("inline-permission", MeasureInlinePermissionCheckAsync);
        measured.Should().BeLessThanOrEqualTo(maxMs);
    }

    [Test]
    [Category(HarnessTestCategories.PerformanceGate)]
    public async Task PerformanceGate_InlineToolPermissionCheck_Under5ms()
    {
        var measured = await CachedAsync("inline-permission", MeasureInlinePermissionCheckAsync);
        measured.Should().BeLessThanOrEqualTo(5);
    }

    [TestCaseSource(nameof(ToolCallRoundTripTiers))]
    [Category(HarnessTestCategories.PerformanceDiagnostic)]
    public async Task ToolCallRoundTrip_ProviderToolProvider_Tiered(int providerMs, int percent)
    {
        HarnessDiagnostics.RequireEnabled();
        var measured = await CachedToolAsync(
            $"tool-roundtrip-{providerMs}",
            () => MeasureToolRoundTripAsync(providerMs));
        HarnessBudget.AssertSharpClawOverheadPercent(
            measured.ClientVisibleMs,
            measured.ProviderActualMs,
            providerMs,
            percent,
            "tool round trip");
    }

    [Test]
    [Category(HarnessTestCategories.PerformanceGate)]
    public async Task WarmChatPath_NoStorageReads()
    {
        await using var host = ChatHarnessHost.Create(new Dictionary<string, string?>
        {
            ["Chat:CacheMaxMegabytes"] = "16"
        });
        var seeded = await host.SeedChatAsync(TestHarnessConstants.PlainProviderKey);
        await host.Chat.SendMessageAsync(seeded.Channel.Id, new ChatRequest("warm"));

        host.PersistenceCounter.Reset();
        await host.Chat.GetChannelCostAsync(seeded.Channel.Id);
        await host.Chat.GetAgentCostAsync(seeded.Agent.Id);

        host.PersistenceCounter.QueryCalls.Should().Be(0);
    }

    [Test]
    [Category(HarnessTestCategories.PerformanceDiagnostic)]
    public async Task WarmChatPath_Under5ms()
    {
        HarnessDiagnostics.RequireEnabled();
        var measured = await CachedHotPathAsync("warm-cost-samples", MeasureWarmCostLookupSamplesAsync);
        AssertHotPathBudget(measured, "warm cost lookup");
    }

    [Test]
    [Category(HarnessTestCategories.PerformanceDiagnostic)]
    public async Task PromptAssembly_AllDynamicDisabled_Under5ms()
    {
        HarnessDiagnostics.RequireEnabled();
        var measured = await CachedHotPathAsync(
            "prompt-disabled-samples",
            () => MeasurePromptAssemblySamplesAsync(dynamicEnabled: false));
        AssertHotPathBudget(measured, "prompt assembly with dynamic text disabled");
    }

    [Test]
    [Category(HarnessTestCategories.PerformanceGate)]
    public async Task PerformanceGate_PromptAssembly_AllDynamicDisabled_Under5ms()
    {
        var measured = await CachedHotPathAsync(
            "prompt-disabled-samples",
            () => MeasurePromptAssemblySamplesAsync(dynamicEnabled: false));
        AssertHotPathBudget(measured, "prompt assembly with dynamic text disabled");
    }

    [TestCase(25)]
    [TestCase(50)]
    [TestCase(100)]
    [Category(HarnessTestCategories.PerformanceDiagnostic)]
    public async Task PromptAssembly_AllDynamicEnabled_TieredAbsolute(int maxMs)
    {
        HarnessDiagnostics.RequireEnabled();
        var measured = await CachedAsync("prompt-enabled", () => MeasurePromptAssemblyAsync(dynamicEnabled: true));
        measured.Should().BeLessThanOrEqualTo(maxMs);
    }

    [Test]
    [Category(HarnessTestCategories.PerformanceGate)]
    public async Task PerformanceGate_PromptAssembly_AllDynamicEnabled_Under25ms()
    {
        var measured = await CachedAsync("prompt-enabled", () => MeasurePromptAssemblyAsync(dynamicEnabled: true));
        measured.Should().BeLessThanOrEqualTo(25);
    }

    [Test]
    [Category(HarnessTestCategories.PerformanceDiagnostic)]
    public async Task CostLookup_Warm_Under5ms()
    {
        HarnessDiagnostics.RequireEnabled();
        var measured = await CachedHotPathAsync("warm-cost-samples", MeasureWarmCostLookupSamplesAsync);
        AssertHotPathBudget(measured, "warm cost lookup");
    }

    [Test]
    [Category(HarnessTestCategories.PerformanceGate)]
    public async Task PerformanceGate_CostLookup_Warm_Under5ms()
    {
        var measured = await CachedHotPathAsync("warm-cost-samples", MeasureWarmCostLookupSamplesAsync);
        AssertHotPathBudget(measured, "warm cost lookup");
    }

    [TestCase(2)]
    [TestCase(5)]
    [TestCase(10)]
    [Category(HarnessTestCategories.PerformanceDiagnostic)]
    public async Task ProviderResolution_Warm_TieredAbsolute(int maxMs)
    {
        HarnessDiagnostics.RequireEnabled();
        var measured = await CachedAsync("provider-resolution", MeasureProviderResolutionAsync);
        measured.Should().BeLessThanOrEqualTo(maxMs);
    }

    [Test]
    [Category(HarnessTestCategories.PerformanceGate)]
    public async Task PerformanceGate_ProviderResolution_Warm_Under5ms()
    {
        var measured = await CachedAsync("provider-resolution", MeasureProviderResolutionAsync);
        measured.Should().BeLessThanOrEqualTo(5);
    }

    [Test]
    [Category(HarnessTestCategories.PerformanceGate)]
    public async Task PerformanceGate_TenConcurrentStreams_1000msProviderDelay_Under1200ms()
    {
        var sw = Stopwatch.StartNew();
        var tasks = Enumerable.Range(0, 10)
            .Select(_ => MeasureApiStreamAsync(1000))
            .ToArray();
        await Task.WhenAll(tasks);
        sw.Stop();

        HarnessBudget.AssertOverheadAbsolute(sw.ElapsedMilliseconds, 1000, 200, "10 concurrent streams");
    }

    [Test]
    [Category(HarnessTestCategories.PerformanceGate)]
    public async Task PerformanceGate_FiftyConcurrentShortChats_ZeroProviderDelay_Under200ms()
    {
        await using var host = ChatHarnessHost.Create(new Dictionary<string, string?>
        {
            ["Chat:DisableDefaultHeaders"] = "true",
            ["Chat:DisableDefaultSystemPrompt"] = "true",
            ["Chat:DisableHeaderTagExpansion"] = "true",
            ["Chat:DisableModuleHeaderTags"] = "true",
            ["AgentOrchestration:DisableAccessibleThreadsHeader"] = "true",
            ["Chat:CacheMaxMegabytes"] = "16"
        });
        var seeded = await host.SeedChatAsync(
            TestHarnessConstants.PlainProviderKey,
            agentSystemPrompt: "p",
            disableToolSchemas: true);
        host.Harness.ConfigureProvider(
            TestHarnessConstants.PlainProviderKey,
            new TestHarnessProviderScenario
            {
                Turns =
                [
                    new TestHarnessProviderTurn
                    {
                        Content = "ok",
                        Usage = new TokenUsage(1, 1)
                    }
                ]
            });
        await host.Chat.SendMessageAsync(seeded.Channel.Id, new ChatRequest("warm short chat"));
        host.Harness.Reset();
        host.Harness.ConfigureProvider(
            TestHarnessConstants.PlainProviderKey,
            new TestHarnessProviderScenario
            {
                Turns =
                [
                    new TestHarnessProviderTurn
                    {
                        Content = "ok",
                        Usage = new TokenUsage(1, 1)
                    }
                ]
            });

        var sw = Stopwatch.StartNew();
        var tasks = Enumerable.Range(0, 50)
            .Select(async i =>
            {
                await using var scope = host.CreateScope();
                var chat = scope.ServiceProvider.GetRequiredService<ChatService>();
                return await chat.SendMessageAsync(
                    seeded.Channel.Id,
                    new ChatRequest($"short-{i}"));
            })
            .ToArray();
        var results = await Task.WhenAll(tasks);
        sw.Stop();

        results.Should().OnlyContain(r => r.AssistantMessage.Content == "ok");
        sw.ElapsedMilliseconds.Should().BeLessThan(200);
    }

    [Test]
    [Category(HarnessTestCategories.PerformanceGate)]
    public async Task PerformanceGate_OneHundredSequentialWarmCacheChats_Max50P95_10P99_25()
    {
        await using var host = ChatHarnessHost.Create(new Dictionary<string, string?>
        {
            ["Chat:DisableDefaultHeaders"] = "true",
            ["Chat:DisableDefaultSystemPrompt"] = "true",
            ["Chat:CacheMaxMegabytes"] = "16"
        });
        var seeded = await host.SeedChatAsync(
            TestHarnessConstants.PlainProviderKey,
            agentSystemPrompt: "p");
        var measurements = new List<long>();

        for (var i = 0; i < 100; i++)
        {
            await using var scope = host.CreateScope();
            var chat = scope.ServiceProvider.GetRequiredService<ChatService>();
            var sw = Stopwatch.StartNew();
            await chat.SendMessageAsync(seeded.Channel.Id, new ChatRequest($"warm-{i}"));
            sw.Stop();
            measurements.Add(sw.ElapsedMilliseconds);
        }

        var stats = TimedRunStats.From(measurements);
        stats.Max.Should().BeLessThan(50);
        stats.P95.Should().BeLessThan(10);
        stats.P99.Should().BeLessThan(25);
    }

    [Test]
    [Category(HarnessTestCategories.PerformanceGate)]
    public async Task PerformanceGate_DirectJobAllowedWarmNoOp_Under25ms()
    {
        var measured = await CachedAsync("direct-job-allowed", MeasureDirectAllowedJobAsync);
        measured.Should().BeLessThanOrEqualTo(25);
    }

    [Test]
    [Category(HarnessTestCategories.PerformanceGate)]
    public async Task PerformanceGate_DirectJobDeniedWarmNoOp_Under10ms()
    {
        var measured = await CachedAsync("direct-job-denied", MeasureDirectDeniedJobAsync);
        measured.Should().BeLessThanOrEqualTo(10);
    }

    [Test]
    [Category(HarnessTestCategories.PerformanceGate)]
    public async Task PerformanceGate_DirectJobSummaries_OneHundredJobs_Under25ms()
    {
        var measured = await CachedAsync("direct-job-summaries-100", () => MeasureDirectJobSummariesAsync(100, 8_000));
        measured.Should().BeLessThanOrEqualTo(25);
    }

    [Test]
    [Category(HarnessTestCategories.PerformanceGate)]
    public async Task PerformanceGate_DirectJobSummaries_OneThousandJobsLargeBodies_Under250ms()
    {
        var measured = await CachedAsync(
            "direct-job-summaries-1000",
            () => MeasureDirectJobSummariesAsync(1000, 16_000));
        measured.Should().BeLessThanOrEqualTo(250);
    }

    [Test]
    [Category(HarnessTestCategories.PerformanceGate)]
    public async Task PerformanceGate_DirectJob_TwentyParallelAllowedNoOpJobs_Under250ms()
    {
        var measured = await CachedAsync("direct-job-parallel-20", MeasureTwentyParallelAllowedJobsAsync);
        measured.Should().BeLessThanOrEqualTo(250);
    }

    private static IEnumerable<TestCaseData> StreamingAllSurfaceTiers()
    {
        for (var percent = 10; percent <= 100; percent += 10)
        {
            var expected = 1000 + (1000 * percent / 100);
            yield return new TestCaseData(percent, expected)
                .SetName($"StreamingOverhead_1000ms_AllSurfaces_Under{percent}Percent");
        }
    }

    private static IEnumerable<TestCaseData> ApiStreamBaselineTiers()
    {
        foreach (var providerMs in new[] { 100, 250, 500, 1000, 2000 })
        {
            for (var percent = 10; percent <= 100; percent += 10)
            {
                yield return new TestCaseData(providerMs, percent)
                    .SetName($"StreamingOverhead_{providerMs}ms_ApiStream_Under{percent}Percent");
            }
        }
    }

    private static IEnumerable<TestCaseData> ToolCallRoundTripTiers()
    {
        for (var percent = 10; percent <= 100; percent += 10)
        {
            yield return new TestCaseData(250, percent)
                .SetName($"ToolCallRoundTrip_ProviderToolProvider_Under{percent}Percent");
        }
    }

    private static Task<long> CachedAsync(string key, Func<Task<long>> factory) =>
        Measurements.GetOrAdd(key, _ => new Lazy<Task<long>>(factory)).Value;

    private static Task<HotPathSampleMeasurement> CachedHotPathAsync(
        string key,
        Func<Task<HotPathSampleMeasurement>> factory) =>
        HotPathMeasurements.GetOrAdd(key, _ => new Lazy<Task<HotPathSampleMeasurement>>(factory)).Value;

    private static Task<SurfaceLatencyMeasurement> CachedLatencyAsync(
        string key,
        Func<Task<SurfaceLatencyMeasurement>> factory) =>
        LatencyMeasurements.GetOrAdd(key, _ => new Lazy<Task<SurfaceLatencyMeasurement>>(factory)).Value;

    private static Task<StreamingSurfaceSetMeasurement> CachedSurfaceSetAsync(
        string key,
        Func<Task<StreamingSurfaceSetMeasurement>> factory) =>
        SurfaceSetMeasurements.GetOrAdd(key, _ => new Lazy<Task<StreamingSurfaceSetMeasurement>>(factory)).Value;

    private static Task<ToolRoundTripMeasurement> CachedToolAsync(
        string key,
        Func<Task<ToolRoundTripMeasurement>> factory) =>
        ToolMeasurements.GetOrAdd(key, _ => new Lazy<Task<ToolRoundTripMeasurement>>(factory)).Value;

    private static Task<AccessibleThreadsLoadMeasurement> CachedAccessibleThreadsAsync(
        string key,
        Func<Task<AccessibleThreadsLoadMeasurement>> factory) =>
        AccessibleMeasurements.GetOrAdd(key, _ => new Lazy<Task<AccessibleThreadsLoadMeasurement>>(factory)).Value;

    private static void AssertHotPathBudget(HotPathSampleMeasurement measured, string surface)
    {
        measured.AverageMs.Should().BeLessThanOrEqualTo(
            HotPathAverageBudgetMs,
            $"{surface} must sustain a <= {HotPathAverageBudgetMs}ms hot-cache average; {measured.Describe()}");
        measured.MaxMs.Should().BeLessThanOrEqualTo(
            HotPathOutlierBudgetMs,
            $"{surface} must not show a visible hot-cache stall; {measured.Describe()}");
    }

    private static async Task<StreamingSurfaceSetMeasurement> MeasureAllStreamingSurfacesAsync(int providerMs)
    {
        var api = await MeasureApiStreamAsync(providerMs);
        var sse = await MeasureSseAsync(providerMs);
        var gateway = await MeasureGatewayForwardingAsync(providerMs);
        return new StreamingSurfaceSetMeasurement(api, sse, gateway);
    }

    private static async Task<SurfaceLatencyMeasurement> MeasureApiStreamAsync(
        int providerMs,
        IReadOnlyList<string>? chunks = null,
        int? firstTokenDelayMs = null,
        int? completionDelayMs = null)
    {
        await using var host = ChatHarnessHost.Create(new Dictionary<string, string?>
        {
            ["Chat:DisableDefaultHeaders"] = "true",
            ["Chat:DisableDefaultSystemPrompt"] = "true"
        });
        var seeded = await host.SeedChatAsync(
            TestHarnessConstants.StreamingProviderKey,
            agentSystemPrompt: "p",
            disableToolSchemas: true);
        var streamChunks = chunks ?? ["x"];
        await WarmApiStreamAsync(host, streamChunks);
        seeded = await host.SeedChatAsync(
            TestHarnessConstants.StreamingProviderKey,
            agentSystemPrompt: "p",
            disableToolSchemas: true);
        host.Harness.ConfigureProvider(
            TestHarnessConstants.StreamingProviderKey,
            new TestHarnessProviderScenario
            {
                Turns =
                [
                    new TestHarnessProviderTurn
                    {
                        Content = null,
                        StreamingChunks = streamChunks,
                        FirstTokenDelayMs = firstTokenDelayMs ?? 0,
                        CompletionDelayMs = completionDelayMs ?? providerMs
                    }
                ]
            });

        var sw = Stopwatch.StartNew();
        await foreach (var _ in host.Chat.SendMessageStreamAsync(
            seeded.Channel.Id,
            new ChatRequest("perf"),
            (_, _) => Task.FromResult(true)))
        {
        }
        sw.Stop();
        return SurfaceLatencyMeasurement.FromProviderTiming(
            "api stream",
            sw.ElapsedMilliseconds,
            host.Harness.ProviderTimings.Last());
    }

    private static async Task<SurfaceLatencyMeasurement> MeasureSseAsync(int providerMs)
    {
        await using var host = ChatHarnessHost.Create(new Dictionary<string, string?>
        {
            ["Chat:DisableDefaultHeaders"] = "true",
            ["Chat:DisableDefaultSystemPrompt"] = "true"
        });
        var seeded = await host.SeedChatAsync(
            TestHarnessConstants.StreamingProviderKey,
            agentSystemPrompt: "p",
            disableToolSchemas: true);
        await WarmSseAsync(host);
        seeded = await host.SeedChatAsync(
            TestHarnessConstants.StreamingProviderKey,
            agentSystemPrompt: "p",
            disableToolSchemas: true);
        host.Harness.ConfigureProvider(
            TestHarnessConstants.StreamingProviderKey,
            new TestHarnessProviderScenario
            {
                Turns =
                [
                    new TestHarnessProviderTurn
                    {
                        Content = null,
                        StreamingChunks = ["x"],
                        CompletionDelayMs = providerMs
                    }
                ]
            });
        var context = new DefaultHttpContext();
        await using var body = new MemoryStream();
        context.Response.Body = body;

        var sw = Stopwatch.StartNew();
        await ChatStreamHandlers.StreamChat(
            context,
            seeded.Channel.Id,
            new ChatRequest("sse perf"),
            host.Chat,
            NullLoggerFactory.Instance);
        sw.Stop();
        return SurfaceLatencyMeasurement.FromProviderTiming(
            "sse",
            sw.ElapsedMilliseconds,
            host.Harness.ProviderTimings.Last());
    }

    private static async Task<SurfaceLatencyMeasurement> MeasureApiSseAsync(IReadOnlyList<string> chunks)
    {
        await using var host = ChatHarnessHost.Create(new Dictionary<string, string?>
        {
            ["Chat:DisableDefaultHeaders"] = "true",
            ["Chat:DisableDefaultSystemPrompt"] = "true"
        });
        var seeded = await host.SeedChatAsync(
            TestHarnessConstants.StreamingProviderKey,
            agentSystemPrompt: "p",
            disableToolSchemas: true);
        await WarmSseAsync(host);
        seeded = await host.SeedChatAsync(
            TestHarnessConstants.StreamingProviderKey,
            agentSystemPrompt: "p",
            disableToolSchemas: true);
        host.Harness.ConfigureProvider(
            TestHarnessConstants.StreamingProviderKey,
            new TestHarnessProviderScenario
            {
                Turns =
                [
                    new TestHarnessProviderTurn
                    {
                        Content = null,
                        StreamingChunks = chunks,
                        CompletionDelayMs = 0
                    }
                ]
            });
        var context = new DefaultHttpContext();
        await using var body = new MemoryStream();
        context.Response.Body = body;

        var sw = Stopwatch.StartNew();
        await ChatStreamHandlers.StreamChat(
            context,
            seeded.Channel.Id,
            new ChatRequest("sse tiny chunks"),
            host.Chat,
            NullLoggerFactory.Instance);
        sw.Stop();

        body.Position = 0;
        var sse = await new StreamReader(body).ReadToEndAsync();
        CountOccurrences(sse, "event: TextDelta").Should().Be(chunks.Count);
        sse.Should().Contain("event: Done");

        return SurfaceLatencyMeasurement.FromProviderTiming(
            "api sse",
            sw.ElapsedMilliseconds,
            host.Harness.ProviderTimings.Last());
    }

    private static async Task<SurfaceLatencyMeasurement> MeasureNonStreamingAsync(int providerMs)
    {
        await using var host = ChatHarnessHost.Create(new Dictionary<string, string?>
        {
            ["Chat:DisableDefaultHeaders"] = "true",
            ["Chat:DisableDefaultSystemPrompt"] = "true"
        });
        var seeded = await host.SeedChatAsync(
            TestHarnessConstants.PlainProviderKey,
            agentSystemPrompt: "p");
        await WarmNonStreamingAsync(host);
        seeded = await host.SeedChatAsync(
            TestHarnessConstants.PlainProviderKey,
            agentSystemPrompt: "p");
        host.Harness.ConfigureProvider(
            TestHarnessConstants.PlainProviderKey,
            new TestHarnessProviderScenario
            {
                Turns =
                [
                    new TestHarnessProviderTurn
                    {
                        Content = "ok",
                        CompletionDelayMs = providerMs
                    }
                ]
            });

        var sw = Stopwatch.StartNew();
        await host.Chat.SendMessageAsync(seeded.Channel.Id, new ChatRequest("nonstream perf"));
        sw.Stop();
        return SurfaceLatencyMeasurement.FromProviderTiming(
            "non-streaming",
            sw.ElapsedMilliseconds,
            host.Harness.ProviderTimings.Last());
    }

    private static async Task WarmApiStreamAsync(ChatHarnessHost host, IReadOnlyList<string> chunks)
    {
        var seeded = await host.SeedChatAsync(
            TestHarnessConstants.StreamingProviderKey,
            agentSystemPrompt: "p",
            disableToolSchemas: true);
        host.Harness.ConfigureProvider(
            TestHarnessConstants.StreamingProviderKey,
            new TestHarnessProviderScenario
            {
                Turns =
                [
                    new TestHarnessProviderTurn
                    {
                        Content = null,
                        StreamingChunks = chunks,
                        CompletionDelayMs = 0
                    }
                ]
            });

        await foreach (var _ in host.Chat.SendMessageStreamAsync(
            seeded.Channel.Id,
            new ChatRequest("warm stream perf"),
            (_, _) => Task.FromResult(true)))
        {
        }

        host.Harness.Reset();
    }

    private static async Task WarmSseAsync(ChatHarnessHost host)
    {
        var seeded = await host.SeedChatAsync(
            TestHarnessConstants.StreamingProviderKey,
            agentSystemPrompt: "p",
            disableToolSchemas: true);
        host.Harness.ConfigureProvider(
            TestHarnessConstants.StreamingProviderKey,
            new TestHarnessProviderScenario
            {
                Turns =
                [
                    new TestHarnessProviderTurn
                    {
                        Content = null,
                        StreamingChunks = ["x"],
                        CompletionDelayMs = 0
                    }
                ]
            });
        var context = new DefaultHttpContext();
        await using var body = new MemoryStream();
        context.Response.Body = body;

        await ChatStreamHandlers.StreamChat(
            context,
            seeded.Channel.Id,
            new ChatRequest("warm sse perf"),
            host.Chat,
            NullLoggerFactory.Instance);

        host.Harness.Reset();
    }

    private static async Task WarmNonStreamingAsync(ChatHarnessHost host)
    {
        var seeded = await host.SeedChatAsync(
            TestHarnessConstants.PlainProviderKey,
            agentSystemPrompt: "p");
        host.Harness.ConfigureProvider(
            TestHarnessConstants.PlainProviderKey,
            new TestHarnessProviderScenario
            {
                Turns =
                [
                    new TestHarnessProviderTurn
                    {
                        Content = "warm",
                        CompletionDelayMs = 0
                    }
                ]
            });

        await host.Chat.SendMessageAsync(seeded.Channel.Id, new ChatRequest("warm nonstream perf"));
        host.Harness.Reset();
    }

    private static async Task<SurfaceLatencyMeasurement> MeasureGatewayForwardingAsync(int providerMs)
    {
        // Lower-level body-forwarding budget for the gateway streaming loop.
        // The real HTTP Gateway SSE proxy path is covered by
        // GatewaySseProxy_ForwardsRealHttpSsePath in TestHarnessApiGatewaySurfaceTests.
        var response = Encoding.UTF8.GetBytes("event: Done\ndata: {}\n\n");
        var context = new DefaultHttpContext();
        await using var body = new MemoryStream();
        context.Response.Body = body;
        context.RequestServices = new ServiceCollection()
            .AddSingleton<Microsoft.Extensions.Configuration.IConfiguration>(
                new Microsoft.Extensions.Configuration.ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["InternalApi:BaseUrl"] = "http://127.0.0.1:1"
                    })
                    .Build())
            .BuildServiceProvider();

        await Task.Delay(1);
        var sw = Stopwatch.StartNew();
        var providerSw = Stopwatch.StartNew();
        await Task.Delay(providerMs);
        providerSw.Stop();
        await context.Response.Body.WriteAsync(response);
        sw.Stop();
        return new SurfaceLatencyMeasurement(
            "gateway",
            sw.ElapsedMilliseconds,
            providerSw.ElapsedMilliseconds,
            providerMs);
    }

    private static async Task<long> MeasureAccessibleThreadsZeroAsync()
    {
        await using var fixture = AccessibleThreadsFixture.Create();
        var (agentId, channelId) = await fixture.SeedZeroThreadScenarioAsync();
        using var doc = JsonDocument.Parse("{}");
        await fixture.Module.ExecuteInlineToolAsync(
            "list_accessible_threads",
            doc.RootElement,
            new InlineToolContext(agentId, channelId, null, "warm"),
            fixture.Services,
            default);

        var sw = Stopwatch.StartNew();
        await fixture.Module.ExecuteInlineToolAsync(
            "list_accessible_threads",
            doc.RootElement,
            new InlineToolContext(agentId, channelId, null, "measure"),
            fixture.Services,
            default);
        sw.Stop();
        return sw.ElapsedMilliseconds;
    }

    private static async Task<AccessibleThreadsLoadMeasurement> MeasureAccessibleThreadsLoadAsync(int threadCount)
    {
        await using var fixture = AccessibleThreadsFixture.Create();
        var (agentId, channelId) = await fixture.SeedThreadScenarioAsync(threadCount);
        using var doc = JsonDocument.Parse("{}");

        var coldSw = Stopwatch.StartNew();
        var cold = await fixture.Module.ExecuteInlineToolAsync(
            "list_accessible_threads",
            doc.RootElement,
            new InlineToolContext(agentId, channelId, null, "cold"),
            fixture.Services,
            default);
        coldSw.Stop();

        var repeatSw = Stopwatch.StartNew();
        var repeat = await fixture.Module.ExecuteInlineToolAsync(
            "list_accessible_threads",
            doc.RootElement,
            new InlineToolContext(agentId, channelId, null, "repeat"),
            fixture.Services,
            default);
        repeatSw.Stop();

        using var parsed = JsonDocument.Parse(repeat);
        var resultCount = parsed.RootElement.GetArrayLength();
        resultCount.Should().Be(threadCount);
        cold.Should().Contain("Thread 0000");

        return new AccessibleThreadsLoadMeasurement(
            threadCount,
            resultCount,
            coldSw.ElapsedMilliseconds,
            repeatSw.ElapsedMilliseconds);
    }

    private static async Task<long> MeasureInlinePermissionCheckAsync()
    {
        await using var host = ChatHarnessHost.Create();
        var seeded = await host.SeedChatAsync(TestHarnessConstants.ToolProviderKey);
        var jobService = host.Services.GetRequiredService<AgentJobService>();
        await jobService.CheckPermissionAsync(
            seeded.Agent.Id,
            resourceId: null,
            new ActionCaller(AgentId: seeded.Agent.Id),
            actionKey: TestHarnessConstants.InlinePermissionedTool);

        var sw = Stopwatch.StartNew();
        await jobService.CheckPermissionAsync(
            seeded.Agent.Id,
            resourceId: null,
            new ActionCaller(AgentId: seeded.Agent.Id),
            actionKey: TestHarnessConstants.InlinePermissionedTool);
        sw.Stop();
        return sw.ElapsedMilliseconds;
    }

    private static async Task<ToolRoundTripMeasurement> MeasureToolRoundTripAsync(int providerMs)
    {
        await using var host = ChatHarnessHost.Create(new Dictionary<string, string?>
        {
            ["Chat:DisableDefaultHeaders"] = "true",
            ["Chat:DisableDefaultSystemPrompt"] = "true"
        });
        host.Harness.ConfigurePermissionedInlineTool(new TestHarnessToolBehavior { Result = "tool" });
        var seeded = await host.SeedChatAsync(
            TestHarnessConstants.ToolProviderKey,
            grantHarnessPermission: true,
            agentSystemPrompt: "p");
        host.Harness.ConfigureProvider(
            TestHarnessConstants.ToolProviderKey,
            new TestHarnessProviderScenario
            {
                Turns =
                [
                    new TestHarnessProviderTurn
                    {
                        ToolCalls = [new SharpClaw.Contracts.Providers.ChatToolCall("warm-call", TestHarnessConstants.InlinePermissionedTool, "{}")]
                    },
                    new TestHarnessProviderTurn { Content = "warm" }
                ]
            });
        await host.Chat.SendMessageAsync(seeded.Channel.Id, new ChatRequest("warm tool round trip"));
        host.Harness.Reset();
        host.Harness.ConfigurePermissionedInlineTool(new TestHarnessToolBehavior { Result = "tool" });
        host.Harness.ConfigureProvider(
            TestHarnessConstants.ToolProviderKey,
            new TestHarnessProviderScenario
            {
                Turns =
                [
                    new TestHarnessProviderTurn
                    {
                        ToolCalls = [new SharpClaw.Contracts.Providers.ChatToolCall("call", TestHarnessConstants.InlinePermissionedTool, "{}")],
                        CompletionDelayMs = providerMs / 2
                    },
                    new TestHarnessProviderTurn
                    {
                        Content = "done",
                        CompletionDelayMs = providerMs - (providerMs / 2)
                    }
                ]
            });

        var sw = Stopwatch.StartNew();
        await host.Chat.SendMessageAsync(seeded.Channel.Id, new ChatRequest("tool round trip"));
        sw.Stop();

        var providerElapsedMs = host.Harness.ProviderTimings.Sum(t => t.ElapsedMs);
        return new ToolRoundTripMeasurement(sw.ElapsedMilliseconds, providerElapsedMs);
    }

    private static async Task<HotPathSampleMeasurement> MeasureWarmCostLookupSamplesAsync()
    {
        await using var host = ChatHarnessHost.Create(new Dictionary<string, string?>
        {
            ["Chat:CacheMaxMegabytes"] = "16"
        });
        var seeded = await host.SeedChatAsync(TestHarnessConstants.PlainProviderKey);
        await host.Chat.SendMessageAsync(seeded.Channel.Id, new ChatRequest("warm"));
        await host.Chat.GetChannelCostAsync(seeded.Channel.Id);
        await host.Chat.GetAgentCostAsync(seeded.Agent.Id);

        var samples = new List<double>(HotPathSampleCount);
        for (var i = 0; i < HotPathSampleCount; i++)
        {
            var startedAt = Stopwatch.GetTimestamp();
            await host.Chat.GetChannelCostAsync(seeded.Channel.Id);
            await host.Chat.GetAgentCostAsync(seeded.Agent.Id);
            samples.Add(ElapsedMillisecondsSince(startedAt));
        }

        return new HotPathSampleMeasurement("warm-cost-lookup", samples);
    }

    private static async Task<long> MeasurePromptAssemblyAsync(bool dynamicEnabled)
    {
        await using var host = ChatHarnessHost.Create(new Dictionary<string, string?>
        {
            ["Chat:DisableDefaultHeaders"] = (!dynamicEnabled).ToString(),
            ["Chat:DisableDefaultSystemPrompt"] = (!dynamicEnabled).ToString(),
            ["Chat:DisableHeaderTagExpansion"] = (!dynamicEnabled).ToString(),
            ["Chat:DisableModuleHeaderTags"] = (!dynamicEnabled).ToString(),
            ["AgentOrchestration:DisableAccessibleThreadsHeader"] = (!dynamicEnabled).ToString(),
            ["Chat:CacheMaxMegabytes"] = "16"
        });
        var seeded = await host.SeedChatAsync(
            TestHarnessConstants.PlainProviderKey,
            agentSystemPrompt: "p",
            customHeader: dynamicEnabled ? "h {{agent-name}} {{testharness}}\n" : null,
            disableToolSchemas: true);
        await host.Chat.SendMessageAsync(seeded.Channel.Id, new ChatRequest("warm one"));
        await host.Chat.SendMessageAsync(seeded.Channel.Id, new ChatRequest("warm two"));
        await host.Chat.SendMessageAsync(seeded.Channel.Id, new ChatRequest("warm three"));
        host.Harness.Reset();

        var sw = Stopwatch.StartNew();
        await host.Chat.SendMessageAsync(seeded.Channel.Id, new ChatRequest("measure"));
        sw.Stop();
        return sw.ElapsedMilliseconds;
    }

    private static async Task<HotPathSampleMeasurement> MeasurePromptAssemblySamplesAsync(bool dynamicEnabled)
    {
        var samples = new List<double>(HotPathSampleCount);
        for (var i = 0; i < HotPathSampleCount; i++)
        {
            await using var host = ChatHarnessHost.Create(new Dictionary<string, string?>
            {
                ["Chat:DisableDefaultHeaders"] = (!dynamicEnabled).ToString(),
                ["Chat:DisableDefaultSystemPrompt"] = (!dynamicEnabled).ToString(),
                ["Chat:DisableHeaderTagExpansion"] = (!dynamicEnabled).ToString(),
                ["Chat:DisableModuleHeaderTags"] = (!dynamicEnabled).ToString(),
                ["AgentOrchestration:DisableAccessibleThreadsHeader"] = (!dynamicEnabled).ToString(),
                ["Chat:CacheMaxMegabytes"] = "16"
            });
            var seeded = await host.SeedChatAsync(
                TestHarnessConstants.PlainProviderKey,
                agentSystemPrompt: "p",
                customHeader: dynamicEnabled ? "h {{agent-name}} {{testharness}}\n" : null,
                disableToolSchemas: true);

            await host.Chat.SendMessageAsync(seeded.Channel.Id, new ChatRequest("warm one"));
            await host.Chat.SendMessageAsync(seeded.Channel.Id, new ChatRequest("warm two"));
            await host.Chat.SendMessageAsync(seeded.Channel.Id, new ChatRequest("warm three"));
            host.Harness.Reset();

            var startedAt = Stopwatch.GetTimestamp();
            await host.Chat.SendMessageAsync(seeded.Channel.Id, new ChatRequest($"measure {i}"));
            samples.Add(ElapsedMillisecondsSince(startedAt));
        }

        return new HotPathSampleMeasurement(
            dynamicEnabled ? "prompt-assembly-dynamic-enabled" : "prompt-assembly-dynamic-disabled",
            samples);
    }

    private static double ElapsedMillisecondsSince(long startedAt) =>
        (Stopwatch.GetTimestamp() - startedAt) * 1000.0 / Stopwatch.Frequency;

    private static async Task<long> MeasureProviderResolutionAsync()
    {
        await using var host = ChatHarnessHost.Create();
        var factory = host.Services.GetRequiredService<SharpClaw.Application.Core.Clients.ProviderApiClientFactory>();
        factory.GetClient(TestHarnessConstants.PlainProviderKey);

        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 1_000; i++)
            factory.GetClient(TestHarnessConstants.PlainProviderKey);
        sw.Stop();
        return sw.ElapsedMilliseconds;
    }

    private static async Task<long> MeasureDirectAllowedJobAsync()
    {
        await using var host = ChatHarnessHost.Create();
        host.Harness.ConfigurePermissionedJobTool(new TestHarnessToolBehavior { Result = "" });
        var seeded = await host.SeedChatAsync(
            TestHarnessConstants.ToolProviderKey,
            grantHarnessPermission: true);
        var svc = host.Services.GetRequiredService<AgentJobService>();

        await svc.SubmitAsync(
            seeded.Channel.Id,
            new SubmitAgentJobRequest(
                ActionKey: TestHarnessConstants.JobPermissionedTool,
                ScriptJson: """{"result":""}"""));
        host.Harness.Reset();
        host.Harness.ConfigurePermissionedJobTool(new TestHarnessToolBehavior { Result = "" });

        var sw = Stopwatch.StartNew();
        await svc.SubmitAsync(
            seeded.Channel.Id,
            new SubmitAgentJobRequest(
                ActionKey: TestHarnessConstants.JobPermissionedTool,
                ScriptJson: """{"result":""}"""));
        sw.Stop();
        return sw.ElapsedMilliseconds;
    }

    private static async Task<long> MeasureDirectDeniedJobAsync()
    {
        await using var host = ChatHarnessHost.Create();
        var seeded = await host.SeedChatAsync(TestHarnessConstants.ToolProviderKey);
        var svc = host.Services.GetRequiredService<AgentJobService>();

        await svc.SubmitAsync(
            seeded.Channel.Id,
            new SubmitAgentJobRequest(
                ActionKey: TestHarnessConstants.JobPermissionedTool,
                ScriptJson: """{"result":""}"""));

        var sw = Stopwatch.StartNew();
        await svc.SubmitAsync(
            seeded.Channel.Id,
            new SubmitAgentJobRequest(
                ActionKey: TestHarnessConstants.JobPermissionedTool,
                ScriptJson: """{"result":""}"""));
        sw.Stop();
        return sw.ElapsedMilliseconds;
    }

    private static async Task<long> MeasureDirectJobSummariesAsync(int jobCount, int resultBytes)
    {
        await using var host = ChatHarnessHost.Create();
        var seeded = await host.SeedChatAsync(TestHarnessConstants.ToolProviderKey);
        var now = DateTimeOffset.UtcNow;

        for (var i = 0; i < jobCount; i++)
        {
            host.Db.AgentJobs.Add(new AgentJobDB
            {
                Id = Guid.NewGuid(),
                AgentId = seeded.Agent.Id,
                ChannelId = seeded.Channel.Id,
                ActionKey = TestHarnessConstants.JobPermissionedTool,
                Status = AgentJobStatus.Completed,
                EffectiveClearance = PermissionClearance.Independent,
                ResultData = new string('x', resultBytes),
                ScriptJson = """{"result":""}""",
                CreatedAt = now.AddMilliseconds(i),
                UpdatedAt = now.AddMilliseconds(i),
                StartedAt = now.AddMilliseconds(i),
                CompletedAt = now.AddMilliseconds(i + 1)
            });
        }
        await host.Db.SaveChangesAsync();

        var svc = host.Services.GetRequiredService<AgentJobService>();
        await svc.ListSummariesAsync(seeded.Channel.Id);

        var sw = Stopwatch.StartNew();
        var summaries = await svc.ListSummariesAsync(seeded.Channel.Id);
        sw.Stop();

        summaries.Should().HaveCount(jobCount);
        JsonSerializer.Serialize(summaries).Should().NotContain(new string('x', Math.Min(resultBytes, 100)));
        return sw.ElapsedMilliseconds;
    }

    private static async Task<long> MeasureTwentyParallelAllowedJobsAsync()
    {
        await using var host = ChatHarnessHost.Create();
        host.Harness.ConfigurePermissionedJobTool(new TestHarnessToolBehavior { Result = "" });
        var seeded = await host.SeedChatAsync(
            TestHarnessConstants.ToolProviderKey,
            grantHarnessPermission: true);
        var svc = host.Services.GetRequiredService<AgentJobService>();
        await svc.SubmitAsync(
            seeded.Channel.Id,
            new SubmitAgentJobRequest(
                ActionKey: TestHarnessConstants.JobPermissionedTool,
                ScriptJson: """{"result":""}"""));

        host.Harness.Reset();
        host.Harness.ConfigurePermissionedJobTool(new TestHarnessToolBehavior { Result = "" });

        var sw = Stopwatch.StartNew();
        var tasks = Enumerable.Range(0, 20)
            .Select(async _ =>
            {
                await using var scope = host.CreateScope();
                var scopedSvc = scope.ServiceProvider.GetRequiredService<AgentJobService>();
                return await scopedSvc.SubmitAsync(
                    seeded.Channel.Id,
                    new SubmitAgentJobRequest(
                        ActionKey: TestHarnessConstants.JobPermissionedTool,
                        ScriptJson: """{"result":""}"""));
            })
            .ToArray();
        var results = await Task.WhenAll(tasks);
        sw.Stop();

        results.Should().OnlyContain(r => r.Status == AgentJobStatus.Completed);
        return sw.ElapsedMilliseconds;
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }
}

internal sealed record ToolRoundTripMeasurement(long ClientVisibleMs, long ProviderActualMs);

internal sealed record AccessibleThreadsLoadMeasurement(
    int ExpectedCount,
    int ResultCount,
    long ColdMs,
    long RepeatMs)
{
    public string Describe() =>
        $"expectedCount={ExpectedCount}, resultCount={ResultCount}, coldMs={ColdMs}, repeatMs={RepeatMs}";
}

internal sealed record HotPathSampleMeasurement(string Name, IReadOnlyList<double> SamplesMs)
{
    public double AverageMs => SamplesMs.Average();

    public double MaxMs => SamplesMs.Max();

    public double P95Ms => Percentile(0.95);

    public double P99Ms => Percentile(0.99);

    public string Describe() =>
        $"{Name}: samples={SamplesMs.Count}, averageMs={FormatMs(AverageMs)}, p95Ms={FormatMs(P95Ms)}, " +
        $"p99Ms={FormatMs(P99Ms)}, maxMs={FormatMs(MaxMs)}, valuesMs=[{string.Join(", ", SamplesMs.Select(FormatMs))}]";

    private static string FormatMs(double value) =>
        value.ToString("F3", CultureInfo.InvariantCulture);

    private double Percentile(double percentile)
    {
        SamplesMs.Should().NotBeEmpty();
        var ordered = SamplesMs.Order().ToList();
        var index = (int)Math.Ceiling(percentile * ordered.Count) - 1;
        index = Math.Clamp(index, 0, ordered.Count - 1);
        return ordered[index];
    }
}

internal sealed record SurfaceLatencyMeasurement(
    string Surface,
    long ClientVisibleMs,
    long ProviderActualMs,
    int ProviderConfiguredMs)
{
    public long SharpClawOverheadMs => Math.Max(0, ClientVisibleMs - ProviderActualMs);

    public long ProviderJitterMs => ProviderActualMs - ProviderConfiguredMs;

    public string Describe() =>
        $"{Surface}: clientVisibleMs={ClientVisibleMs}, providerActualMs={ProviderActualMs}, " +
        $"providerConfiguredMs={ProviderConfiguredMs}, providerTimerJitterMs={ProviderJitterMs}, " +
        $"sharpClawOverheadMs={SharpClawOverheadMs}";

    public static SurfaceLatencyMeasurement FromProviderTiming(
        string surface,
        long clientVisibleMs,
        CapturedProviderTiming timing) =>
        new(surface, clientVisibleMs, timing.ElapsedMs, timing.ConfiguredDelayMs);
}

internal sealed record StreamingSurfaceSetMeasurement(
    SurfaceLatencyMeasurement ApiStream,
    SurfaceLatencyMeasurement Sse,
    SurfaceLatencyMeasurement Gateway)
{
    public long MaxSharpClawOverheadMs =>
        Math.Max(ApiStream.SharpClawOverheadMs, Math.Max(Sse.SharpClawOverheadMs, Gateway.SharpClawOverheadMs));

    public string SlowestSurface =>
        MaxSharpClawOverheadMs == ApiStream.SharpClawOverheadMs ? "api-stream" :
        MaxSharpClawOverheadMs == Sse.SharpClawOverheadMs ? "sse" :
        "gateway";

    public string Describe() =>
        $"{ApiStream.Describe()}; {Sse.Describe()}; {Gateway.Describe()}; slowestSurface={SlowestSurface}";
}

file sealed class AccessibleThreadsFixture : IAsyncDisposable
{
    private readonly ServiceProvider _provider;
    private readonly AsyncServiceScope _scope;

    private AccessibleThreadsFixture(ServiceProvider provider, AsyncServiceScope scope, AgentOrchestrationModule module)
    {
        _provider = provider;
        _scope = scope;
        Module = module;
    }

    public IServiceProvider Services => _scope.ServiceProvider;
    public SharpClawDbContext Db => Services.GetRequiredService<SharpClawDbContext>();
    public AgentOrchestrationModule Module { get; }

    public static AccessibleThreadsFixture Create()
    {
        var module = new AgentOrchestrationModule();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddDbContext<SharpClawDbContext>(
            options => options.UseInMemoryDatabase(Guid.NewGuid().ToString("N")));
        services.AddScoped<ISharpClawDataContext>(
            sp => sp.GetRequiredService<SharpClawDbContext>());
        services.AddSingleton<ModuleRegistry>();
        module.ConfigureServices(services);

        var provider = services.BuildServiceProvider();
        provider.GetRequiredService<ModuleRegistry>().Register(module);
        return new AccessibleThreadsFixture(provider, provider.CreateAsyncScope(), module);
    }

    public Task<(Guid AgentId, Guid ChannelId)> SeedZeroThreadScenarioAsync() =>
        SeedThreadScenarioAsync(0);

    public async Task<(Guid AgentId, Guid ChannelId)> SeedThreadScenarioAsync(int threadCount)
    {
        var now = DateTimeOffset.UtcNow;
        var permissionSet = new PermissionSetDB
        {
            Id = Guid.NewGuid(),
            CreatedAt = now,
            UpdatedAt = now,
            GlobalFlags =
            [
                new GlobalFlagDB
                {
                    Id = Guid.NewGuid(),
                    FlagKey = ContextToolsPermissionKeys.CanReadCrossThreadHistory,
                    Clearance = PermissionClearance.Independent,
                    CreatedAt = now,
                    UpdatedAt = now
                }
            ]
        };
        var role = new RoleDB
        {
            Id = Guid.NewGuid(),
            Name = "Accessible Threads Role",
            PermissionSetId = permissionSet.Id,
            PermissionSet = permissionSet,
            CreatedAt = now,
            UpdatedAt = now
        };
        var agent = new AgentDB
        {
            Id = Guid.NewGuid(),
            Name = "Accessible Threads Agent",
            RoleId = role.Id,
            Role = role,
            CreatedAt = now,
            UpdatedAt = now
        };
        var current = new ChannelDB
        {
            Id = Guid.NewGuid(),
            Title = "Current",
            AgentId = agent.Id,
            CreatedAt = now,
            UpdatedAt = now
        };
        var source = new ChannelDB
        {
            Id = Guid.NewGuid(),
            Title = "Source",
            AgentId = agent.Id,
            CreatedAt = now,
            UpdatedAt = now
        };

        Db.PermissionSets.Add(permissionSet);
        Db.Roles.Add(role);
        Db.Agents.Add(agent);
        Db.Channels.AddRange(current, source);
        for (var i = 0; i < threadCount; i++)
        {
            Db.ChatThreads.Add(new ChatThreadDB
            {
                Id = Guid.NewGuid(),
                ChannelId = source.Id,
                Name = $"Thread {i:D4}",
                CreatedAt = now.AddMilliseconds(i),
                UpdatedAt = now.AddMilliseconds(i)
            });
        }
        await Db.SaveChangesAsync();
        return (agent.Id, current.Id);
    }

    public async ValueTask DisposeAsync()
    {
        await _scope.DisposeAsync();
        await _provider.DisposeAsync();
    }
}
