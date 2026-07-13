using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Runtime.BLL.Services;
using SharpClaw.Contracts.DTOs.AgentActions;
using SharpClaw.Contracts.DTOs.Chat;
using SharpClaw.Contracts.Entities.Core;
using SharpClaw.Contracts.Entities.Core.Jobs;
using SharpClaw.Contracts.Enums;
using SharpClaw.Contracts.Providers;
using SharpClaw.Tests.TestHarness;

namespace SharpClaw.Tests.TestHarness;

[TestFixture]
public sealed class TestHarnessCostTrackingExpandedTests
{
    [Test]
    public async Task JobCostIsNonNullForCompletedJobAndStableAcrossGetCalls()
    {
        await using var host = ChatHarnessHost.Create();
        host.Harness.ConfigurePermissionedJobTool(new TestHarnessToolBehavior { Result = "job-ok" });
        var seeded = await host.SeedChatAsync(
            TestHarnessConstants.ToolProviderKey,
            grantHarnessPermission: true);
        ConfigureJobToolThenFinal(
            host,
            TestHarnessConstants.JobPermissionedTool,
            """{}""",
            new TokenUsage(11, 7),
            "done");

        var response = await host.Chat.SendMessageAsync(
            seeded.Channel.Id,
            new ChatRequest("job"));

        var job = response.JobResults!.Single();
        job.JobCost.Should().NotBeNull();
        job.JobCost!.TotalTokens.Should().Be(18);

        var service = host.Services.GetRequiredService<AgentJobService>();
        var firstGet = await service.GetAsync(job.Id);
        var secondGet = await service.GetAsync(job.Id);
        firstGet!.JobCost.Should().BeEquivalentTo(job.JobCost);
        secondGet!.JobCost.Should().BeEquivalentTo(job.JobCost);
    }

    [Test]
    public async Task FailedJobCostBehaviorIsExplicit()
    {
        await using var host = ChatHarnessHost.Create();
        host.Harness.ConfigurePermissionedJobTool(new TestHarnessToolBehavior { ThrowFailure = true });
        var seeded = await host.SeedChatAsync(
            TestHarnessConstants.ToolProviderKey,
            grantHarnessPermission: true);
        ConfigureJobToolThenFinal(
            host,
            TestHarnessConstants.JobPermissionedTool,
            """{}""",
            new TokenUsage(5, 6),
            "after failed job");

        var response = await host.Chat.SendMessageAsync(
            seeded.Channel.Id,
            new ChatRequest("failed job"));

        var job = response.JobResults!.Single();
        job.Status.Should().Be(AgentJobStatus.Failed);
        job.JobCost.Should().NotBeNull();
        job.JobCost!.TotalTokens.Should().Be(11);
    }

    [Test]
    public async Task StreamingCostReportedOnlyAtEndStillAttachesToJob()
    {
        await using var host = ChatHarnessHost.Create();
        host.Harness.ConfigurePermissionedJobTool(new TestHarnessToolBehavior { Result = "job-ok" });
        var seeded = await host.SeedChatAsync(
            TestHarnessConstants.ToolProviderKey,
            grantHarnessPermission: true);
        ConfigureJobToolThenFinal(
            host,
            TestHarnessConstants.JobPermissionedTool,
            """{}""",
            new TokenUsage(8, 9),
            "stream final");

        var events = new List<ChatStreamEvent>();
        await foreach (var evt in host.Chat.SendMessageStreamAsync(
            seeded.Channel.Id,
            new ChatRequest("streaming job"),
            (_, _) => Task.FromResult(true)))
        {
            events.Add(evt);
        }

        events.Single(e => e.Type == ChatStreamEventType.ToolCallStart)
            .Job!.JobCost.Should().BeNull();
        events.Single(e => e.Type == ChatStreamEventType.Done)
            .FinalResponse!.JobResults!.Single().JobCost!.TotalTokens.Should().Be(17);
    }

    [Test]
    public async Task MultipleAgentsInOneChannelDoNotCrossContaminateCosts()
    {
        await using var host = ChatHarnessHost.Create();
        var seeded = await host.SeedChatAsync(TestHarnessConstants.PlainProviderKey);
        var other = new AgentDB
        {
            Id = Guid.NewGuid(),
            Name = "Other Cost Agent",
            ModelId = seeded.Model.Id,
            Model = seeded.Model,
            RoleId = seeded.Role.Id,
            Role = seeded.Role,
            SystemPrompt = "other prompt",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        host.Db.Agents.Add(other);
        seeded.Channel.AllowedAgents.Add(other);
        await host.Db.SaveChangesAsync();

        host.Harness.ConfigureProvider(
            TestHarnessConstants.PlainProviderKey,
            new TestHarnessProviderScenario
            {
                Turns =
                [
                    new TestHarnessProviderTurn { Content = "first", Usage = new TokenUsage(2, 3) },
                    new TestHarnessProviderTurn { Content = "second", Usage = new TokenUsage(7, 11) }
                ]
            });

        var first = await host.Chat.SendMessageAsync(
            seeded.Channel.Id,
            new ChatRequest("first"));
        var second = await host.Chat.SendMessageAsync(
            seeded.Channel.Id,
            new ChatRequest("second", AgentId: other.Id));

        first.AgentCost!.AgentId.Should().Be(seeded.Agent.Id);
        first.AgentCost.TotalTokens.Should().Be(5);
        second.AgentCost!.AgentId.Should().Be(other.Id);
        second.AgentCost.TotalTokens.Should().Be(18);
        second.ChannelCost!.AgentBreakdown.Should().Contain(b =>
            b.AgentId == seeded.Agent.Id && b.TotalTokens == 5);
        second.ChannelCost.AgentBreakdown.Should().Contain(b =>
            b.AgentId == other.Id && b.TotalTokens == 18);
    }

    [Test]
    public async Task CustomModuleCostHookAddsUsageWithoutCoreKnowingModuleInternals()
    {
        await using var host = ChatHarnessHost.Create();
        var seeded = await host.SeedChatAsync(TestHarnessConstants.CostProviderKey);
        host.Harness.ConfigureCost(new TestHarnessCostBehavior
        {
            Result = new ProviderCostResult(
                12.34m,
                "usd",
                [new ProviderCostDailyBucket(DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddDays(1), 12.34m)])
        });

        var result = await host.Services.GetRequiredService<ProviderCostService>()
            .GetCostAsync(seeded.Provider.Id, startDate: DateTimeOffset.UnixEpoch, endDate: DateTimeOffset.UnixEpoch.AddDays(1));

        result!.TotalCost.Should().Be(12.34m);
        result.CostApiSupported.Should().BeTrue();
        host.Harness.CostCalls.Should().ContainSingle();
    }

    [Test]
    public async Task DirectJobSubmissionCompletesAndListSummariesStayLightweight()
    {
        await using var host = ChatHarnessHost.Create();
        host.Harness.ConfigurePermissionedJobTool(new TestHarnessToolBehavior { Result = "direct-ok" });
        var seeded = await host.SeedChatAsync(
            TestHarnessConstants.ToolProviderKey,
            grantHarnessPermission: true);
        var svc = host.Services.GetRequiredService<AgentJobService>();

        var job = await svc.SubmitAsync(
            seeded.Channel.Id,
            new SubmitAgentJobRequest(
                ActionKey: TestHarnessConstants.JobPermissionedTool,
                ScriptJson: """{"result":"direct-ok"}"""));

        job.Status.Should().Be(AgentJobStatus.Completed);
        job.ResultData.Should().Be("direct-ok");
        job.StartedAt.Should().NotBeNull();
        job.CompletedAt.Should().NotBeNull();
        job.Logs.Select(l => l.Message).Should().Contain(m => m.Contains("Job completed successfully"));
        host.Harness.ToolCalls.Should().ContainSingle()
            .Which.JobId.Should().Be(job.Id);

        host.PersistenceCounter.Reset();
        var summaries = await svc.ListSummariesAsync(seeded.Channel.Id);

        summaries.Should().ContainSingle()
            .Which.Status.Should().Be(AgentJobStatus.Completed);
        host.PersistenceCounter.QueryCalls.Should().Be(1);
    }

    [Test]
    public async Task DirectJobDetailAndLifecycleActionsReuseCachedLogs()
    {
        await using var host = ChatHarnessHost.Create();
        host.Harness.ConfigurePermissionedJobTool(new TestHarnessToolBehavior { Result = "started" });
        var seeded = await host.SeedChatAsync(
            TestHarnessConstants.ToolProviderKey,
            grantHarnessPermission: true);
        var svc = host.Services.GetRequiredService<AgentJobService>();

        var job = await svc.SubmitAsync(
            seeded.Channel.Id,
            new SubmitAgentJobRequest(
                ActionKey: TestHarnessConstants.JobPermissionedTool,
                ScriptJson: """{"result":"started","remainExecuting":true}"""));

        host.PersistenceCounter.Reset();
        var firstDetail = await svc.GetAsync(job.Id);

        firstDetail!.Logs.Should().NotBeEmpty();
        host.PersistenceCounter.QueryCalls.Should().Be(0);

        host.PersistenceCounter.Reset();
        var stopped = await svc.StopAsync(job.Id);

        stopped!.Status.Should().Be(AgentJobStatus.Completed);
        stopped.Logs.Select(l => l.Message).Should().Contain("Job stopped.");
        host.PersistenceCounter.QueryCalls.Should().Be(0);
    }

    [Test]
    public async Task DirectJobDeniedStopsBeforeModuleInvocation()
    {
        await using var host = ChatHarnessHost.Create();
        var seeded = await host.SeedChatAsync(TestHarnessConstants.ToolProviderKey);
        var svc = host.Services.GetRequiredService<AgentJobService>();

        var job = await svc.SubmitAsync(
            seeded.Channel.Id,
            new SubmitAgentJobRequest(
                ActionKey: TestHarnessConstants.JobPermissionedTool,
                ScriptJson: """{"result":"should-not-run"}"""));

        job.Status.Should().Be(AgentJobStatus.Denied);
        job.CompletedAt.Should().BeNull();
        job.ResultData.Should().BeNull();
        job.Logs.Should().Contain(l =>
            l.Level == JobLogLevels.Warning
            && l.Message.Contains("Denied", StringComparison.OrdinalIgnoreCase));
        host.Harness.ToolCalls.Should().BeEmpty();
    }

    [Test]
    public async Task DirectJobAwaitingApprovalExecutesAfterAuthorizedApproval()
    {
        await using var host = ChatHarnessHost.Create();
        host.Harness.ConfigurePermissionedJobTool(new TestHarnessToolBehavior { Result = "approved-ok" });
        var seeded = await host.SeedChatAsync(
            TestHarnessConstants.ToolProviderKey,
            grantHarnessPermission: true,
            clearance: PermissionClearance.ApprovedByWhitelistedUser);
        var session = host.Services.GetRequiredService<SessionService>();
        var userId = seeded.User!.Id;
        session.UserId = null;
        var svc = host.Services.GetRequiredService<AgentJobService>();

        var pending = await svc.SubmitAsync(
            seeded.Channel.Id,
            new SubmitAgentJobRequest(
                ActionKey: TestHarnessConstants.JobPermissionedTool,
                ScriptJson: """{"result":"approved-ok"}"""));

        pending.Status.Should().Be(AgentJobStatus.AwaitingApproval);
        host.Harness.ToolCalls.Should().BeEmpty();

        session.UserId = userId;
        var approved = await svc.ApproveAsync(
            pending.Id,
            new ApproveAgentJobRequest());

        approved!.Status.Should().Be(AgentJobStatus.Completed);
        approved.ResultData.Should().Be("approved-ok");
        approved.Logs.Select(l => l.Message).Should().Contain(m =>
            m.Contains("Awaiting approval", StringComparison.OrdinalIgnoreCase));
        approved.Logs.Select(l => l.Message).Should().Contain(m =>
            m.Contains("Approved by", StringComparison.OrdinalIgnoreCase));
        host.Harness.ToolCalls.Should().ContainSingle()
            .Which.JobId.Should().Be(pending.Id);
    }

    [Test]
    public async Task DirectJobApprovalDeniesWhenPermissionWasRevokedWhileAwaiting()
    {
        await using var host = ChatHarnessHost.Create();
        host.Harness.ConfigurePermissionedJobTool(new TestHarnessToolBehavior { Result = "should-not-run" });
        var seeded = await host.SeedChatAsync(
            TestHarnessConstants.ToolProviderKey,
            grantHarnessPermission: true,
            clearance: PermissionClearance.ApprovedByWhitelistedUser);
        var session = host.Services.GetRequiredService<SessionService>();
        session.UserId = null;
        var svc = host.Services.GetRequiredService<AgentJobService>();

        var pending = await svc.SubmitAsync(
            seeded.Channel.Id,
            new SubmitAgentJobRequest(
                ActionKey: TestHarnessConstants.JobPermissionedTool,
                ScriptJson: """{"result":"should-not-run"}"""));

        pending.Status.Should().Be(AgentJobStatus.AwaitingApproval);
        var flags = host.Db.GlobalFlags
            .Where(f => f.PermissionSetId == seeded.PermissionSet.Id)
            .ToList();
        host.Db.GlobalFlags.RemoveRange(flags);
        await host.Db.SaveChangesAsync();

        session.UserId = seeded.User!.Id;
        var denied = await svc.ApproveAsync(
            pending.Id,
            new ApproveAgentJobRequest());

        denied!.Status.Should().Be(AgentJobStatus.Denied);
        denied.CompletedAt.Should().NotBeNull();
        denied.Logs.Should().Contain(l =>
            l.Level == JobLogLevels.Warning
            && l.Message.Contains("permission revoked", StringComparison.OrdinalIgnoreCase));
        host.Harness.ToolCalls.Should().BeEmpty();
    }

    [Test]
    public async Task DirectJobFailureCapturesErrorAndFailedToolCall()
    {
        await using var host = ChatHarnessHost.Create();
        var seeded = await host.SeedChatAsync(
            TestHarnessConstants.ToolProviderKey,
            grantHarnessPermission: true);
        var svc = host.Services.GetRequiredService<AgentJobService>();

        var job = await svc.SubmitAsync(
            seeded.Channel.Id,
            new SubmitAgentJobRequest(
                ActionKey: TestHarnessConstants.JobPermissionedTool,
                ScriptJson: """{"fail":true}"""));

        job.Status.Should().Be(AgentJobStatus.Failed);
        job.CompletedAt.Should().NotBeNull();
        job.ErrorLog.Should().Contain("test harness tool failure");
        job.Logs.Should().Contain(l => l.Level == JobLogLevels.Error);
        host.Harness.ToolCalls.Should().ContainSingle()
            .Which.Failed.Should().BeTrue();
    }

    [Test]
    public async Task LongRunningDirectJobPauseResumeStopAndCompletedCancelAreStable()
    {
        await using var host = ChatHarnessHost.Create();
        var seeded = await host.SeedChatAsync(
            TestHarnessConstants.ToolProviderKey,
            grantHarnessPermission: true);
        var svc = host.Services.GetRequiredService<AgentJobService>();

        var started = await svc.SubmitAsync(
            seeded.Channel.Id,
            new SubmitAgentJobRequest(
                ActionKey: TestHarnessConstants.JobPermissionedTool,
                ScriptJson: """{"result":"started","remainExecuting":true}"""));

        started.Status.Should().Be(AgentJobStatus.Executing);
        started.CompletedAt.Should().BeNull();
        started.ResultData.Should().Be("started");

        var paused = await svc.PauseAsync(started.Id);
        paused!.Status.Should().Be(AgentJobStatus.Paused);

        var resumed = await svc.ResumeAsync(started.Id);
        resumed!.Status.Should().Be(AgentJobStatus.Executing);

        var stopped = await svc.StopAsync(started.Id);
        stopped!.Status.Should().Be(AgentJobStatus.Completed);
        stopped.CompletedAt.Should().NotBeNull();

        var cancelCompleted = await svc.CancelAsync(started.Id);
        cancelCompleted!.Status.Should().Be(AgentJobStatus.Completed);
        cancelCompleted.Logs.Should().Contain(l =>
            l.Message.Contains("Cancel rejected", StringComparison.OrdinalIgnoreCase));
    }

    [Test]
    public async Task DirectStreamingJobToolConcatenatesChunksIntoResult()
    {
        await using var host = ChatHarnessHost.Create();
        host.Harness.ConfigureStreamingJobTool(new TestHarnessToolBehavior { Result = "stream-direct" });
        var seeded = await host.SeedChatAsync(
            TestHarnessConstants.ToolProviderKey,
            grantHarnessPermission: true);
        var svc = host.Services.GetRequiredService<AgentJobService>();

        var job = await svc.SubmitAsync(
            seeded.Channel.Id,
            new SubmitAgentJobRequest(
                ActionKey: TestHarnessConstants.JobStreamingTool,
                ScriptJson: """{"result":"stream-direct"}"""));

        job.Status.Should().Be(AgentJobStatus.Completed);
        job.ResultData.Should().Be("stream-direct");
        host.Harness.ToolCalls.Should().ContainSingle()
            .Which.Kind.Should().Be("job-streaming");
    }

    private static void ConfigureJobToolThenFinal(
        ChatHarnessHost host,
        string toolName,
        string argumentsJson,
        TokenUsage toolRoundUsage,
        string finalContent)
    {
        host.Harness.ConfigureProvider(
            TestHarnessConstants.ToolProviderKey,
            new TestHarnessProviderScenario
            {
                Turns =
                [
                    new TestHarnessProviderTurn
                    {
                        ToolCalls = [new ChatToolCall("call-job", toolName, argumentsJson)],
                        Usage = toolRoundUsage
                    },
                    new TestHarnessProviderTurn
                    {
                        Content = finalContent,
                        Usage = new TokenUsage(1, 1)
                    }
                ]
            });
    }
}
