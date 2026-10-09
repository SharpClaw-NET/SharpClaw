using LlamaSharp.ToolCallEnvelopes;

namespace SharpClaw.Tests.Providers.LlamaSharp;


[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812",
    Justification = "NUnit discovers and constructs this internal fixture through reflection; its tests are executed by the maintained test suite.")]
[TestFixture]
internal class LlamaSharpRegexToGrammarTests
{
    [Test]
    public void TryConvert_Literals_Succeeds()
    {
        LlamaSharpRegexToGrammar.TryConvert("abc", out var body).Should().BeTrue();
        body.Should().Contain("\"a\"");
    }

    [Test]
    public void TryConvert_CharClassWithRange_Succeeds()
    {
        LlamaSharpRegexToGrammar.TryConvert("[a-z0-9]+", out var body).Should().BeTrue();
        body.Should().Contain("a-z0-9");
    }

    [Test]
    public void TryConvert_BoundedQuantifier_Succeeds()
    {
        LlamaSharpRegexToGrammar.TryConvert("[0-9]{3,5}", out _).Should().BeTrue();
    }

    [Test]
    public void TryConvert_Alternation_Succeeds()
    {
        LlamaSharpRegexToGrammar.TryConvert("yes|no", out var body).Should().BeTrue();
        body.Should().Contain(" | ");
    }

    [Test]
    public void TryConvert_NonCapturingGroup_Succeeds()
    {
        LlamaSharpRegexToGrammar.TryConvert("(?:ab)+", out _).Should().BeTrue();
    }

    [Test]
    public void TryConvert_Anchors_Stripped_Succeeds()
    {
        LlamaSharpRegexToGrammar.TryConvert("^abc$", out _).Should().BeTrue();
    }

    [Test]
    public void TryConvert_UnsupportedShorthand_Fails()
    {
        LlamaSharpRegexToGrammar.TryConvert(@"\d+", out _).Should().BeFalse();
    }

    [Test]
    public void TryConvert_Lookahead_Fails()
    {
        LlamaSharpRegexToGrammar.TryConvert("(?=abc)", out _).Should().BeFalse();
    }

    [Test]
    public void TryConvert_EmptyPattern_Fails()
    {
        LlamaSharpRegexToGrammar.TryConvert("", out _).Should().BeFalse();
    }
}
