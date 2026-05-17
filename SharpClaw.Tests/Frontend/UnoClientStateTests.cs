using System.Net.Http;
using System.Text.Json;
using FluentAssertions;
using SharpClaw.Presentation;

namespace SharpClaw.Tests.Frontend;

[TestFixture]
public class UnoClientStateTests
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    [Test]
    public void CreateChannelRequest_TruncatesTitleAndCarriesSelectedAgent()
    {
        var agentId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

        var request = UnoClientState.CreateChannelRequest("0123456789abcdef", agentId);

        request.Title.Should().Be("0123456789\u2026");
        request.AgentId.Should().Be(agentId);
        JsonSerializer.Serialize(request, Json).Should().Be(
            """{"title":"0123456789\u2026","agentId":"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"}""");
    }

    [Test]
    public void CreateThreadRequests_UseDefaultOrMessageDerivedNames()
    {
        UnoClientState.CreateDefaultThreadRequest().Name.Should().Be("Default");
        UnoClientState.CreatePendingThreadRequest("new thread name").Name.Should().Be("new thread\u2026");
    }

    [Test]
    public void CreateChatStreamRequest_IncludesThreadAgentAndClientType()
    {
        var channelId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var threadId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
        var agentId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");

        var request = UnoClientState.CreateChatStreamRequest(
            channelId,
            threadId,
            "hello",
            agentId,
            "uno-windows");

        request.Path.Should().Be(
            "/channels/bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb/chat/threads/cccccccc-cccc-cccc-cccc-cccccccccccc/stream");
        request.Body.Should().Be(new UnoChatStreamRequestBody("hello", agentId, "uno-windows"));
        JsonSerializer.Serialize(request.Body, Json).Should().Be(
            """{"message":"hello","agentId":"dddddddd-dddd-dddd-dddd-dddddddddddd","clientType":"uno-windows"}""");
    }

    [Test]
    public void CreateChatStreamRequest_WithoutThread_UsesChannelStreamEndpoint()
    {
        var channelId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

        var request = UnoClientState.CreateChatStreamRequest(channelId, null, "hello", null, "uno-browser");

        request.Path.Should().Be("/channels/bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb/chat/stream");
        request.Body.AgentId.Should().BeNull();
    }

    [Test]
    public void ThreadWatchProcessing_DisablesInputAndMarksThreadBusy()
    {
        var initial = new UnoChatInteractionState(IsSending: false, IsThreadBusy: false, HistoryStaleAfterSend: false);

        var decision = UnoClientState.ApplyThreadWatchEvent(initial, "Processing");

        decision.State.IsThreadBusy.Should().BeTrue();
        decision.SendEnabled.Should().BeFalse();
        decision.InputEnabled.Should().BeFalse();
        decision.LoadHistoryNow.Should().BeFalse();
    }

    [Test]
    public void ThreadWatchNewMessages_WhenIdle_ReloadsImmediately()
    {
        var initial = new UnoChatInteractionState(IsSending: false, IsThreadBusy: true, HistoryStaleAfterSend: false);

        var decision = UnoClientState.ApplyThreadWatchEvent(initial, "NewMessages");

        decision.State.IsThreadBusy.Should().BeFalse();
        decision.State.HistoryStaleAfterSend.Should().BeFalse();
        decision.LoadHistoryNow.Should().BeTrue();
        decision.LoadCostNow.Should().BeTrue();
        decision.ScrollToBottom.Should().BeTrue();
        decision.SendEnabled.Should().BeTrue();
        decision.InputEnabled.Should().BeTrue();
    }

    [Test]
    public void ThreadWatchNewMessages_DuringSend_DefersReloadUntilSendCompletes()
    {
        var sending = new UnoChatInteractionState(IsSending: true, IsThreadBusy: true, HistoryStaleAfterSend: false);

        var duringSend = UnoClientState.ApplyThreadWatchEvent(sending, "NewMessages");
        var complete = UnoClientState.CompleteSend(duringSend.State);

        duringSend.LoadHistoryNow.Should().BeFalse();
        duringSend.State.HistoryStaleAfterSend.Should().BeTrue();
        complete.LoadHistoryNow.Should().BeTrue();
        complete.LoadCostNow.Should().BeTrue();
        complete.ScrollToBottom.Should().BeTrue();
        complete.State.HistoryStaleAfterSend.Should().BeFalse();
        complete.SendEnabled.Should().BeTrue();
    }

    [TestCase("AwaitingApproval", new[] { UnoJobActionKind.Approve, UnoJobActionKind.Cancel })]
    [TestCase("Queued", new[] { UnoJobActionKind.Cancel, UnoJobActionKind.Stop, UnoJobActionKind.Pause })]
    [TestCase("Executing", new[] { UnoJobActionKind.Cancel, UnoJobActionKind.Stop, UnoJobActionKind.Pause })]
    [TestCase("Paused", new[] { UnoJobActionKind.Resume, UnoJobActionKind.Cancel })]
    [TestCase("Completed", new UnoJobActionKind[0])]
    public void GetVisibleJobActions_MatchesJobStatus(string status, UnoJobActionKind[] expected)
    {
        UnoClientState.GetVisibleJobActions(status).Should().Equal(expected);
    }

    [TestCase(UnoJobActionKind.Approve, "POST", "approve", true)]
    [TestCase(UnoJobActionKind.Cancel, "POST", "cancel", false)]
    [TestCase(UnoJobActionKind.Stop, "POST", "stop", false)]
    [TestCase(UnoJobActionKind.Pause, "PUT", "pause", false)]
    [TestCase(UnoJobActionKind.Resume, "PUT", "resume", false)]
    public void CreateJobActionRequest_UsesExpectedMethodRouteAndBody(
        UnoJobActionKind action,
        string method,
        string suffix,
        bool sendsEmptyJsonBody)
    {
        var channelId = Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee");
        var jobId = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff");

        var request = UnoClientState.CreateJobActionRequest(channelId, jobId, action);

        request.Method.Should().Be(new HttpMethod(method));
        request.Path.Should().Be(
            $"/channels/eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee/jobs/ffffffff-ffff-ffff-ffff-ffffffffffff/{suffix}");
        request.SendsEmptyJsonBody.Should().Be(sendsEmptyJsonBody);
    }

    [Test]
    public void SseStreamState_AccumulatesTextToolStatusAndDoneCost()
    {
        var state = new UnoSseStreamState();

        state.Apply("TextDelta", """{"delta":"first"}""");
        state.Apply("ToolCallStart", """{"job":{"actionKey":"noop","status":"Executing"}}""");
        state.Apply("TextDelta", """{"delta":"second"}""");
        state.Text.Should().Be("first\n[noop] -> Executing\nsecond");

        var done = state.Apply("Done",
            """
            {"finalResponse":{"assistantMessage":{"content":"final"},"channelCost":{"totalTokens":9},"threadCost":{"totalTokens":4}}}
            """);

        done.ShouldEnd.Should().BeTrue();
        state.DoneReceived.Should().BeTrue();
        state.Text.Should().Be("final");
        state.Cost.Should().Be(new UnoStreamCostSnapshot(9, 4));
        state.ShouldLoadCostFallback.Should().BeFalse();
    }

    [Test]
    public void SseStreamState_EndedWithoutDone_RequiresCostFallback()
    {
        var state = new UnoSseStreamState();

        state.Apply("TextDelta", """{"delta":"partial"}""");

        state.Text.Should().Be("partial");
        state.ShouldLoadCostFallback.Should().BeTrue();
    }

    [Test]
    public void SseStreamState_ErrorEndsStreamWithoutCostFallback()
    {
        var state = new UnoSseStreamState();

        var result = state.Apply("Error", """{"error":"provider failed"}""");

        result.ShouldEnd.Should().BeTrue();
        state.ErrorReceived.Should().BeTrue();
        state.ErrorText.Should().Be("provider failed");
        state.ShouldLoadCostFallback.Should().BeFalse();
    }
}
