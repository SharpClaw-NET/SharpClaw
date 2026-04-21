using System.Text.Json;
using SharpClaw.Application.Core.Clients;
using SharpClaw.Application.Core.LocalInference;

namespace SharpClaw.Tests.LocalInference;

[TestFixture]
public class LlamaSharpToolPromptBuilderTests
{
    // ── System prompt ─────────────────────────────────────────────

    [Test]
    public void Build_NoTools_SystemPromptContainsEnvelopeContract()
    {
        var history = LlamaSharpToolPromptBuilder.Build(null, [], []);

        var systemMessage = history.Messages.First(m => m.AuthorRole == LLama.Common.AuthorRole.System);
        systemMessage.Content.Should().Contain("mode");
        systemMessage.Content.Should().Contain("tool_calls");
        systemMessage.Content.Should().Contain("message");
    }

    [Test]
    public void Build_WithSystemPrompt_PrependedBeforeEnvelope()
    {
        var history = LlamaSharpToolPromptBuilder.Build("Be concise.", [], []);

        var systemMessage = history.Messages.First(m => m.AuthorRole == LLama.Common.AuthorRole.System);
        systemMessage.Content.Should().StartWith("Be concise.");
        systemMessage.Content.Should().Contain("## Tool calling");
    }

    [Test]
    public void Build_NullSystemPrompt_NoLeadingNewlines()
    {
        var history = LlamaSharpToolPromptBuilder.Build(null, [], []);

        var systemMessage = history.Messages.First(m => m.AuthorRole == LLama.Common.AuthorRole.System);
        systemMessage.Content.Should().NotStartWith("\n");
    }

    [Test]
    public void Build_WithTools_SystemPromptListsToolNames()
    {
        var tools = new List<ChatToolDefinition>
        {
            MakeTool("get_weather", "Gets the current weather.", """{"properties":{"location":{"type":"string","description":"City name"}}}"""),
            MakeTool("search_web", "Searches the web.", """{"properties":{}}"""),
        };

        var history = LlamaSharpToolPromptBuilder.Build(null, [], tools);

        var systemMessage = history.Messages.First(m => m.AuthorRole == LLama.Common.AuthorRole.System);
        systemMessage.Content.Should().Contain("get_weather");
        systemMessage.Content.Should().Contain("Gets the current weather.");
        systemMessage.Content.Should().Contain("search_web");
        systemMessage.Content.Should().Contain("location");
        systemMessage.Content.Should().Contain("string");
        systemMessage.Content.Should().Contain("City name");
    }

    [Test]
    public void Build_NoTools_NoAvailableToolsSection()
    {
        var history = LlamaSharpToolPromptBuilder.Build(null, [], []);

        var systemMessage = history.Messages.First(m => m.AuthorRole == LLama.Common.AuthorRole.System);
        systemMessage.Content.Should().NotContain("## Available tools");
    }

    // ── User messages ─────────────────────────────────────────────

    [Test]
    public void Build_UserMessage_MappedToUserRole()
    {
        var messages = new List<ToolAwareMessage>
        {
            ToolAwareMessage.User("Hello"),
        };

        var history = LlamaSharpToolPromptBuilder.Build(null, messages, []);

        history.Messages.Should().Contain(m =>
            m.AuthorRole == LLama.Common.AuthorRole.User && m.Content == "Hello");
    }

    // ── Plain assistant messages ──────────────────────────────────

    [Test]
    public void Build_PlainAssistantMessage_WrappedAsMessageEnvelope()
    {
        var messages = new List<ToolAwareMessage>
        {
            ToolAwareMessage.Assistant("Sure, I can help with that."),
        };

        var history = LlamaSharpToolPromptBuilder.Build(null, messages, []);

        var assistantMsg = history.Messages.First(m => m.AuthorRole == LLama.Common.AuthorRole.Assistant);
        using var doc = JsonDocument.Parse(assistantMsg.Content);
        doc.RootElement.GetProperty("mode").GetString().Should().Be("message");
        doc.RootElement.GetProperty("text").GetString().Should().Be("Sure, I can help with that.");
        doc.RootElement.GetProperty("calls").GetArrayLength().Should().Be(0);
    }

    [Test]
    public void Build_PlainAssistantMessage_NullContent_WrappedWithEmptyText()
    {
        var messages = new List<ToolAwareMessage>
        {
            ToolAwareMessage.Assistant(""),
        };

        var history = LlamaSharpToolPromptBuilder.Build(null, messages, []);

        var assistantMsg = history.Messages.First(m => m.AuthorRole == LLama.Common.AuthorRole.Assistant);
        using var doc = JsonDocument.Parse(assistantMsg.Content);
        doc.RootElement.GetProperty("text").GetString().Should().BeEmpty();
    }

    // ── Tool call assistant messages ──────────────────────────────

    [Test]
    public void Build_AssistantWithToolCalls_FormattedAsToolCallsEnvelope()
    {
        var messages = new List<ToolAwareMessage>
        {
            new()
            {
                Role = "assistant",
                Content = "",
                ToolCalls =
                [
                    new ChatToolCall("call_1", "get_weather", """{"location":"Paris"}"""),
                ],
            },
        };

        var history = LlamaSharpToolPromptBuilder.Build(null, messages, []);

        var assistantMsg = history.Messages.First(m => m.AuthorRole == LLama.Common.AuthorRole.Assistant);
        using var doc = JsonDocument.Parse(assistantMsg.Content);
        doc.RootElement.GetProperty("mode").GetString().Should().Be("tool_calls");

        var calls = doc.RootElement.GetProperty("calls");
        calls.GetArrayLength().Should().Be(1);
        calls[0].GetProperty("id").GetString().Should().Be("call_1");
        calls[0].GetProperty("name").GetString().Should().Be("get_weather");
        calls[0].GetProperty("args").GetProperty("location").GetString().Should().Be("Paris");
    }

    [Test]
    public void Build_AssistantWithToolCalls_InvalidArgsJson_UsesEmptyObject()
    {
        var messages = new List<ToolAwareMessage>
        {
            ToolAwareMessage.AssistantWithToolCalls(
                [new ChatToolCall("call_2", "broken", "not-json")]),
        };

        var history = LlamaSharpToolPromptBuilder.Build(null, messages, []);

        var assistantMsg = history.Messages.First(m => m.AuthorRole == LLama.Common.AuthorRole.Assistant);
        using var doc = JsonDocument.Parse(assistantMsg.Content);
        var args = doc.RootElement.GetProperty("calls")[0].GetProperty("args");
        args.ValueKind.Should().Be(JsonValueKind.Object);
        args.EnumerateObject().Should().BeEmpty();
    }

    [Test]
    public void Build_AssistantWithToolCalls_NullArgs_UsesEmptyObject()
    {
        var messages = new List<ToolAwareMessage>
        {
            ToolAwareMessage.AssistantWithToolCalls(
                [new ChatToolCall("call_3", "noop", "")]),
        };

        var history = LlamaSharpToolPromptBuilder.Build(null, messages, []);

        var assistantMsg = history.Messages.First(m => m.AuthorRole == LLama.Common.AuthorRole.Assistant);
        using var doc = JsonDocument.Parse(assistantMsg.Content);
        doc.RootElement.GetProperty("calls")[0].GetProperty("args").ValueKind
            .Should().Be(JsonValueKind.Object);
    }

    // ── Tool result messages ──────────────────────────────────────

    [Test]
    public void Build_ToolResultMessage_MappedToUserRoleWithResultEnvelope()
    {
        var messages = new List<ToolAwareMessage>
        {
            ToolAwareMessage.ToolResult("call_1", "Sunny, 22°C"),
        };

        var history = LlamaSharpToolPromptBuilder.Build(null, messages, []);

        var userMsg = history.Messages.First(m => m.AuthorRole == LLama.Common.AuthorRole.User);
        using var doc = JsonDocument.Parse(userMsg.Content);
        var result = doc.RootElement.GetProperty("tool_result");
        result.GetProperty("id").GetString().Should().Be("call_1");
        result.GetProperty("content").GetString().Should().Be("Sunny, 22°C");
    }

    // ── Multi-turn round-trip ─────────────────────────────────────

    [Test]
    public void Build_MultiTurn_MessageOrderPreserved()
    {
        var messages = new List<ToolAwareMessage>
        {
            new() { Role = "user",      Content = "What is the weather in Tokyo?" },
            new() { Role = "assistant", Content = "", ToolCalls = [new ChatToolCall("c1", "get_weather", """{"location":"Tokyo"}""")] },
            new() { Role = "tool",      ToolCallId = "c1", Content = "Rainy, 15°C" },
            new() { Role = "assistant", Content = "It is rainy in Tokyo at 15°C." },
        };

        var history = LlamaSharpToolPromptBuilder.Build("You are a weather assistant.", messages, []);

        // system + 4 conversation turns
        history.Messages.Should().HaveCount(5);
        history.Messages[0].AuthorRole.Should().Be(LLama.Common.AuthorRole.System);
        history.Messages[1].AuthorRole.Should().Be(LLama.Common.AuthorRole.User);
        history.Messages[2].AuthorRole.Should().Be(LLama.Common.AuthorRole.Assistant);
        history.Messages[3].AuthorRole.Should().Be(LLama.Common.AuthorRole.User);   // tool result → user
        history.Messages[4].AuthorRole.Should().Be(LLama.Common.AuthorRole.Assistant);
    }

    [Test]
    public void Build_MultiTurn_AllAssistantTurnsAreValidJson()
    {
        var messages = new List<ToolAwareMessage>
        {
            ToolAwareMessage.Assistant("Thinking..."),
            ToolAwareMessage.AssistantWithToolCalls([new ChatToolCall("c1", "lookup", "{}")]),
        };

        var history = LlamaSharpToolPromptBuilder.Build(null, messages, []);

        foreach (var msg in history.Messages.Where(m => m.AuthorRole == LLama.Common.AuthorRole.Assistant))
        {
            var act = () => JsonDocument.Parse(msg.Content);
            act.Should().NotThrow();
        }
    }

    // ── Helpers ───────────────────────────────────────────────────

    private static ChatToolDefinition MakeTool(string name, string description, string schemaJson)
    {
        using var doc = JsonDocument.Parse(schemaJson);
        return new ChatToolDefinition(name, description, doc.RootElement.Clone());
    }
}
