using SharpClaw.Modules.Providers.LlamaSharp.Clients;
using LlamaSharp.ToolCallEnvelopes;
using SharpClaw.Providers.Common;
using EnvelopeToolAwareMessage = LlamaSharp.ToolCallEnvelopes.ToolAwareMessage;

namespace SharpClaw.Tests.Providers.LlamaSharp;

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

    // ── Phase 2 / 3: tool-choice & parallel specialisation ────────

    [Test]
    public void Build_ToolChoiceNone_OmitsToolCallsAlternative()
    {
        var grammar = LlamaSharpToolGrammar.Build(ToolChoice.None);

        grammar.Should().Contain("\\\"message\\\"");
        grammar.Should().NotContain("\\\"tool_calls\\\"");
    }

    [Test]
    public void Build_ToolChoiceRequired_OmitsMessageAlternative()
    {
        var grammar = LlamaSharpToolGrammar.Build(ToolChoice.Required);

        grammar.Should().NotContain("\\\"message\\\"");
        grammar.Should().Contain("\\\"tool_calls\\\"");
    }

    [Test]
    public void Build_ToolChoiceNamed_PinsFunctionNameLiteral()
    {
        var grammar = LlamaSharpToolGrammar.Build(ToolChoice.ForFunction("get_weather"));

        grammar.Should().Contain("\\\"get_weather\\\"");
        // When named, the name terminal is no longer the generic "string" rule
        // inside call-obj — so the assignment "name ... : ws string ," should be gone.
        grammar.Should().NotContain("\\\"name\\\"  ws \":\" ws string");
    }

    [Test]
    public void Build_ParallelCallsDisabled_RestrictsToSingleCall()
    {
        var grammar = LlamaSharpToolGrammar.Build(ToolChoice.Auto, parallelCalls: false);

        // With parallel disabled, the grammar must not allow a comma-separated list.
        grammar.Should().NotContain("ws \",\" ws call-obj");
    }

    [Test]
    public void Build_ToolChoiceNamedWithInvalidName_Throws()
    {
        var act = () => LlamaSharpToolGrammar.Build(ToolChoice.ForFunction("bad name"));

        act.Should().Throw<ArgumentException>();
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
    public void ParseEnvelope_CallWithNoId_ThrowsEnvelopeException()
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

        var act = () => LocalInferenceApiClient.ParseEnvelope(json);

        act.Should().Throw<SharpClaw.Contracts.Providers.LocalInferenceEnvelopeException>()
           .WithInnerException<LlamaSharpToolEnvelopeException>();
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
    [TestCase("NONE")]
    [TestCase("No_Tool")]
    public void ParseEnvelope_PseudoToolName_ReturnsCallAsData(string pseudoToolName)
    {
        var json = $$"""
            {
              "mode": "tool_calls",
              "text": "",
              "calls": [
                { "id": "call_1", "name": "{{pseudoToolName}}", "args": {} }
              ]
            }
            """;

        var result = LocalInferenceApiClient.ParseEnvelope(json);

        result.ToolCalls.Should().ContainSingle();
        result.ToolCalls[0].Name.Should().Be(pseudoToolName);
    }

    [Test]
    public void ParseEnvelope_EmptyToolName_ThrowsEnvelopeException()
    {
        var json = """
            {
              "mode": "tool_calls",
              "text": "",
              "calls": [
                { "id": "call_1", "name": "", "args": {} }
              ]
            }
            """;

        var act = () => LocalInferenceApiClient.ParseEnvelope(json);

        act.Should().Throw<SharpClaw.Contracts.Providers.LocalInferenceEnvelopeException>()
           .WithInnerException<LlamaSharpToolEnvelopeException>();
    }

    // ── malformed input ───────────────────────────────────────────

    [Test]
    public void ParseEnvelope_MissingMode_ThrowsEnvelopeException()
    {
        var json = """{"text":"fallback","calls":[]}""";

        var act = () => LocalInferenceApiClient.ParseEnvelope(json);

        act.Should().Throw<SharpClaw.Contracts.Providers.LocalInferenceEnvelopeException>()
           .WithInnerException<LlamaSharpToolEnvelopeException>();
    }

    [Test]
    public void ParseEnvelope_EmptyString_ThrowsEnvelopeException()
    {
        var act = () => LocalInferenceApiClient.ParseEnvelope(string.Empty);

        act.Should().Throw<SharpClaw.Contracts.Providers.LocalInferenceEnvelopeException>()
           .WithInnerException<LlamaSharpToolEnvelopeException>();
    }

    [Test]
    public void ParseEnvelope_InvalidJson_ThrowsEnvelopeException()
    {
        // L-017: malformed envelopes are signalled with a typed exception so
        // ChatService can surface a clear error instead of a silent fallback
        // string that downstream code treats as a real assistant message.
        var act = () => LocalInferenceApiClient.ParseEnvelope("not json at all");

        act.Should().Throw<SharpClaw.Contracts.Providers.LocalInferenceEnvelopeException>()
           .WithInnerException<LlamaSharpToolEnvelopeException>();
    }

    [Test]
    public void ParseEnvelope_ArgsAsEscapedString_ThrowsEnvelopeException()
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

        var act = () => LocalInferenceApiClient.ParseEnvelope(json);

        act.Should().Throw<SharpClaw.Contracts.Providers.LocalInferenceEnvelopeException>()
           .WithInnerException<LlamaSharpToolEnvelopeException>();
    }

    [Test]
    public void ParseEnvelope_WrongTypeForText_ThrowsEnvelopeException()
    {
        // text field is an integer instead of a string
        var json = """{"mode":"message","text":42,"calls":[]}""";

        // JsonElement.GetString() on a number returns null → falls back to empty
        var act = () => LocalInferenceApiClient.ParseEnvelope(json);

        act.Should().Throw<SharpClaw.Contracts.Providers.LocalInferenceEnvelopeException>()
           .WithInnerException<LlamaSharpToolEnvelopeException>();
    }
}

[TestFixture]
public class EnvelopeStreamWalkerTests
{
    private static List<ToolCallDelta> FeedAll(string json)
    {
        var parser = new LlamaSharpToolEnvelopeStreamParser();
        var deltas = new List<ToolCallDelta>();
        foreach (var ch in json)
        {
            foreach (var chunk in parser.Feed(ch.ToString()))
            {
                if (chunk.ToolCallDelta is { } delta)
                    deltas.Add(delta);
            }
        }
        return deltas;
    }

    private static string ConcatArgs(IEnumerable<ToolCallDelta> deltas, int index) =>
        string.Concat(deltas
            .Where(d => d.Index == index && d.ArgumentsFragment is not null)
            .Select(d => d.ArgumentsFragment));

    [Test]
    public void Feed_SingleCall_EmitsIdNameAndCompleteArgs()
    {
        const string envelope =
            """{"mode":"tool_calls","text":"","calls":[{"id":"call_a","name":"get_weather","args":{"city":"Paris"}}]}""";

        var deltas = FeedAll(envelope);

        deltas.Should().Contain(d => d.Index == 0 && d.Id == "call_a");
        deltas.Should().Contain(d => d.Index == 0 && d.Name == "get_weather");
        ConcatArgs(deltas, 0).Should().Be("""{"city":"Paris"}""");
    }

    [Test]
    public void Feed_MultipleCalls_TracksIndexPerCall()
    {
        const string envelope =
            """{"mode":"tool_calls","text":"","calls":[""" +
            """{"id":"c1","name":"a","args":{"x":1}},""" +
            """{"id":"c2","name":"b","args":{"y":2}}""" +
            """]}""";

        var deltas = FeedAll(envelope);

        deltas.Should().Contain(d => d.Index == 0 && d.Id == "c1");
        deltas.Should().Contain(d => d.Index == 1 && d.Id == "c2");
        deltas.Should().Contain(d => d.Index == 0 && d.Name == "a");
        deltas.Should().Contain(d => d.Index == 1 && d.Name == "b");
        ConcatArgs(deltas, 0).Should().Be("""{"x":1}""");
        ConcatArgs(deltas, 1).Should().Be("""{"y":2}""");
    }

    [Test]
    public void Feed_EmptyArgs_EmitsEmptyObjectFragment()
    {
        const string envelope =
            """{"mode":"tool_calls","text":"","calls":[{"id":"c","name":"ping","args":{}}]}""";

        var deltas = FeedAll(envelope);

        ConcatArgs(deltas, 0).Should().Be("{}");
    }

    [Test]
    public void Feed_UnicodeEscapeInName_DecodesOnDelta()
    {
        const string envelope =
            """{"mode":"tool_calls","text":"","calls":[{"id":"c","name":"caf\u00e9","args":{}}]}""";

        var deltas = FeedAll(envelope);

        deltas.Should().ContainSingle(d => d.Name != null)
            .Which.Name.Should().Be("café");
    }

    [Test]
    public void Feed_NestedObjectInArgs_PreservesStructure()
    {
        const string envelope =
            """{"mode":"tool_calls","text":"","calls":[{"id":"c","name":"n","args":{"a":{"b":[1,2,3]}}}]}""";

        var deltas = FeedAll(envelope);

        ConcatArgs(deltas, 0).Should().Be("""{"a":{"b":[1,2,3]}}""");
    }

    [Test]
    public void Feed_StringWithBraceInArgs_DoesNotTerminateEarly()
    {
        const string envelope =
            """{"mode":"tool_calls","text":"","calls":[{"id":"c","name":"n","args":{"s":"a}b"}}]}""";

        var deltas = FeedAll(envelope);

        ConcatArgs(deltas, 0).Should().Be("""{"s":"a}b"}""");
    }

    [Test]
    public void Feed_KeysOutOfOrder_StillProducesAllFields()
    {
        const string envelope =
            """{"mode":"tool_calls","text":"","calls":[{"name":"n","args":{"k":true},"id":"c"}]}""";

        var deltas = FeedAll(envelope);

        deltas.Should().Contain(d => d.Id == "c");
        deltas.Should().Contain(d => d.Name == "n");
        ConcatArgs(deltas, 0).Should().Be("""{"k":true}""");
    }
}


[TestFixture]
public class LlamaSharpToolPromptBuilderImageTests
{
    private static readonly IReadOnlyList<ToolDefinition> NoTools = Array.Empty<ToolDefinition>();
    private static readonly IReadOnlyList<EnvelopeToolAwareMessage> NoMessages =
        Array.Empty<EnvelopeToolAwareMessage>();

    [Test]
    public void Build_NoImages_OmitsVisualContextSection()
    {
        var history = LlamaSharpToolPromptBuilder.Build("sys", NoMessages, NoTools, imageCount: 0);
        history.Messages[0].Content.Should().NotContain("Visual context");
    }

    [Test]
    public void Build_SingleImage_AppendsSingularNotice()
    {
        var history = LlamaSharpToolPromptBuilder.Build("sys", NoMessages, NoTools, imageCount: 1);
        history.Messages[0].Content.Should().Contain("Visual context");
        history.Messages[0].Content.Should().Contain("shown 1 image");
    }

    [Test]
    public void Build_MultipleImages_AppendsPluralNoticeWithCount()
    {
        var history = LlamaSharpToolPromptBuilder.Build("sys", NoMessages, NoTools, imageCount: 3);
        history.Messages[0].Content.Should().Contain("shown 3 images");
    }
}
