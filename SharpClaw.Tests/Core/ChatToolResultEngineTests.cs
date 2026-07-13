using SharpClaw.Contracts.DTOs.AgentActions;
using SharpClaw.Contracts.DTOs.Chat;
using SharpClaw.Contracts.Enums;

namespace SharpClaw.Tests.Core;

[TestFixture]
public sealed class ChatToolResultEngineTests
{
    private readonly ChatToolResultEngine _engine = new();

    [Test]
    public void BuildFinalAssistantContent_WhenToolNotationExists_PrependsNotation()
    {
        var content = _engine.BuildFinalAssistantContent(
            "\ngear [tool] -> Completed",
            "done");

        content.Should().Be("\ngear [tool] -> Completed\ndone");
    }

    [Test]
    public void BuildFinalAssistantContent_WhenModelContentIsNullAfterTools_PreservesTrailingSeparator()
    {
        var content = _engine.BuildFinalAssistantContent(
            "\ngear [tool] -> Completed",
            null);

        content.Should().Be("\ngear [tool] -> Completed\n");
    }

    [Test]
    public void BuildMalformedEnvelopeAssistantContent_IncludesNotationAndTrimmedPreview()
    {
        var content = _engine.BuildMalformedEnvelopeAssistantContent(
            "\ngear [tool] -> Completed",
            "  {bad json}  ");

        content.Should().Be(
            "\ngear [tool] -> Completed\n"
            + "Error: the local model returned malformed tool-loop output after a tool call. "
            + "The model likely lost the required JSON envelope format for the follow-up response. "
            + "Preview: {bad json}");
    }

    [Test]
    public void ExtractScreenshotData_WhenMarkerPresent_SplitsTextAndPayload()
    {
        var (text, image) = _engine.ExtractScreenshotData(
            "Captured window  " + ChatToolResultEngine.ScreenshotMarker + "abc123");

        text.Should().Be("Captured window");
        image.Should().Be("abc123");
    }

    [Test]
    public void BuildToolResultMessage_WhenNoScreenshot_BuildsTextToolResult()
    {
        var message = _engine.BuildToolResultMessage(
            "call-1",
            Job(AgentJobStatus.Completed, resultData: "ok"),
            supportsVision: true);

        message.Role.Should().Be("tool");
        message.ToolCallId.Should().Be("call-1");
        message.Content.Should().Be("status=Completed result=ok");
        message.HasImage.Should().BeFalse();
    }

    [Test]
    public void BuildToolResultMessage_WhenJobFailed_IncludesErrorLog()
    {
        var message = _engine.BuildToolResultMessage(
            "call-1",
            Job(AgentJobStatus.Failed, resultData: null, errorLog: "boom"),
            supportsVision: false);

        message.Content.Should().Be("status=Failed error=boom");
    }

    [Test]
    public void BuildToolResultMessage_WhenScreenshotAndVisionSupported_AttachesImage()
    {
        var message = _engine.BuildToolResultMessage(
            "call-1",
            Job(
                AgentJobStatus.Completed,
                "saw it\n" + ChatToolResultEngine.ScreenshotMarker + "image-data"),
            supportsVision: true);

        message.Content.Should().Be("status=Completed result=saw it");
        message.ImageBase64.Should().Be("image-data");
        message.ImageMediaType.Should().Be("image/png");
    }

    [Test]
    public void BuildToolResultMessage_WhenScreenshotAndVisionUnsupported_AddsCapturedNote()
    {
        var message = _engine.BuildToolResultMessage(
            "call-1",
            Job(
                AgentJobStatus.Completed,
                "saw it\n" + ChatToolResultEngine.ScreenshotMarker + "image-data"),
            supportsVision: false);

        message.Content.Should().Be(
            "status=Completed result=saw it (screenshot captured successfully)");
        message.HasImage.Should().BeFalse();
    }

    [Test]
    public void ApplyRoundTokenUsageToJobResponses_SplitsRemainderIntoFirstRoundJob()
    {
        var first = Job(AgentJobStatus.Completed, "first");
        var second = Job(AgentJobStatus.Completed, "second");
        var jobs = new List<AgentJobResponse> { first, second };

        _engine.ApplyRoundTokenUsageToJobResponses(
            jobs,
            [first.Id, second.Id],
            promptTokens: 5,
            completionTokens: 3);

        jobs[0].JobCost.Should().Be(new TokenUsageResponse(3, 2, 5));
        jobs[1].JobCost.Should().Be(new TokenUsageResponse(2, 1, 3));
    }

    [Test]
    public void ApplyRoundTokenUsageToJobResponses_AccumulatesExistingSnapshotCost()
    {
        var job = Job(AgentJobStatus.Completed, "result") with
        {
            JobCost = new TokenUsageResponse(1, 2, 3)
        };
        var jobs = new List<AgentJobResponse> { job };

        _engine.ApplyRoundTokenUsageToJobResponses(
            jobs,
            [job.Id],
            promptTokens: 4,
            completionTokens: 5);

        jobs[0].JobCost.Should().Be(new TokenUsageResponse(5, 7, 12));
    }

    private static AgentJobResponse Job(
        AgentJobStatus status,
        string? resultData,
        string? errorLog = null)
    {
        return new AgentJobResponse(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            ActionKey: "tool",
            ResourceId: null,
            Status: status,
            EffectiveClearance: PermissionClearance.Independent,
            ResultData: resultData,
            ErrorLog: errorLog,
            Logs: [],
            CreatedAt: DateTimeOffset.UtcNow,
            StartedAt: null,
            CompletedAt: null);
    }
}
