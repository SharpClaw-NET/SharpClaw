using SharpClaw.Modules.Providers.LlamaSharp.LocalInference;

namespace SharpClaw.Tests.Providers.LlamaSharp;

[TestFixture]
public class LlamaSharpRegexToGrammarTests
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

[TestFixture]
public class LlamaSharpStringFormatGrammarsTests
{
    [TestCase("uuid")]
    [TestCase("email")]
    [TestCase("date")]
    [TestCase("date-time")]
    [TestCase("time")]
    [TestCase("ipv4")]
    [TestCase("uri")]
    [TestCase("hostname")]
    public void TryGet_SupportedFormats_ReturnFragment(string format)
    {
        var fragment = LlamaSharpStringFormatGrammars.TryGet(format);
        fragment.Should().NotBeNull();
        fragment!.TopBody.Should().NotBeNullOrWhiteSpace();
    }

    [TestCase("regex")]
    [TestCase("color")]
    [TestCase("unknown-format")]
    public void TryGet_UnsupportedFormats_ReturnNull(string format)
    {
        LlamaSharpStringFormatGrammars.TryGet(format).Should().BeNull();
    }
}
