using System.Text.RegularExpressions;
using SharpClaw.Modules.Providers.LlamaSharp.Clients;
using SharpClaw.Modules.Providers.LlamaSharp.LocalInference;
using SharpClaw.Contracts.Providers;
using System.Text.Json;

namespace SharpClaw.Tests.Providers.LlamaSharp;

/// <summary>
/// Regression guard for bug #4 in
/// <c>docs/internal/local-inference-pipeline-debug-report.md</c>.
/// <para>
/// llama.cpp's GBNF parser does not reliably accept multi-line alternations
/// where the continuation line starts with whitespace followed by <c>|</c>.
/// Our emitter previously produced grammars like:
/// <code>
/// calls-arr ::= "[" ws "]"
///             | "[" ws call-obj ( ws "," ws call-obj )* ws "]"
/// </code>
/// which caused llama.cpp to fail with <c>parse: error parsing grammar:
/// expecting name at | "["</c> — and then segfault during sampling.
/// </para>
/// <para>
/// The fix is that every emitted rule is single-line. These tests assert
/// that invariant directly so a future refactor that "prettifies" the
/// emitter by reintroducing line-broken alternatives will fail in CI
/// instead of crashing the native sampler at runtime.
/// </para>
/// </summary>
[TestFixture]
public class LlamaSharpToolGrammarShapeTests
{
    /// <summary>
    /// Regex matching the specific failure pattern: any line whose first
    /// non-whitespace character is <c>|</c>. In valid single-line-rule
    /// GBNF output, <c>|</c> only ever appears inside a rule body (never
    /// at the start of a physical line). Blank lines are ignored.
    /// </summary>
    private static readonly Regex BrokenContinuationLine =
        new(@"^\s+\|", RegexOptions.Multiline | RegexOptions.Compiled);

    // ─── Non-strict grammar (BuildGrammar path) ─────────────────────────

    [Test]
    public void BuildGrammar_NoPinned_NoMultiLineAlternation()
    {
        var gbnf = LlamaSharpToolGrammar.Build(
            ToolChoice.Auto, parallelCalls: true, allowRefusal: true);

        AssertNoBrokenContinuation(gbnf);
    }

    [Test]
    public void BuildGrammar_Required_NoMultiLineAlternation()
    {
        var gbnf = LlamaSharpToolGrammar.Build(
            ToolChoice.Required, parallelCalls: true, allowRefusal: false);

        AssertNoBrokenContinuation(gbnf);
    }

    [Test]
    public void BuildGrammar_None_NoMultiLineAlternation()
    {
        var gbnf = LlamaSharpToolGrammar.Build(
            ToolChoice.None, parallelCalls: false, allowRefusal: false);

        AssertNoBrokenContinuation(gbnf);
    }

    [TestCase(true, true)]
    [TestCase(true, false)]
    [TestCase(false, true)]
    [TestCase(false, false)]
    public void BuildGrammar_AllCombinations_NoMultiLineAlternation(
        bool parallelCalls, bool allowRefusal)
    {
        var gbnf = LlamaSharpToolGrammar.Build(
            ToolChoice.Auto, parallelCalls, allowRefusal);

        AssertNoBrokenContinuation(gbnf);
    }

    // ─── Strict grammar (per-tool arg schemas) ──────────────────────────

    [Test]
    public void BuildStrictGrammar_SingleTool_NoMultiLineAlternation()
    {
        var tools = new[] { SimpleTool("echo", """{"type":"object","properties":{"text":{"type":"string"}},"required":["text"]}""") };

        var gbnf = LlamaSharpToolGrammar.Build(
            ToolChoice.Auto, parallelCalls: true, tools, strict: true, allowRefusal: true);

        AssertNoBrokenContinuation(gbnf);
    }

    [Test]
    public void BuildStrictGrammar_MultipleTools_NoMultiLineAlternation()
    {
        // Representative of the real workload: multiple tools with mixed
        // argument shapes (nested objects, arrays, primitives). This is
        // the shape that produced the original bug #4 failure with 80+
        // tools loaded.
        var tools = new[]
        {
            SimpleTool("echo",   """{"type":"object","properties":{"text":{"type":"string"}},"required":["text"]}"""),
            SimpleTool("add",    """{"type":"object","properties":{"a":{"type":"number"},"b":{"type":"number"}},"required":["a","b"]}"""),
            SimpleTool("search", """{"type":"object","properties":{"query":{"type":"string"},"count":{"type":"integer"}},"required":["query"]}"""),
            SimpleTool("nested", """{"type":"object","properties":{"filter":{"type":"object","properties":{"kind":{"type":"string"}}}}}"""),
            SimpleTool("listy",  """{"type":"object","properties":{"items":{"type":"array","items":{"type":"string"}}}}"""),
        };

        var gbnf = LlamaSharpToolGrammar.Build(
            ToolChoice.Auto, parallelCalls: true, tools, strict: true, allowRefusal: true);

        AssertNoBrokenContinuation(gbnf);
    }

    [Test]
    public void BuildStrictGrammar_NamedTool_NoMultiLineAlternation()
    {
        var tools = new[]
        {
            SimpleTool("echo",  """{"type":"object","properties":{"text":{"type":"string"}},"required":["text"]}"""),
            SimpleTool("other", """{"type":"object","properties":{"x":{"type":"integer"}}}"""),
        };

        var gbnf = LlamaSharpToolGrammar.Build(
            ToolChoice.ForFunction("echo"), parallelCalls: false, tools, strict: true, allowRefusal: false);

        AssertNoBrokenContinuation(gbnf);
    }

    // ─── Every non-blank line starts with a rule LHS, a comment, or is
    //     part of the body of the preceding rule (no leading `|`). ──────

    [Test]
    public void BuildGrammar_EveryLineShapeIsParserFriendly()
    {
        var gbnf = LlamaSharpToolGrammar.Build(
            ToolChoice.Auto, parallelCalls: true, allowRefusal: true);

        foreach (var (line, lineNumber) in NumberedLines(gbnf))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            var trimmed = line.TrimStart();
            trimmed.Should().NotStartWith("|",
                $"line {lineNumber} ('{line}') starts with '|', which llama.cpp's GBNF parser " +
                "interprets as the start of a new rule LHS (and fails with 'expecting name at |'). " +
                "Keep all alternation branches on a single physical line. See bug #4.");
        }
    }

    [Test]
    public void BuildStrictGrammar_MultipleTools_EveryLineShapeIsParserFriendly()
    {
        var tools = new[]
        {
            SimpleTool("echo",   """{"type":"object","properties":{"text":{"type":"string"}},"required":["text"]}"""),
            SimpleTool("add",    """{"type":"object","properties":{"a":{"type":"number"},"b":{"type":"number"}},"required":["a","b"]}"""),
            SimpleTool("search", """{"type":"object","properties":{"query":{"type":"string"}}}"""),
        };

        var gbnf = LlamaSharpToolGrammar.Build(
            ToolChoice.Auto, parallelCalls: true, tools, strict: true, allowRefusal: true);

        foreach (var (line, lineNumber) in NumberedLines(gbnf))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            var trimmed = line.TrimStart();
            trimmed.Should().NotStartWith("|",
                $"line {lineNumber} ('{line}') starts with '|'. See bug #4.");
        }
    }

    // ─── Helpers ─────────────────────────────────────────────────────────

    /// <summary>
    /// Asserts that no physical line in the emitted grammar starts (after
    /// leading whitespace) with a <c>|</c>. This is the exact syntactic
    /// shape that caused llama.cpp to fail in bug #4.
    /// </summary>
    private static void AssertNoBrokenContinuation(string gbnf)
    {
        var match = BrokenContinuationLine.Match(gbnf);
        match.Success.Should().BeFalse(
            "grammar must not contain any line starting with '|' after whitespace. " +
            "First offending match: '{0}'. See bug #4 in the debug report — llama.cpp's GBNF " +
            "parser rejects this shape with 'expecting name at |' and the native sampler " +
            "then segfaults.",
            match.Success ? match.Value : "<none>");
    }

    private static IEnumerable<(string Line, int LineNumber)> NumberedLines(string text)
    {
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
            yield return (lines[i].TrimEnd('\r'), i + 1);
    }

    private static ChatToolDefinition SimpleTool(string name, string parametersJson)
    {
        using var doc = JsonDocument.Parse(parametersJson);
        return new ChatToolDefinition(
            Name: name,
            Description: $"Test tool {name}",
            ParametersSchema: doc.RootElement.Clone());
    }
}
