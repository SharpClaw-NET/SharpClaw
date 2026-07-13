using FluentAssertions;
using SharpClaw.Contracts.DTOs.AgentActions;
using SharpClaw.Contracts.Entities.Core.Jobs;
using SharpClaw.Contracts.Enums;
using SharpClaw.Contracts.Modules;
using SharpClaw.Core.Jobs;

namespace SharpClaw.Tests.Core;

[TestFixture]
public sealed class AgentJobLifecycleEngineTests
{
    private readonly AgentJobLifecycleEngine _engine = new();

    [Test]
    public void ResolveSubmissionPermission_WhenPendingIsPreauthorized_ExecutesWithoutAwaitingApproval()
    {
        var result = AgentActionResult.Pending(
            "Needs approval.",
            PermissionClearance.ApprovedByWhitelistedUser);

        var decision = _engine.ResolveSubmissionPermission(
            result,
            channelPreauthorized: true);

        decision.ShouldExecute.Should().BeTrue();
        decision.Status.Should().BeNull();
        decision.Logs.Should().ContainSingle(log =>
            log.Message == "Pre-authorized by channel/context permission set."
            && log.Level == JobLogLevels.Info);
    }

    [Test]
    public void ResolveApproval_WhenPermissionWasRevoked_DeniesAndCompletesAtNow()
    {
        var now = DateTimeOffset.Parse("2026-06-28T17:20:00Z");
        var approverId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var result = AgentActionResult.Denied("Agent does not have permission.");

        var decision = _engine.ResolveApproval(
            result,
            new ActionCaller(UserId: approverId),
            now);

        decision.Status.Should().Be(AgentJobStatus.Denied);
        decision.UpdateCompletedAt.Should().BeTrue();
        decision.CompletedAt.Should().Be(now);
        decision.ShouldExecute.Should().BeFalse();
        decision.Logs.Should().ContainSingle(log =>
            log.Level == JobLogLevels.Warning
            && log.Message.Contains($"user {approverId}")
            && log.Message.Contains("permission revoked"));
    }

    [Test]
    public void Cancel_WhenAlreadyTerminal_OnlyEmitsWarning()
    {
        var decision = _engine.Cancel(
            AgentJobStatus.Completed,
            DateTimeOffset.UtcNow);

        decision.Status.Should().BeNull();
        decision.UpdateCompletedAt.Should().BeFalse();
        decision.Logs.Should().ContainSingle(log =>
            log.Level == JobLogLevels.Warning
            && log.Message == "Cancel rejected: job is already Completed.");
    }

    [Test]
    public void Stop_WhenActionPrefixDoesNotMatch_OnlyEmitsWarning()
    {
        var decision = _engine.Stop(
            AgentJobStatus.Executing,
            "curativa.transcribe.start",
            "curativa.audio.",
            DateTimeOffset.UtcNow);

        decision.Status.Should().BeNull();
        decision.UpdateCompletedAt.Should().BeFalse();
        decision.Logs.Should().ContainSingle(log =>
            log.Level == JobLogLevels.Warning
            && log.Message == "Stop rejected: job action does not match the requested action prefix.");
    }

    [Test]
    public void CompleteExecution_WhenModuleRemainsExecuting_ClearsCompletedAtAndStoresResult()
    {
        var decision = _engine.CompleteExecution(
            "started",
            ModuleJobCompletionBehavior.RemainExecuting,
            DateTimeOffset.UtcNow);

        decision.Status.Should().Be(AgentJobStatus.Executing);
        decision.UpdateCompletedAt.Should().BeTrue();
        decision.CompletedAt.Should().BeNull();
        decision.UpdateResultData.Should().BeTrue();
        decision.ResultData.Should().Be("started");
        decision.Logs.Should().ContainSingle(log =>
            log.Level == JobLogLevels.Info
            && log.Message.Contains("job remains Executing"));
    }

    [Test]
    public void PauseThenResume_ProducesExpectedStatusTransitions()
    {
        var pause = _engine.Pause(AgentJobStatus.Executing);
        var resume = _engine.Resume(AgentJobStatus.Paused);

        pause.Status.Should().Be(AgentJobStatus.Paused);
        pause.Logs.Should().ContainSingle(log => log.Message == "Job paused.");
        resume.Status.Should().Be(AgentJobStatus.Executing);
        resume.Logs.Should().ContainSingle(log => log.Message == "Job resumed.");
    }

    [Test]
    public void FailModuleCallback_SetsFailedCompletedAtErrorLogAndErrorMessage()
    {
        var now = DateTimeOffset.Parse("2026-07-02T17:00:00Z");

        var decision = _engine.FailModuleCallback(
            AgentJobStatus.Executing,
            "late failure",
            "exception details",
            now);

        decision.HasChanges.Should().BeTrue();
        decision.Status.Should().Be(AgentJobStatus.Failed);
        decision.UpdateCompletedAt.Should().BeTrue();
        decision.CompletedAt.Should().Be(now);
        decision.UpdateErrorLog.Should().BeTrue();
        decision.ErrorLog.Should().Be("exception details");
        decision.Logs.Should().ContainSingle(log =>
            log.Level == JobLogLevels.Error
            && log.Message == "Job failed: late failure");
    }

    [Test]
    public void CompleteModuleCallback_WithNullResult_UsesDefaultMessageAndPreservesResultData()
    {
        var now = DateTimeOffset.Parse("2026-07-02T17:01:00Z");

        var decision = _engine.CompleteModuleCallback(
            AgentJobStatus.Executing,
            resultData: null,
            message: null,
            now);

        decision.HasChanges.Should().BeTrue();
        decision.Status.Should().Be(AgentJobStatus.Completed);
        decision.UpdateCompletedAt.Should().BeTrue();
        decision.CompletedAt.Should().Be(now);
        decision.UpdateResultData.Should().BeFalse();
        decision.Logs.Should().ContainSingle(log =>
            log.Level == JobLogLevels.Info
            && log.Message == "Job completed by module.");
    }

    [Test]
    public void CompleteModuleCallback_WithCustomMessageAndResult_UpdatesResultData()
    {
        var decision = _engine.CompleteModuleCallback(
            AgentJobStatus.Executing,
            resultData: "result",
            message: "custom completion",
            DateTimeOffset.Parse("2026-07-02T17:02:00Z"));

        decision.UpdateResultData.Should().BeTrue();
        decision.ResultData.Should().Be("result");
        decision.Logs.Should().ContainSingle(log =>
            log.Level == JobLogLevels.Info
            && log.Message == "custom completion");
    }

    [Test]
    public void CancelModuleCallback_UsesDefaultOrCustomWarningMessage()
    {
        var defaultDecision = _engine.CancelModuleCallback(
            AgentJobStatus.Executing,
            message: null,
            DateTimeOffset.Parse("2026-07-02T17:03:00Z"));
        var customDecision = _engine.CancelModuleCallback(
            AgentJobStatus.Executing,
            "custom cancellation",
            DateTimeOffset.Parse("2026-07-02T17:04:00Z"));

        defaultDecision.Status.Should().Be(AgentJobStatus.Cancelled);
        defaultDecision.UpdateCompletedAt.Should().BeTrue();
        defaultDecision.Logs.Should().ContainSingle(log =>
            log.Level == JobLogLevels.Warning
            && log.Message == "Job cancelled by module.");
        customDecision.Logs.Should().ContainSingle(log =>
            log.Level == JobLogLevels.Warning
            && log.Message == "custom cancellation");
    }

    [Test]
    public void ModuleCallbacks_WhenAlreadyTerminal_ReturnNoChangeDecision()
    {
        var now = DateTimeOffset.Parse("2026-07-02T17:05:00Z");

        var failed = _engine.FailModuleCallback(
            AgentJobStatus.Completed,
            "boom",
            "details",
            now);
        var completed = _engine.CompleteModuleCallback(
            AgentJobStatus.Cancelled,
            "result",
            "done",
            now);
        var cancelled = _engine.CancelModuleCallback(
            AgentJobStatus.Failed,
            "cancel",
            now);

        failed.HasChanges.Should().BeFalse();
        completed.HasChanges.Should().BeFalse();
        cancelled.HasChanges.Should().BeFalse();
    }

    [Test]
    public void CancelStaleFromPreviousSession_OnlyCancelsQueuedOrExecutingJobs()
    {
        var now = DateTimeOffset.Parse("2026-07-02T17:06:00Z");

        var queued = _engine.CancelStaleFromPreviousSession(
            AgentJobStatus.Queued,
            now);
        var executing = _engine.CancelStaleFromPreviousSession(
            AgentJobStatus.Executing,
            now);
        var paused = _engine.CancelStaleFromPreviousSession(
            AgentJobStatus.Paused,
            now);

        queued.Status.Should().Be(AgentJobStatus.Cancelled);
        executing.Status.Should().Be(AgentJobStatus.Cancelled);
        queued.Logs.Should().ContainSingle(log =>
            log.Level == JobLogLevels.Warning
            && log.Message == "Cancelled: stale from previous session.");
        paused.HasChanges.Should().BeFalse();
    }

    [Test]
    public void ModuleCallbackActionPrefixValidation_PreservesHistoricalExceptionMessage()
    {
        var jobs = new AgentJobAdministrationEngine();

        var act = () => jobs.EnsureModuleCallbackActionPrefix(" ");

        act.Should().Throw<ArgumentException>()
            .WithMessage("Action key prefix is required. (Parameter 'actionKeyPrefix')");
    }

    [Test]
    public void JobMatchesActionPrefix_HandlesNullActionsAndCaseInsensitiveMatches()
    {
        var jobs = new AgentJobAdministrationEngine();
        var nullAction = new AgentJobDB { ActionKey = null };
        var match = new AgentJobDB { ActionKey = "Curativa.Audio.Start" };
        var miss = new AgentJobDB { ActionKey = "Curativa.Video.Start" };

        jobs.JobMatchesActionPrefix(nullAction, "curativa.audio.").Should().BeFalse();
        jobs.JobMatchesActionPrefix(match, "curativa.audio.").Should().BeTrue();
        jobs.JobMatchesActionPrefix(miss, "curativa.audio.").Should().BeFalse();
    }
}
