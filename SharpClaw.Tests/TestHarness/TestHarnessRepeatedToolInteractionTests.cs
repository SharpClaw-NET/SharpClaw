using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SharpClaw.Runtime.Host.Handlers;
using SharpClaw.Runtime.BLL.Modules;
using SharpClaw.Contracts.DTOs.Chat;
using SharpClaw.Contracts.Entities.Core.Access;
using SharpClaw.Contracts.Entities.Core.Clearance;
using SharpClaw.Contracts.Enums;
using SharpClaw.Contracts.Modules;
using SharpClaw.Contracts.Providers;
using SharpClaw.Tests.TestHarness;
using SharpClaw.Core.Modules;

namespace SharpClaw.Tests.TestHarness;

[TestFixture]
public sealed class TestHarnessRepeatedToolInteractionTests
{
    private const int HundredCalls = 100;
    private static readonly ConcurrentDictionary<string, Lazy<Task<RepeatedToolRunMeasurement>>> Measurements = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    [TestCaseSource(nameof(HundredToolCallCorrectnessCases))]
    public async Task ProviderStream_100ToolCalls_CorrectnessVariants(
        string variant,
        bool grantPermission,
        int expectedInvocations,
        int expectedDenied,
        int expectedMalformed)
    {
        await using var host = CreateHotPathHost();
        host.Harness.ConfigureOpenInlineTool(new TestHarnessToolBehavior { Result = "open" });
        host.Harness.ConfigurePermissionedInlineTool(new TestHarnessToolBehavior { Result = "permissioned" });
        var seeded = await host.SeedChatAsync(
            TestHarnessConstants.StreamingProviderKey,
            grantHarnessPermission: grantPermission,
            agentSystemPrompt: "p");

        var calls = BuildVariantCalls(variant, HundredCalls);
        var run = await RunStreamingToolLoopAsync(host, seeded.Channel.Id, calls, "after variants");

        run.Done.FinalResponse!.AssistantMessage.Content.Should().EndWith("after variants");
        host.Harness.ToolCalls.Should().HaveCount(expectedInvocations);
        ToolResultMessages(host).Count(m => m.Content?.Contains("permission denied", StringComparison.OrdinalIgnoreCase) == true)
            .Should().Be(expectedDenied);
        ToolResultMessages(host).Count(m => m.Content == "Error: malformed tool arguments JSON.")
            .Should().Be(expectedMalformed);

        if (variant == "alias-allowed")
        {
            host.Harness.ToolCalls.Should().OnlyContain(c =>
                c.ToolName == TestHarnessConstants.InlinePermissionedTool);
        }
    }

    [TestCaseSource(nameof(HundredToolCallPerCallTiers))]
    [Category(HarnessTestCategories.PerformanceDiagnostic)]
    public async Task ProviderStream_100AllowedNoOpToolCalls_WarmCache_PerCallOverhead_Tiered(int maxPerCallMs)
    {
        HarnessDiagnostics.RequireEnabled();
        var measurement = await CachedMeasurementAsync(
            "hundred-allowed-warm",
            MeasureHundredAllowedWarmAsync);

        HarnessBudget.AssertPerCallOverhead(
            measurement.PerCallSharpClawOverheadMs,
            measurement.ClientVisibleMs,
            measurement.ProviderActualMs,
            HundredCalls,
            maxPerCallMs,
            "100 allowed no-op inline tools");

        measurement.InterCallStats.P95.Should().BeLessThanOrEqualTo(
            maxPerCallMs,
            $"warm inter-call p95 should identify the same performance band while max/p99 are reported separately; {measurement}");
    }

    [Test]
    [Category(HarnessTestCategories.PerformanceGate)]
    public async Task PerformanceGate_ProviderStream_100AllowedNoOpToolCalls_WarmCache_Max10P95_2P99_5()
    {
        var measurement = await CachedMeasurementAsync(
            "hundred-allowed-warm",
            MeasureHundredAllowedWarmAsync);

        measurement.ToolBodyInvocations.Should().Be(HundredCalls);
        measurement.InterCallStats.Max.Should().BeLessThanOrEqualTo(10, measurement.ToString());
        measurement.InterCallStats.P95.Should().BeLessThanOrEqualTo(2, measurement.ToString());
        measurement.InterCallStats.P99.Should().BeLessThanOrEqualTo(5, measurement.ToString());
    }

    [Test]
    [Category(HarnessTestCategories.PerformanceGate)]
    public async Task PerformanceGate_ProviderStream_100DeniedToolCalls_WarmCache_Under25ms()
    {
        var denied = await CachedMeasurementAsync("hundred-denied-warm", MeasureHundredDeniedWarmAsync);

        denied.ToolBodyInvocations.Should().Be(0);
        denied.DescriptorBuilds.Should().Be(0);
        denied.PermissionDeniedResults.Should().Be(HundredCalls);
        denied.SharpClawOverheadMs.Should().BeLessThanOrEqualTo(
            25,
            $"denial should stop at host permission enforcement and stay below 0.25ms per denied tool call; denied={denied}");
    }

    [Test]
    public async Task ProviderStream_100MixedToolCalls_AlternatingAllowedAndDeniedKeepsBoundaries()
    {
        await using var host = CreateHotPathHost();
        host.Harness.ConfigureOpenInlineTool(new TestHarnessToolBehavior { Result = "open" });
        var seeded = await host.SeedChatAsync(
            TestHarnessConstants.StreamingProviderKey,
            grantHarnessPermission: false,
            agentSystemPrompt: "p");

        await RunStreamingToolLoopAsync(
            host,
            seeded.Channel.Id,
            BuildVariantCalls("mixed-open-denied", HundredCalls),
            "after mixed");

        host.Harness.ToolCalls.Should().HaveCount(50);
        host.Harness.ToolCalls.Should().OnlyContain(c => c.ToolName == TestHarnessConstants.InlineOpenTool);
        ToolResultMessages(host).Count(m => m.Content?.Contains("permission denied", StringComparison.OrdinalIgnoreCase) == true)
            .Should().Be(50);
    }

    [Test]
    public async Task ProviderStream_100ToolCalls_MalformedArgumentsFailFastWithoutModuleInvocation()
    {
        await using var host = CreateHotPathHost();
        var seeded = await host.SeedChatAsync(
            TestHarnessConstants.StreamingProviderKey,
            grantHarnessPermission: true,
            agentSystemPrompt: "p");

        await RunStreamingToolLoopAsync(
            host,
            seeded.Channel.Id,
            BuildVariantCalls("malformed", HundredCalls),
            "after malformed");

        host.Harness.ToolCalls.Should().BeEmpty();
        ToolResultMessages(host).Should().HaveCount(HundredCalls);
        ToolResultMessages(host).Should().OnlyContain(m => m.Content == "Error: malformed tool arguments JSON.");
    }

    [Test]
    public async Task ProviderStream_RepeatedToolCalls_RolePermissionRemovalInvalidatesWarmPath()
    {
        await using var host = CreateHotPathHost();
        host.Harness.ConfigurePermissionedInlineTool(new TestHarnessToolBehavior { Result = "ok" });
        var seeded = await host.SeedChatAsync(
            TestHarnessConstants.StreamingProviderKey,
            grantHarnessPermission: true,
            agentSystemPrompt: "p");

        await RunStreamingToolLoopAsync(
            host,
            seeded.Channel.Id,
            BuildToolCalls(50, TestHarnessConstants.InlinePermissionedTool),
            "first allowed");
        host.Harness.ToolCalls.Should().HaveCount(50);

        var flags = await host.Db.GlobalFlags
            .Where(f => f.PermissionSetId == seeded.PermissionSet.Id)
            .ToListAsync();
        host.Db.GlobalFlags.RemoveRange(flags);
        await host.Db.SaveChangesAsync();

        host.Harness.Reset();
        host.Harness.ResetDiagnostics();
        await RunStreamingToolLoopAsync(
            host,
            seeded.Channel.Id,
            BuildToolCalls(50, TestHarnessConstants.InlinePermissionedTool),
            "second denied");

        host.Harness.ToolCalls.Should().BeEmpty();
        ToolResultMessages(host).Count(m => m.Content?.Contains("permission denied", StringComparison.OrdinalIgnoreCase) == true)
            .Should().Be(50);
        host.Harness.PermissionDescriptorBuilds.Should().Be(0);
    }

    [Test]
    public async Task ProviderStream_RepeatedToolCalls_RolePermissionGrantInvalidatesWarmPath()
    {
        await using var host = CreateHotPathHost();
        host.Harness.ConfigurePermissionedInlineTool(new TestHarnessToolBehavior { Result = "ok" });
        var seeded = await host.SeedChatAsync(
            TestHarnessConstants.StreamingProviderKey,
            grantHarnessPermission: false,
            agentSystemPrompt: "p");

        await RunStreamingToolLoopAsync(
            host,
            seeded.Channel.Id,
            BuildToolCalls(50, TestHarnessConstants.InlinePermissionedTool),
            "first denied");
        host.Harness.ToolCalls.Should().BeEmpty();

        host.Db.GlobalFlags.Add(new GlobalFlagDB
        {
            Id = Guid.NewGuid(),
            FlagKey = TestHarnessConstants.GlobalFlagKey,
            Clearance = PermissionClearance.Independent,
            PermissionSetId = seeded.PermissionSet.Id,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await host.Db.SaveChangesAsync();

        host.Harness.Reset();
        await RunStreamingToolLoopAsync(
            host,
            seeded.Channel.Id,
            BuildToolCalls(50, TestHarnessConstants.InlinePermissionedTool),
            "second allowed");

        host.Harness.ToolCalls.Should().HaveCount(50);
    }

    [Test]
    public async Task ProviderStream_ModuleLifecycle_DisableClearsCachedToolDescriptorsAndReenableRestoresThem()
    {
        await using var host = CreateHotPathHost();
        host.Harness.ConfigurePermissionedInlineTool(new TestHarnessToolBehavior { Result = "ok" });
        var seeded = await host.SeedChatAsync(
            TestHarnessConstants.StreamingProviderKey,
            grantHarnessPermission: true,
            agentSystemPrompt: "p");
        var registry = host.Services.GetRequiredService<ModuleRegistry>();
        var module = new LifecycleInlineToolModule();
        registry.Register(module);

        await RunStreamingToolLoopAsync(
            host,
            seeded.Channel.Id,
            BuildToolCalls(1, LifecycleInlineToolModule.ToolName),
            "enabled");
        ToolResultMessages(host).Single().Content.Should().Be("lifecycle-ok");

        registry.Unregister(LifecycleInlineToolModule.ModuleId);
        host.Harness.Reset();
        await RunStreamingToolLoopAsync(
            host,
            seeded.Channel.Id,
            BuildToolCalls(1, LifecycleInlineToolModule.ToolName),
            "disabled");
        host.Harness.ToolCalls.Should().BeEmpty();
        ToolResultMessages(host).Single().Content.Should().Contain("unrecognized tool");

        registry.Register(module);
        host.Harness.Reset();
        await RunStreamingToolLoopAsync(
            host,
            seeded.Channel.Id,
            BuildToolCalls(1, LifecycleInlineToolModule.ToolName),
            "reenabled");
        ToolResultMessages(host).Single().Content.Should().Be("lifecycle-ok");
    }

    [Test]
    public async Task ProviderStream_TextBeforeToolsAndTextAfterTools_PreservesOrderSseAndCost()
    {
        await using var host = CreateHotPathHost();
        host.Harness.ConfigurePermissionedInlineTool(new TestHarnessToolBehavior { Result = "" });
        var seeded = await host.SeedChatAsync(
            TestHarnessConstants.StreamingProviderKey,
            grantHarnessPermission: true,
            agentSystemPrompt: "p");
        ConfigureStreamingScenario(
            host,
            BuildToolCalls(HundredCalls, TestHarnessConstants.InlinePermissionedTool),
            "after streamed tools",
            beforeChunks: ["before streamed tools"]);

        var context = new DefaultHttpContext();
        await using var body = new MemoryStream();
        context.Response.Body = body;

        await ChatStreamHandlers.StreamChat(
            context,
            seeded.Channel.Id,
            new ChatRequest("sse hundred tools"),
            host.Chat,
            NullLoggerFactory.Instance);

        body.Position = 0;
        var sse = await new StreamReader(body).ReadToEndAsync();
        var beforeIndex = sse.IndexOf("before streamed tools", StringComparison.Ordinal);
        var toolIndex = sse.IndexOf(TestHarnessConstants.InlinePermissionedTool, StringComparison.Ordinal);
        var afterIndex = sse.IndexOf("after streamed tools", StringComparison.Ordinal);

        beforeIndex.Should().BeGreaterThanOrEqualTo(0);
        toolIndex.Should().BeGreaterThan(beforeIndex);
        afterIndex.Should().BeGreaterThan(toolIndex);

        var final = ParseDoneEvent(sse).FinalResponse!;
        final.AssistantMessage.Content.Should().EndWith("after streamed tools");
        final.ChannelCost!.TotalTokens.Should().BeGreaterThan(0);
        host.Harness.ToolCalls.Should().HaveCount(HundredCalls);
    }

    [TestCase(1)]
    [TestCase(10)]
    [TestCase(50)]
    public async Task ProviderStream_CancelAfterToolCalls_StopsPendingWorkAndDoesNotLeakPermissionCache(int cancelAfter)
    {
        await using var host = CreateHotPathHost();
        host.Harness.ConfigurePermissionedInlineTool(new TestHarnessToolBehavior
        {
            LatencyMs = 5,
            Result = ""
        });
        var seeded = await host.SeedChatAsync(
            TestHarnessConstants.StreamingProviderKey,
            grantHarnessPermission: true,
            agentSystemPrompt: "p");
        ConfigureStreamingScenario(
            host,
            BuildToolCalls(HundredCalls, TestHarnessConstants.InlinePermissionedTool),
            "should not finish");

        using var cts = new CancellationTokenSource();
        var seenToolNotations = 0;
        var act = async () =>
        {
            await foreach (var ev in host.Chat.SendMessageStreamAsync(
                seeded.Channel.Id,
                new ChatRequest($"cancel after {cancelAfter}"),
                (_, _) => Task.FromResult(true),
                ct: cts.Token))
            {
                if (ev.Type == ChatStreamEventType.TextDelta
                    && ev.Delta?.Contains(TestHarnessConstants.InlinePermissionedTool, StringComparison.Ordinal) == true)
                {
                    seenToolNotations++;
                    if (seenToolNotations == cancelAfter)
                        cts.Cancel();
                }
            }
        };

        await act.Should().ThrowAsync<OperationCanceledException>();
        host.Harness.ToolCalls.Count.Should().BeLessThan(HundredCalls);
        host.Harness.ProviderRequests.Should().HaveCount(1);
        host.Db.AgentJobs.Any(j => j.Status == AgentJobStatus.Executing).Should().BeFalse();

        var flags = await host.Db.GlobalFlags
            .Where(f => f.PermissionSetId == seeded.PermissionSet.Id)
            .ToListAsync();
        host.Db.GlobalFlags.RemoveRange(flags);
        await host.Db.SaveChangesAsync();
        host.Harness.Reset();

        await RunStreamingToolLoopAsync(
            host,
            seeded.Channel.Id,
            BuildToolCalls(1, TestHarnessConstants.InlinePermissionedTool),
            "after cancel denied");
        host.Harness.ToolCalls.Should().BeEmpty();
        ToolResultMessages(host).Single().Content.Should().Contain("permission denied");
    }

    [TestCaseSource(nameof(ToolNameAbuseCases))]
    public async Task ProviderStream_ToolNameVariants_ResolveOrFailDeterministically(
        string toolName,
        bool grantPermission,
        bool expectedInvocation,
        string? expectedErrorFragment)
    {
        await using var host = CreateHotPathHost();
        host.Harness.ConfigurePermissionedInlineTool(new TestHarnessToolBehavior { Result = "ok" });
        var seeded = await host.SeedChatAsync(
            TestHarnessConstants.StreamingProviderKey,
            grantHarnessPermission: grantPermission,
            agentSystemPrompt: "p");

        await RunStreamingToolLoopAsync(
            host,
            seeded.Channel.Id,
            [new ChatToolCall("abuse-1", toolName, "{}")],
            "after abuse");

        host.Harness.ToolCalls.Count.Should().Be(expectedInvocation ? 1 : 0);
        if (expectedErrorFragment is not null)
            ToolResultMessages(host).Single().Content.Should().Contain(expectedErrorFragment);
    }

    [Test]
    [Category(HarnessTestCategories.PerformanceGate)]
    public async Task PerformanceGate_ProviderStream_10ParallelChatsEachWith100AllowedNoOpToolCalls_NoGlobalSerialization()
    {
        var chats = new List<IsolatedHundredToolChat>();
        try
        {
            for (var i = 0; i < 10; i++)
                chats.Add(await CreateIsolatedHundredToolChatAsync(grantPermission: true));

            var (results, elapsedMs) = await RunParallelChatsAsync(chats, "parallel-allowed");

            results.Should().OnlyContain(r => r.ToolBodyInvocations == HundredCalls);
            elapsedMs.Should().BeLessThanOrEqualTo(
                results.Max(r => r.ClientVisibleMs) + 200,
                "parallel 100-tool chats should not serialize behind a process-wide lock");
            results.Select(r => r.LastFinalText).Should().OnlyHaveUniqueItems();
        }
        finally
        {
            foreach (var chat in chats)
                await chat.DisposeAsync();
        }
    }

    [Test]
    [Category(HarnessTestCategories.PerformanceGate)]
    public async Task PerformanceGate_ProviderStream_10ParallelChatsEachWith100DeniedToolCalls_NoGlobalSerialization()
    {
        var chats = new List<IsolatedHundredToolChat>();
        try
        {
            for (var i = 0; i < 10; i++)
                chats.Add(await CreateIsolatedHundredToolChatAsync(grantPermission: false));

            var (results, elapsedMs) = await RunParallelChatsAsync(chats, "parallel-denied");

            results.Should().OnlyContain(r => r.ToolBodyInvocations == 0);
            results.Should().OnlyContain(r => r.PermissionDeniedResults == HundredCalls);
            elapsedMs.Should().BeLessThanOrEqualTo(
                results.Max(r => r.ClientVisibleMs) + 200,
                "parallel denied 100-tool chats should stop at host permission checks without global serialization");
        }
        finally
        {
            foreach (var chat in chats)
                await chat.DisposeAsync();
        }
    }

    private static IEnumerable<TestCaseData> HundredToolCallCorrectnessCases()
    {
        yield return new TestCaseData("canonical-allowed", true, HundredCalls, 0, 0)
            .SetName("ProviderStream_100ToolCalls_CanonicalNames_AllAllowed");
        yield return new TestCaseData("alias-allowed", true, HundredCalls, 0, 0)
            .SetName("ProviderStream_100ToolCalls_AliasesResolveCanonicalPermissionDescriptors");
        yield return new TestCaseData("canonical-denied", false, 0, HundredCalls, 0)
            .SetName("ProviderStream_100ToolCalls_CanonicalNames_AllDenied");
        yield return new TestCaseData("malformed", true, 0, 0, HundredCalls)
            .SetName("ProviderStream_100ToolCalls_MalformedArguments_FailFast");
    }

    private static IEnumerable<TestCaseData> HundredToolCallPerCallTiers()
    {
        foreach (var maxMs in new[] { 1, 2, 5, 10, 25 })
        {
            yield return new TestCaseData(maxMs)
                .SetName($"ProviderStream_100AllowedNoOpToolCalls_PerCallOverhead_Under{maxMs}ms");
        }
    }

    private static IEnumerable<TestCaseData> ToolNameAbuseCases()
    {
        yield return new TestCaseData(TestHarnessConstants.InlinePermissionedTool, true, true, null)
            .SetName("ToolNameVariant_CanonicalAllowed");
        yield return new TestCaseData(TestHarnessConstants.InlinePermissionedToolAlias, true, true, null)
            .SetName("ToolNameVariant_AliasAllowed");
        yield return new TestCaseData(TestHarnessConstants.InlinePermissionedTool, false, false, "permission denied")
            .SetName("ToolNameVariant_CanonicalDeniedNoBypass");
        yield return new TestCaseData(TestHarnessConstants.InlinePermissionedToolAlias, false, false, "permission denied")
            .SetName("ToolNameVariant_AliasDeniedNoBypass");
        yield return new TestCaseData(TestHarnessConstants.InlinePermissionedTool.ToUpperInvariant(), true, false, "unrecognized")
            .SetName("ToolNameVariant_CasingMismatchFails");
        yield return new TestCaseData(" " + TestHarnessConstants.InlinePermissionedTool + " ", true, false, "unrecognized")
            .SetName("ToolNameVariant_WhitespaceMismatchFails");
        yield return new TestCaseData("th_" + TestHarnessConstants.InlinePermissionedTool, true, false, "unrecognized")
            .SetName("ToolNameVariant_ModulePrefixMismatchFails");
        yield return new TestCaseData("unknown_" + TestHarnessConstants.InlinePermissionedTool, true, false, "unrecognized")
            .SetName("ToolNameVariant_UnknownModuleIdFails");
        yield return new TestCaseData("test_harness_unknown_tool", true, false, "unrecognized")
            .SetName("ToolNameVariant_UnknownToolFails");
    }

    private static Task<RepeatedToolRunMeasurement> CachedMeasurementAsync(
        string key,
        Func<Task<RepeatedToolRunMeasurement>> factory) =>
        Measurements.GetOrAdd(key, _ => new Lazy<Task<RepeatedToolRunMeasurement>>(factory)).Value;

    private static async Task<RepeatedToolRunMeasurement> MeasureHundredAllowedWarmAsync()
    {
        await using var host = CreateHotPathHost();
        host.Harness.ConfigurePermissionedInlineTool(new TestHarnessToolBehavior { Result = "" });
        var seeded = await host.SeedChatAsync(
            TestHarnessConstants.StreamingProviderKey,
            grantHarnessPermission: true,
            agentSystemPrompt: "p");

        await RunStreamingToolLoopAsync(
            host,
            seeded.Channel.Id,
            BuildToolCalls(1, TestHarnessConstants.InlinePermissionedTool),
            "warm");

        host.Harness.Reset();
        host.Harness.ResetDiagnostics();
        host.Harness.ConfigurePermissionedInlineTool(new TestHarnessToolBehavior { Result = "" });
        var run = await RunStreamingToolLoopAsync(
            host,
            seeded.Channel.Id,
            BuildToolCalls(HundredCalls, TestHarnessConstants.InlinePermissionedTool),
            "allowed-warm");

        return BuildMeasurement(host, run);
    }

    private static async Task<RepeatedToolRunMeasurement> MeasureHundredDeniedWarmAsync()
    {
        await using var host = CreateHotPathHost();
        var seeded = await host.SeedChatAsync(
            TestHarnessConstants.StreamingProviderKey,
            grantHarnessPermission: false,
            agentSystemPrompt: "p");

        await RunStreamingToolLoopAsync(
            host,
            seeded.Channel.Id,
            BuildToolCalls(1, TestHarnessConstants.InlinePermissionedTool),
            "warm-denied");

        host.Harness.Reset();
        host.Harness.ResetDiagnostics();
        var run = await RunStreamingToolLoopAsync(
            host,
            seeded.Channel.Id,
            BuildToolCalls(HundredCalls, TestHarnessConstants.InlinePermissionedTool),
            "denied-warm");

        return BuildMeasurement(host, run);
    }

    private static async Task<IsolatedHundredToolChat> CreateIsolatedHundredToolChatAsync(bool grantPermission)
    {
        var host = CreateHotPathHost();
        host.Harness.ConfigurePermissionedInlineTool(new TestHarnessToolBehavior { Result = "" });
        try
        {
            var seeded = await host.SeedChatAsync(
                TestHarnessConstants.StreamingProviderKey,
                grantHarnessPermission: grantPermission,
                agentSystemPrompt: "p");
            return new IsolatedHundredToolChat(host, seeded.Channel.Id);
        }
        catch
        {
            await host.DisposeAsync();
            throw;
        }
    }

    private static async Task<(RepeatedToolRunMeasurement[] Results, long ElapsedMs)> RunParallelChatsAsync(
        IReadOnlyList<IsolatedHundredToolChat> chats,
        string finalTextPrefix)
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readyCount = 0;

        var tasks = chats
            .Select((chat, i) => Task.Run(async () =>
            {
                if (Interlocked.Increment(ref readyCount) == chats.Count)
                    allReady.TrySetResult();

                await release.Task;
                return await chat.RunAsync($"{finalTextPrefix}-{i}");
            }))
            .ToArray();

        try
        {
            await allReady.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch
        {
            release.TrySetResult();
            throw;
        }

        var sw = Stopwatch.StartNew();
        release.SetResult();
        var results = await Task.WhenAll(tasks);
        sw.Stop();

        return (results, sw.ElapsedMilliseconds);
    }

    private sealed class IsolatedHundredToolChat(ChatHarnessHost host, Guid channelId) : IAsyncDisposable
    {
        public async Task<RepeatedToolRunMeasurement> RunAsync(string finalText)
        {
            var run = await RunStreamingToolLoopAsync(
                host,
                channelId,
                BuildToolCalls(HundredCalls, TestHarnessConstants.InlinePermissionedTool),
                finalText);
            return BuildMeasurement(host, run);
        }

        public ValueTask DisposeAsync() => host.DisposeAsync();
    }

    private static ChatHarnessHost CreateHotPathHost() =>
        ChatHarnessHost.Create(new Dictionary<string, string?>
        {
            ["Chat:DisableDefaultHeaders"] = "true",
            ["Chat:DisableDefaultSystemPrompt"] = "true",
            ["Chat:DisableHeaderTagExpansion"] = "true",
            ["Chat:DisableModuleHeaderTags"] = "true",
            ["AgentOrchestration:DisableAccessibleThreadsHeader"] = "true",
            ["Chat:CacheMaxMegabytes"] = "16"
        });

    private static async Task<StreamingToolRun> RunStreamingToolLoopAsync(
        ChatHarnessHost host,
        Guid channelId,
        IReadOnlyList<ChatToolCall> calls,
        string finalText,
        IReadOnlyList<string>? beforeChunks = null,
        CancellationToken ct = default)
    {
        ConfigureStreamingScenario(host, calls, finalText, beforeChunks);

        var events = new List<ChatStreamEvent>();
        var sw = Stopwatch.StartNew();
        await foreach (var ev in host.Chat.SendMessageStreamAsync(
            channelId,
            new ChatRequest("repeat tools"),
            (_, _) => Task.FromResult(true),
            ct: ct))
        {
            events.Add(ev);
        }
        sw.Stop();

        return new StreamingToolRun(
            events,
            events.Single(e => e.Type == ChatStreamEventType.Done),
            sw.ElapsedMilliseconds);
    }

    private static void ConfigureStreamingScenario(
        ChatHarnessHost host,
        IReadOnlyList<ChatToolCall> calls,
        string finalText,
        IReadOnlyList<string>? beforeChunks = null)
    {
        host.Harness.ConfigureProvider(
            TestHarnessConstants.StreamingProviderKey,
            new TestHarnessProviderScenario
            {
                Turns =
                [
                    new TestHarnessProviderTurn
                    {
                        Content = null,
                        StreamingChunks = beforeChunks ?? ["before tools"],
                        ToolCalls = calls,
                        Usage = new TokenUsage(100, 10)
                    },
                    new TestHarnessProviderTurn
                    {
                        Content = null,
                        StreamingChunks = [finalText],
                        Usage = new TokenUsage(5, 7)
                    }
                ]
            });
    }

    private static RepeatedToolRunMeasurement BuildMeasurement(
        ChatHarnessHost host,
        StreamingToolRun run)
    {
        var providerActualMs = host.Harness.ProviderTimings.Sum(t => t.ElapsedMs);
        var overheadMs = Math.Max(0, run.ClientVisibleMs - providerActualMs);
        var toolCalls = host.Harness.ToolCalls
            .OrderBy(c => c.Sequence)
            .ToList();
        var interCallStats = TimedRunStats.From(InterCallDeltas(toolCalls));
        var resultMessages = ToolResultMessages(host);

        return new RepeatedToolRunMeasurement(
            run.ClientVisibleMs,
            providerActualMs,
            overheadMs,
            toolCalls.Count == 0 ? 0 : overheadMs / (double)Math.Max(1, toolCalls.Count),
            toolCalls.Count,
            resultMessages.Count(m => m.Content?.Contains("permission denied", StringComparison.OrdinalIgnoreCase) == true),
            host.Harness.PermissionDescriptorBuilds,
            interCallStats,
            run.Done.FinalResponse!.AssistantMessage.Content);
    }

    private static IReadOnlyList<long> InterCallDeltas(IReadOnlyList<CapturedToolCall> calls)
    {
        if (calls.Count < 2)
            return [0];

        var deltas = new List<long>(calls.Count - 1);
        for (var i = 1; i < calls.Count; i++)
        {
            var elapsed = Stopwatch.GetElapsedTime(
                calls[i - 1].CompletedAtTimestamp,
                calls[i].CompletedAtTimestamp);
            deltas.Add((long)Math.Ceiling(elapsed.TotalMilliseconds));
        }

        return deltas;
    }

    private static IReadOnlyList<ChatToolCall> BuildVariantCalls(string variant, int count) =>
        variant switch
        {
            "canonical-allowed" or "canonical-denied" =>
                BuildToolCalls(count, TestHarnessConstants.InlinePermissionedTool),
            "alias-allowed" =>
                BuildToolCalls(count, TestHarnessConstants.InlinePermissionedToolAlias),
            "mixed-open-denied" =>
                Enumerable.Range(0, count)
                    .Select(i => new ChatToolCall(
                        $"call-{i}",
                        i % 2 == 0
                            ? TestHarnessConstants.InlineOpenTool
                            : TestHarnessConstants.InlinePermissionedTool,
                        "{}"))
                    .ToList(),
            "malformed" =>
                Enumerable.Range(0, count)
                    .Select(i => new ChatToolCall($"call-{i}", TestHarnessConstants.InlineOpenTool, "{"))
                    .ToList(),
            _ => throw new ArgumentOutOfRangeException(nameof(variant), variant, null)
        };

    private static IReadOnlyList<ChatToolCall> BuildToolCalls(int count, string toolName) =>
        Enumerable.Range(0, count)
            .Select(i => new ChatToolCall($"call-{i}", toolName, "{}"))
            .ToList();

    private static IReadOnlyList<CapturedProviderMessage> ToolResultMessages(ChatHarnessHost host) =>
        host.Harness.ProviderRequests.Last()
            .Messages
            .Where(m => m.Role == "tool")
            .ToList();

    private static ChatStreamEvent ParseDoneEvent(string sse) =>
        sse.Split('\n')
            .Where(line => line.StartsWith("data: ", StringComparison.Ordinal))
            .Select(line => JsonSerializer.Deserialize<ChatStreamEvent>(line["data: ".Length..], JsonOptions))
            .OfType<ChatStreamEvent>()
            .Single(e => e.Type == ChatStreamEventType.Done);

    private sealed record StreamingToolRun(
        IReadOnlyList<ChatStreamEvent> Events,
        ChatStreamEvent Done,
        long ClientVisibleMs);

    private sealed record RepeatedToolRunMeasurement(
        long ClientVisibleMs,
        long ProviderActualMs,
        long SharpClawOverheadMs,
        double PerCallSharpClawOverheadMs,
        int ToolBodyInvocations,
        int PermissionDeniedResults,
        int DescriptorBuilds,
        TimedRunStats InterCallStats,
        string LastFinalText)
    {
        public override string ToString() =>
            $"clientVisibleMs={ClientVisibleMs}, providerActualMs={ProviderActualMs}, " +
            $"sharpClawOverheadMs={SharpClawOverheadMs}, perCallSharpClawOverheadMs={PerCallSharpClawOverheadMs:F3}, " +
            $"toolBodyInvocations={ToolBodyInvocations}, permissionDeniedResults={PermissionDeniedResults}, " +
            $"descriptorBuilds={DescriptorBuilds}, max={InterCallStats.Max}, p95={InterCallStats.P95}, p99={InterCallStats.P99}";
    }

    private sealed class LifecycleInlineToolModule : ISharpClawCoreModule
    {
        public const string ModuleId = "sharpclaw_lifecycle_test";
        public const string ToolName = "lifecycle_permissioned";

        public string Id => ModuleId;
        public string DisplayName => "Lifecycle Test";
        public string ToolPrefix => "life";

        public void ConfigureServices(IServiceCollection services)
        {
        }

        public IReadOnlyList<ModuleToolDefinition> GetToolDefinitions() => [];

        public IReadOnlyList<ModuleInlineToolDefinition> GetInlineToolDefinitions() =>
        [
            new(
                ToolName,
                "Lifecycle test inline tool.",
                EmptyObjectSchema(),
                new ModuleToolPermission(
                    IsPerResource: false,
                    Check: null,
                    DelegateTo: TestHarnessConstants.DelegateName))
        ];

        public async Task<string> ExecuteInlineToolAsync(
            string toolName,
            JsonElement parameters,
            InlineToolContext context,
            IServiceProvider scopedServices,
            CancellationToken ct)
        {
            await Task.Yield();
            return "lifecycle-ok";
        }

        public Task<string> ExecuteToolAsync(
            string toolName,
            JsonElement parameters,
            AgentJobContext job,
            IServiceProvider scopedServices,
            CancellationToken ct) =>
            throw new InvalidOperationException("Lifecycle test module only exposes inline tools.");

        private static JsonElement EmptyObjectSchema()
        {
            using var doc = JsonDocument.Parse("""
                {
                  "type": "object",
                  "properties": {},
                  "additionalProperties": false
                }
                """);
            return doc.RootElement.Clone();
        }
    }
}
