using SharpClaw.Application.Core.Clients;
using SharpClaw.Application.Core.LocalInference;

namespace SharpClaw.Tests.LocalInference;

[TestFixture]
public class LlamaSharpToolGrammarTests
{
    [Test]
    public void Build_ReturnsNonEmptyString()
    {
        var grammar = LlamaSharpToolGrammar.Build();

        grammar.Should().NotBeNullOrWhiteSpace();
    }

    [Test]
    public void Build_ReturnsSameStringOnEveryCall()
    {
        var first = LlamaSharpToolGrammar.Build();
        var second = LlamaSharpToolGrammar.Build();

        first.Should().Be(second);
    }

    [Test]
    public void Build_ContainsRequiredRules()
    {
        var grammar = LlamaSharpToolGrammar.Build();

        grammar.Should().Contain("root");
        grammar.Should().Contain("mode-val");
        // GBNF string literals use backslash-quote escaping for embedded quotes
        grammar.Should().Contain("\\\"message\\\"");
        grammar.Should().Contain("\\\"tool_calls\\\"");
        grammar.Should().Contain("calls-arr");
        grammar.Should().Contain("call-obj");
    }
}

[TestFixture]
public class LocalInferenceEnvelopeParserTests
{
    // ── message mode ─────────────────────────────────────────────

    [Test]
    public void ParseEnvelope_MessageMode_ReturnsContent()
    {
        var json = """{"mode":"message","text":"Hello, world!","calls":[]}""";

        var result = LocalInferenceApiClient.ParseEnvelope(json);

        result.Content.Should().Be("Hello, world!");
        result.ToolCalls.Should().BeEmpty();
    }

    [Test]
    public void ParseEnvelope_MessageMode_EmptyText_ReturnsEmptyContent()
    {
        var json = """{"mode":"message","text":"","calls":[]}""";

        var result = LocalInferenceApiClient.ParseEnvelope(json);

        result.Content.Should().BeEmpty();
        result.ToolCalls.Should().BeEmpty();
    }

    // ── tool_calls mode ───────────────────────────────────────────

    [Test]
    public void ParseEnvelope_ToolCallsMode_SingleCall_ReturnsCall()
    {
        var json = """
            {
              "mode": "tool_calls",
              "text": "",
              "calls": [
                { "id": "call_abc", "name": "get_weather", "args": { "location": "London" } }
              ]
            }
            """;

        var result = LocalInferenceApiClient.ParseEnvelope(json);

        result.Content.Should().BeEmpty();
        result.ToolCalls.Should().HaveCount(1);
        result.ToolCalls[0].Id.Should().Be("call_abc");
        result.ToolCalls[0].Name.Should().Be("get_weather");
        result.ToolCalls[0].ArgumentsJson.Should().Contain("London");
    }

    [Test]
    public void ParseEnvelope_ToolCallsMode_MultipleCalls_ReturnsAllCalls()
    {
        var json = """
            {
              "mode": "tool_calls",
              "text": "",
              "calls": [
                { "id": "call_1", "name": "search",      "args": { "q": "cats" } },
                { "id": "call_2", "name": "open_browser", "args": { "url": "https://example.com" } }
              ]
            }
            """;

        var result = LocalInferenceApiClient.ParseEnvelope(json);

        result.ToolCalls.Should().HaveCount(2);
        result.ToolCalls[0].Name.Should().Be("search");
        result.ToolCalls[1].Name.Should().Be("open_browser");
    }

    [Test]
    public void ParseEnvelope_CallWithNoId_GeneratesId()
    {
        var json = """
            {
              "mode": "tool_calls",
              "text": "",
              "calls": [
                { "id": "", "name": "list_files", "args": {} }
              ]
            }
            """;

        var result = LocalInferenceApiClient.ParseEnvelope(json);

        result.ToolCalls.Should().HaveCount(1);
        result.ToolCalls[0].Id.Should().NotBeNullOrWhiteSpace();
        result.ToolCalls[0].Id.Should().StartWith("call_");
    }

    [Test]
    public void ParseEnvelope_CallWithEmptyArgs_ReturnsEmptyObject()
    {
        var json = """
            {
              "mode": "tool_calls",
              "text": "",
              "calls": [
                { "id": "call_x", "name": "ping", "args": {} }
              ]
            }
            """;

        var result = LocalInferenceApiClient.ParseEnvelope(json);

        result.ToolCalls.Should().HaveCount(1);
        result.ToolCalls[0].ArgumentsJson.Should().Be("{}");
    }

    // ── no-op name filtering ──────────────────────────────────────

    [TestCase("none")]
    [TestCase("null")]
    [TestCase("no_tool")]
    [TestCase("noop")]
    [TestCase("no-op")]
    [TestCase("n/a")]
    [TestCase("")]
    [TestCase("NONE")]
    [TestCase("No_Tool")]
    public void ParseEnvelope_NoOpToolName_ProducesZeroCalls(string noOpName)
    {
        var json = $$"""
            {
              "mode": "tool_calls",
              "text": "",
              "calls": [
                { "id": "call_1", "name": "{{noOpName}}", "args": {} }
              ]
            }
            """;

        var result = LocalInferenceApiClient.ParseEnvelope(json);

        result.ToolCalls.Should().BeEmpty();
    }

    // ── malformed input ───────────────────────────────────────────

    [Test]
    public void ParseEnvelope_MissingMode_TreatsAsMessage()
    {
        var json = """{"text":"fallback","calls":[]}""";

        var result = LocalInferenceApiClient.ParseEnvelope(json);

        result.Content.Should().Be("fallback");
        result.ToolCalls.Should().BeEmpty();
    }

    [Test]
    public void ParseEnvelope_EmptyString_ReturnsEmptyResult()
    {
        var result = LocalInferenceApiClient.ParseEnvelope(string.Empty);

        result.Content.Should().BeEmpty();
        result.ToolCalls.Should().BeEmpty();
    }

    [Test]
    public void ParseEnvelope_InvalidJson_ReturnsFallbackMessage()
    {
        var result = LocalInferenceApiClient.ParseEnvelope("not json at all");

        result.Content.Should().Contain("malformed output");
        result.ToolCalls.Should().BeEmpty();
    }

    [Test]
    public void ParseEnvelope_ArgsAsEscapedString_StillReturnsCall()
    {
        // Grammar prevents this but heavily-quantized models can defeat it.
        // The parser should survive gracefully — args will be the raw string token.
        var json = """
            {
              "mode": "tool_calls",
              "text": "",
              "calls": [
                { "id": "call_z", "name": "do_thing", "args": "{\"key\":\"value\"}" }
              ]
            }
            """;

        var result = LocalInferenceApiClient.ParseEnvelope(json);

        // Call is parsed; args will be whatever the raw token text is.
        result.ToolCalls.Should().HaveCount(1);
        result.ToolCalls[0].Name.Should().Be("do_thing");
    }

    [Test]
    public void ParseEnvelope_WrongTypeForText_ReturnsEmptyContent()
    {
        // text field is an integer instead of a string
        var json = """{"mode":"message","text":42,"calls":[]}""";

        // JsonElement.GetString() on a number returns null → falls back to empty
        var result = LocalInferenceApiClient.ParseEnvelope(json);

        result.Content.Should().BeEmpty();
    }
}
