using LlamaSharp.ToolCallEnvelopes;

namespace SharpClaw.Tests.Providers.LlamaSharp;


[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812",
    Justification = "NUnit discovers and constructs this internal fixture through reflection; its tests are executed by the maintained test suite.")]
[TestFixture]
internal sealed class LlamaSharpStringFormatGrammarsTests
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
