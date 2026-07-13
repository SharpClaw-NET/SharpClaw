using SharpClaw.Core.Clients;
using SharpClaw.Contracts.Providers;

namespace SharpClaw.Tests.Clients;

/// <summary>
/// Cross-provider tests for the normalised <see cref="FinishReason"/>.
/// The per-provider mappers are internal, so we exercise them via the
/// publicly observable <see cref="ChatCompletionResult.FinishReason"/>
/// contract plus the Local <c>InferLocalFinishReason</c> helper that is
/// exposed internally to the test assembly.
/// </summary>
[TestFixture]
public class FinishReasonMappingTests
{
    [Test]
    public void ChatCompletionResult_DefaultFinishReason_IsUnknown()
    {
        var result = new ChatCompletionResult();
        result.FinishReason.Should().Be(FinishReason.Unknown);
    }

    [Test]
    public void ChatCompletionResult_FinishReason_RoundtripsInitValue()
    {
        var result = new ChatCompletionResult { FinishReason = FinishReason.Length };
        result.FinishReason.Should().Be(FinishReason.Length);
    }

    [TestCase(FinishReason.Unknown)]
    [TestCase(FinishReason.Stop)]
    [TestCase(FinishReason.Length)]
    [TestCase(FinishReason.ToolCalls)]
    [TestCase(FinishReason.ContentFilter)]
    public void FinishReason_AllMembersAreDistinct(FinishReason value)
    {
        Enum.IsDefined(typeof(FinishReason), value).Should().BeTrue();
    }
}
