using SharpClaw.Application.Core.LocalInference;

namespace SharpClaw.Tests.LocalInference;

[TestFixture]
public class GgufArchitectureClassifierTests
{
    [Test]
    public void Classify_NullArchitecture_AttemptsBoth()
    {
        var result = GgufArchitectureClassifier.Classify(null);

        result.LlamaSharp.Should().BeTrue();
        result.Whisper.Should().BeTrue();
    }

    [Test]
    public void Classify_WhisperArchitecture_WhisperOnlyNormalisation()
    {
        var result = GgufArchitectureClassifier.Classify("whisper");

        result.LlamaSharp.Should().BeFalse();
        result.Whisper.Should().BeTrue();
    }

    [TestCase("WHISPER")]
    [TestCase("Whisper")]
    public void Classify_WhisperArchitecture_CaseInsensitive(string architecture)
    {
        var result = GgufArchitectureClassifier.Classify(architecture);

        result.LlamaSharp.Should().BeFalse();
        result.Whisper.Should().BeTrue();
    }

    [TestCase("llama")]
    [TestCase("qwen2")]
    [TestCase("phi3")]
    [TestCase("gemma")]
    [TestCase("mistral")]
    public void Classify_StandardLlmArchitecture_LlamaSharpOnly(string architecture)
    {
        var result = GgufArchitectureClassifier.Classify(architecture);

        result.LlamaSharp.Should().BeTrue();
        result.Whisper.Should().BeFalse();
    }

    [TestCase("qwen2_audio")]
    [TestCase("qwen2_5_omni")]
    [TestCase("llama_omni")]
    public void Classify_MultimodalAudioArchitecture_AttemptsBoth(string architecture)
    {
        var result = GgufArchitectureClassifier.Classify(architecture);

        result.LlamaSharp.Should().BeTrue();
        result.Whisper.Should().BeTrue();
    }

    [Test]
    public void Classify_UnknownArchitecture_LlamaSharpOnly()
    {
        var result = GgufArchitectureClassifier.Classify("some_future_arch");

        result.LlamaSharp.Should().BeTrue();
        result.Whisper.Should().BeFalse();
    }
}
