using System.Text.Json;
using FluentAssertions;
using SharpClaw.Presentation;
using SharpClaw.Contracts.Providers;

namespace SharpClaw.Tests.Frontend;

[TestFixture]
public class UnoClientStateTests
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    [Test]
    public void DirectChatRequest_UsesStatelessPayloadWithoutConversationIdentity()
    {
        var request = new UnoDirectChatRequest("hello");

        JsonSerializer.Serialize(request, Json).Should().Be("{\"message\":\"hello\"}");
        request.ConversationId.Should().BeNull();
    }

    [Test]
    public void DirectChatRequest_CanCarryExplicitConversationIdentity()
    {
        var id = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var request = new UnoDirectChatRequest("hello", id);

        JsonSerializer.Serialize(request, Json).Should().Be(
            "{\"message\":\"hello\",\"conversationId\":\"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa\"}");
    }

    [Test]
    public void SseStreamState_AccumulatesTextAndToolStatus()
    {
        var state = new UnoSseStreamState();

        state.Apply("TextDelta", "{\"delta\":\"first\"}");
        state.Apply("ToolCallStart", "{\"job\":{\"actionKey\":\"noop\",\"status\":\"Executing\"}}");
        state.Apply("TextDelta", "{\"delta\":\"second\"}");

        state.Text.Should().Be("first\n[noop] -> Executing\nsecond");
        state.DoneReceived.Should().BeFalse();
    }

    [Test]
    public void SseStreamState_DoneUsesFinalAssistantContent()
    {
        var state = new UnoSseStreamState();

        var result = state.Apply(
            "Done",
            "{\"finalResponse\":{\"assistantMessage\":{\"content\":\"final\"}}}");

        result.ShouldEnd.Should().BeTrue();
        result.TextChanged.Should().BeTrue();
        state.DoneReceived.Should().BeTrue();
        state.Text.Should().Be("final");
    }

    [Test]
    public void SseStreamState_ErrorStopsAndPreservesSafeErrorText()
    {
        var state = new UnoSseStreamState();

        var result = state.Apply("Error", "{\"error\":\"provider failed\"}");

        result.ShouldEnd.Should().BeTrue();
        state.ErrorReceived.Should().BeTrue();
        state.ErrorText.Should().Be("provider failed");
        state.DoneReceived.Should().BeFalse();
    }

    [Test]
    public void SseStreamState_UnknownEventsDoNotCompleteTheStream()
    {
        var state = new UnoSseStreamState();

        var result = state.Apply("Unknown", "{}");

        result.ShouldEnd.Should().BeFalse();
        result.TextChanged.Should().BeFalse();
        state.Text.Should().BeEmpty();
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Canonical_Runtime_data_only_chunks_complete_the_displayed_response(bool camelCase)
    {
        var state = new UnoSseStreamState();
        var options = camelCase ? Json : new JsonSerializerOptions();
        state.Apply(string.Empty, JsonSerializer.Serialize(ChatStreamChunk.Text("hello "), options));
        state.Apply(string.Empty, JsonSerializer.Serialize(ChatStreamChunk.Text("world"), options));
        state.Text.Should().Be("hello world");
        var end = state.Apply(string.Empty, JsonSerializer.Serialize(
            ChatStreamChunk.Final(new ChatCompletionResult { Content = "hello world" }), options));
        end.ShouldEnd.Should().BeTrue();
        state.DoneReceived.Should().BeTrue();
        state.ErrorReceived.Should().BeFalse();
        state.Text.Should().Be("hello world");
    }
}
