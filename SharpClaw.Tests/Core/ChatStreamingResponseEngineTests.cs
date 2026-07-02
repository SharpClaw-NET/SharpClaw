using SharpClaw.Contracts.DTOs.Chat;
using SharpClaw.Core.Chat;

namespace SharpClaw.Tests.Core;

[TestFixture]
public sealed class ChatStreamingResponseEngineTests
{
    [Test]
    public async Task RunAsync_WhenNativeEventsAreProduced_MapsOutboundEventsAndAccumulatesAssistantText()
    {
        var state = new ChatStreamingResponseState();
        var finalResult = new ChatNativeToolStreamingLoopResult(
            "final",
            [],
            TotalPromptTokens: 3,
            TotalCompletionTokens: 4,
            ProviderMetadataJson: """{"ok":true}""",
            ProviderRounds: 2);

        var events = await CollectAsync(
            new ChatStreamingResponseEngine().RunAsync(
                NativeEvents(
                    ChatNativeToolStreamingLoopEvent.TextDelta("visible"),
                    ChatNativeToolStreamingLoopEvent.BufferedText("[tool]"),
                    ChatNativeToolStreamingLoopEvent.StreamEvent(
                        ChatStreamEvent.Err("structured")),
                    ChatNativeToolStreamingLoopEvent.Completed(finalResult)),
                state));

        events.Select(e => e.Kind).Should().Equal(
            ChatStreamingResponseEventKind.StreamEvent,
            ChatStreamingResponseEventKind.StreamEvent,
            ChatStreamingResponseEventKind.Completed);
        events[0].StreamEvent!.Type.Should().Be(ChatStreamEventType.TextDelta);
        events[0].StreamEvent!.Delta.Should().Be("visible");
        events[1].StreamEvent!.Type.Should().Be(ChatStreamEventType.Error);
        events[1].StreamEvent!.Error.Should().Be("structured");
        events[2].Result.Should().BeSameAs(finalResult);
        state.PartialAssistantContent.Should().Be("visible[tool]");
        state.Completed.Should().BeTrue();
        state.CompletionResult.Should().BeSameAs(finalResult);
    }

    [Test]
    public async Task RunAsync_WhenStreamEndsWithoutCompletion_ThrowsAndKeepsPartialText()
    {
        var state = new ChatStreamingResponseState();

        var act = async () => await CollectAsync(
            new ChatStreamingResponseEngine().RunAsync(
                NativeEvents(
                    ChatNativeToolStreamingLoopEvent.TextDelta("partial"),
                    ChatNativeToolStreamingLoopEvent.BufferedText("[hidden]")),
                state));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Core streaming loop ended without a completion event.");
        state.PartialAssistantContent.Should().Be("partial[hidden]");
        state.Completed.Should().BeFalse();
        state.CompletionResult.Should().BeNull();
    }

    [Test]
    public async Task RunAsync_WhenCompletedEventHasNoResult_ThrowsCompletionInvariant()
    {
        var state = new ChatStreamingResponseState();

        var act = async () => await CollectAsync(
            new ChatStreamingResponseEngine().RunAsync(
                NativeEvents(new ChatNativeToolStreamingLoopEvent(
                    ChatNativeToolStreamingLoopEventKind.Completed)),
                state));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Core streaming loop completed without a result.");
        state.Completed.Should().BeFalse();
    }

    [Test]
    public async Task RunAsync_WhenNativeEventKindIsUnknown_ThrowsWithoutFallback()
    {
        var state = new ChatStreamingResponseState();

        var act = async () => await CollectAsync(
            new ChatStreamingResponseEngine().RunAsync(
                NativeEvents(new ChatNativeToolStreamingLoopEvent(
                    (ChatNativeToolStreamingLoopEventKind)999)),
                state));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Unknown native chat streaming event kind '999'.");
        state.PartialAssistantContent.Should().BeEmpty();
        state.Completed.Should().BeFalse();
    }

    private static async Task<List<ChatStreamingResponseEvent>> CollectAsync(
        IAsyncEnumerable<ChatStreamingResponseEvent> source)
    {
        var events = new List<ChatStreamingResponseEvent>();
        await foreach (var item in source)
            events.Add(item);
        return events;
    }

    private static async IAsyncEnumerable<ChatNativeToolStreamingLoopEvent> NativeEvents(
        params ChatNativeToolStreamingLoopEvent[] events)
    {
        foreach (var item in events)
        {
            await Task.Yield();
            yield return item;
        }
    }
}
