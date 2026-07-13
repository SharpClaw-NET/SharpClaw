using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SharpClaw.Runtime.Host.Handlers;
using SharpClaw.Contracts.DTOs.Chat;
using SharpClaw.Contracts.Entities.Core.Messages;
using SharpClaw.Contracts.Enums;
using SharpClaw.Contracts.Providers;
using SharpClaw.Tests.TestHarness;

namespace SharpClaw.Tests.TestHarness;

[TestFixture]
public sealed class TestHarnessStreamingCorrectnessTests
{
    [Test]
    public async Task StreamedChunksPreserveOrderAndForwardEmptyChunks()
    {
        await using var host = ChatHarnessHost.Create();
        var seeded = await host.SeedChatAsync(TestHarnessConstants.StreamingProviderKey);
        host.Harness.ConfigureProvider(
            TestHarnessConstants.StreamingProviderKey,
            new TestHarnessProviderScenario
            {
                Turns =
                [
                    new TestHarnessProviderTurn
                    {
                        Content = null,
                        StreamingChunks = ["alpha", "", "omega"],
                        Usage = new TokenUsage(3, 4)
                    }
                ]
            });

        var events = await CollectStreamAsync(host, seeded.Channel.Id, "stream order");

        events.Where(e => e.Type == ChatStreamEventType.TextDelta)
            .Select(e => e.Delta)
            .Should().Equal(["alpha", "", "omega"]);
        events.Single(e => e.Type == ChatStreamEventType.Done)
            .FinalResponse!.AssistantMessage.Content.Should().Be("alphaomega");
    }

    [Test]
    public async Task FirstTokenDelayIsMeasuredAsProviderTimeNotSharpClawOverhead()
    {
        await using var host = ChatHarnessHost.Create();
        var seeded = await host.SeedChatAsync(TestHarnessConstants.StreamingProviderKey);
        host.Harness.ConfigureProvider(
            TestHarnessConstants.StreamingProviderKey,
            new TestHarnessProviderScenario
            {
                Turns =
                [
                    new TestHarnessProviderTurn
                    {
                        Content = null,
                        StreamingChunks = ["first", "second"],
                        FirstTokenDelayMs = 200,
                        PerChunkDelayMs = 20
                    }
                ]
            });

        var sw = Stopwatch.StartNew();
        var firstDeltaAt = -1L;
        await foreach (var evt in host.Chat.SendMessageStreamAsync(
            seeded.Channel.Id,
            new ChatRequest("first-token"),
            (_, _) => Task.FromResult(true)))
        {
            if (evt.Type == ChatStreamEventType.TextDelta && firstDeltaAt < 0)
                firstDeltaAt = sw.ElapsedMilliseconds;
        }
        sw.Stop();

        firstDeltaAt.Should().BeGreaterThanOrEqualTo(180);
        host.Harness.ProviderTimings.Single().ConfiguredDelayMs.Should().Be(220);
        HarnessBudget.AssertOverheadAbsolute(sw.ElapsedMilliseconds, 220, 500, "first-token stream");
    }

    [Test]
    public async Task CancellationStopsProviderWorkBeforeRemainingDelayedChunks()
    {
        await using var host = ChatHarnessHost.Create();
        var seeded = await host.SeedChatAsync(TestHarnessConstants.StreamingProviderKey);
        host.Harness.ConfigureProvider(
            TestHarnessConstants.StreamingProviderKey,
            new TestHarnessProviderScenario
            {
                Turns =
                [
                    new TestHarnessProviderTurn
                    {
                        Content = null,
                        StreamingChunks = ["one", "two", "three"],
                        PerChunkDelayMs = 1000
                    }
                ]
            });

        using var cts = new CancellationTokenSource();
        var sw = Stopwatch.StartNew();
        var deltas = new List<string?>();
        var act = async () =>
        {
            await foreach (var evt in host.Chat.SendMessageStreamAsync(
                seeded.Channel.Id,
                new ChatRequest("cancel"),
                (_, _) => Task.FromResult(true),
                ct: cts.Token))
            {
                if (evt.Type != ChatStreamEventType.TextDelta)
                    continue;
                deltas.Add(evt.Delta);
                cts.Cancel();
            }
        };

        await act.Should().ThrowAsync<OperationCanceledException>();
        sw.Stop();

        deltas.Should().Equal(["one"]);
        sw.ElapsedMilliseconds.Should().BeLessThan(
            1500,
            "cancellation should happen before the second delayed chunk completes");
    }

    [Test]
    public async Task MidStreamProviderFailureBecomesStableSseErrorEvent()
    {
        await using var host = ChatHarnessHost.Create();
        var seeded = await host.SeedChatAsync(TestHarnessConstants.StreamingProviderKey);
        host.Harness.ConfigureProvider(
            TestHarnessConstants.StreamingProviderKey,
            new TestHarnessProviderScenario
            {
                Turns =
                [
                    new TestHarnessProviderTurn
                    {
                        Content = null,
                        StreamingChunks = ["before", "after"],
                        StreamFailureAfterChunks = 1
                    }
                ]
            });

        var direct = async () => await CollectStreamAsync(host, seeded.Channel.Id, "direct failure");
        await direct.Should().ThrowAsync<Exception>();

        host.Harness.Reset();
        seeded = await host.SeedChatAsync(TestHarnessConstants.StreamingProviderKey);
        host.Harness.ConfigureProvider(
            TestHarnessConstants.StreamingProviderKey,
            new TestHarnessProviderScenario
            {
                Turns =
                [
                    new TestHarnessProviderTurn
                    {
                        Content = null,
                        StreamingChunks = ["before", "after"],
                        StreamFailureAfterChunks = 1
                    }
                ]
            });

        var context = new DefaultHttpContext();
        await using var body = new MemoryStream();
        context.Response.Body = body;

        await ChatStreamHandlers.StreamChat(
            context,
            seeded.Channel.Id,
            new ChatRequest("sse failure"),
            host.Chat,
            NullLoggerFactory.Instance);

        body.Position = 0;
        var sse = await new StreamReader(body).ReadToEndAsync();
        sse.Should().Contain("event: Error");
        sse.Should().Contain("ResponseEnded");
    }

    [Test]
    public async Task FinalUsageAndCostMetadataArePreservedAfterStreaming()
    {
        await using var host = ChatHarnessHost.Create();
        var seeded = await host.SeedChatAsync(TestHarnessConstants.StreamingProviderKey);
        host.Harness.ConfigureProvider(
            TestHarnessConstants.StreamingProviderKey,
            new TestHarnessProviderScenario
            {
                Turns =
                [
                    new TestHarnessProviderTurn
                    {
                        Content = null,
                        StreamingChunks = ["cost"],
                        Usage = new TokenUsage(13, 17),
                        ProviderMetadataJson = """{"round":"final"}"""
                    }
                ]
            });

        var events = await CollectStreamAsync(host, seeded.Channel.Id, "cost stream");

        var done = events.Single(e => e.Type == ChatStreamEventType.Done);
        done.FinalResponse!.ChannelCost!.TotalTokens.Should().Be(30);
        done.FinalResponse.AgentCost!.TotalTokens.Should().Be(30);

        var assistant = await host.Db.ChatMessages
            .Where(m => m.Origin == MessageOrigin.Assistant)
            .SingleAsync();
        assistant.PromptTokens.Should().Be(13);
        assistant.CompletionTokens.Should().Be(17);
        assistant.ProviderMetadataJson.Should().Be("""{"round":"final"}""");
    }

    [Test]
    public async Task ChatServiceStreamingPathInvokesProviderExecutionAndPersistsCompletedMessages()
    {
        await using var host = ChatHarnessHost.Create(new Dictionary<string, string?>
        {
            ["Chat:DisableDefaultHeaders"] = "true"
        });
        var seeded = await host.SeedChatAsync(TestHarnessConstants.StreamingProviderKey);
        host.Harness.ConfigureProvider(
            TestHarnessConstants.StreamingProviderKey,
            new TestHarnessProviderScenario
            {
                Turns =
                [
                    new TestHarnessProviderTurn
                    {
                        Content = null,
                        StreamingChunks = ["completed ", "app ", "stream"],
                        Usage = new TokenUsage(5, 6),
                        ProviderMetadataJson = """{"stream":"completed"}"""
                    }
                ]
            });

        var events = await CollectStreamAsync(host, seeded.Channel.Id, "app stream");

        var request = host.Harness.ProviderRequests.Single();
        request.Surface.Should().Be("stream-tools");
        request.Messages.Single(m => m.Role == "user").Content.Should().Be("app stream");
        host.Harness.ProviderTimings.Single().Surface.Should().Be("stream-tools");

        var done = events.Single(e => e.Type == ChatStreamEventType.Done);
        done.FinalResponse!.AssistantMessage.Content.Should().Be("completed app stream");

        var user = await host.Db.ChatMessages
            .Where(m => m.Origin == MessageOrigin.User)
            .SingleAsync();
        var assistant = await host.Db.ChatMessages
            .Where(m => m.Origin == MessageOrigin.Assistant)
            .SingleAsync();
        user.Content.Should().Be("app stream");
        assistant.Content.Should().Be("completed app stream");
        assistant.PromptTokens.Should().Be(5);
        assistant.CompletionTokens.Should().Be(6);
        assistant.ProviderMetadataJson.Should().Be("""{"stream":"completed"}""");
    }

    [Test]
    public async Task ChatServiceStreamingPathPersistsPartialAssistantMessageAfterInterruption()
    {
        await using var host = ChatHarnessHost.Create(new Dictionary<string, string?>
        {
            ["Chat:DisableDefaultHeaders"] = "true"
        });
        var seeded = await host.SeedChatAsync(TestHarnessConstants.StreamingProviderKey);
        host.Harness.ConfigureProvider(
            TestHarnessConstants.StreamingProviderKey,
            new TestHarnessProviderScenario
            {
                Turns =
                [
                    new TestHarnessProviderTurn
                    {
                        Content = null,
                        StreamingChunks = ["partial", " ignored"],
                        PerChunkDelayMs = 1000
                    }
                ]
            });

        using var cts = new CancellationTokenSource();
        var received = new List<string?>();
        var act = async () =>
        {
            await foreach (var evt in host.Chat.SendMessageStreamAsync(
                seeded.Channel.Id,
                new ChatRequest("interrupt stream"),
                (_, _) => Task.FromResult(true),
                ct: cts.Token))
            {
                if (evt.Type != ChatStreamEventType.TextDelta)
                    continue;

                received.Add(evt.Delta);
                cts.Cancel();
            }
        };

        await act.Should().ThrowAsync<OperationCanceledException>();

        received.Should().Equal(["partial"]);
        host.Harness.ProviderRequests.Single().Surface.Should().Be("stream-tools");

        var user = await host.Db.ChatMessages
            .Where(m => m.Origin == MessageOrigin.User)
            .SingleAsync();
        var assistant = await host.Db.ChatMessages
            .Where(m => m.Origin == MessageOrigin.Assistant)
            .SingleAsync();
        user.Content.Should().Be("interrupt stream");
        assistant.Content.Should().Be("partial");
        assistant.PromptTokens.Should().BeNull();
        assistant.CompletionTokens.Should().BeNull();
        assistant.ProviderMetadataJson.Should().BeNull();
    }

    [TestCaseSource(nameof(StreamPayloadCases))]
    public async Task TextPayloadsStreamWithoutCorruption(string name, IReadOnlyList<string> chunks)
    {
        await using var host = ChatHarnessHost.Create();
        var seeded = await host.SeedChatAsync(TestHarnessConstants.StreamingProviderKey);
        host.Harness.ConfigureProvider(
            TestHarnessConstants.StreamingProviderKey,
            new TestHarnessProviderScenario
            {
                Turns =
                [
                    new TestHarnessProviderTurn
                    {
                        Content = null,
                        StreamingChunks = chunks
                    }
                ]
            });

        var events = await CollectStreamAsync(host, seeded.Channel.Id, name);

        var expected = string.Concat(chunks);
        string.Concat(events.Where(e => e.Type == ChatStreamEventType.TextDelta).Select(e => e.Delta))
            .Should().Be(expected);
        events.Single(e => e.Type == ChatStreamEventType.Done)
            .FinalResponse!.AssistantMessage.Content.Should().Be(expected);
    }

    private static IEnumerable<TestCaseData> StreamPayloadCases()
    {
        yield return new TestCaseData("unicode", new[] { "Hello ", "Zagreb ", "cafe\u0301 ", "\u2603" });
        yield return new TestCaseData("json-looking", new[] { "{\"a\":", "\"b\"}" });
        yield return new TestCaseData("tool-call-looking", new[] { "{\"tool_calls\":[{", "\"name\":\"x\"}]}" });
        yield return new TestCaseData("long-text", new[] { new string('x', 16_000), new string('y', 16_000) });
    }

    private static async Task<List<ChatStreamEvent>> CollectStreamAsync(
        ChatHarnessHost host,
        Guid channelId,
        string message,
        CancellationToken ct = default)
    {
        var events = new List<ChatStreamEvent>();
        await foreach (var evt in host.Chat.SendMessageStreamAsync(
            channelId,
            new ChatRequest(message),
            (_, _) => Task.FromResult(true),
            ct: ct))
        {
            events.Add(evt);
        }

        return events;
    }
}
