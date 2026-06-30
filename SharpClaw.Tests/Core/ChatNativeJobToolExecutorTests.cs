using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.DTOs.AgentActions;
using SharpClaw.Contracts.DTOs.Chat;
using SharpClaw.Contracts.Enums;
using SharpClaw.Contracts.Modules;
using SharpClaw.Contracts.Providers;
using SharpClaw.Core.Chat;
using SharpClaw.Core.Modules;

namespace SharpClaw.Tests.Core;

[TestFixture]
public sealed class ChatNativeJobToolExecutorTests
{
    [Test]
    public async Task ExecuteAsync_WhenToolDoesNotResolve_ReturnsMalformedResultWithoutSubmitting()
    {
        var submitted = false;
        var executor = CreateExecutor();

        var result = await executor.ExecuteAsync(
            Request(
                new ChatToolCall("call-1", "missing", "{}"),
                submit: (_, _, _) =>
                {
                    submitted = true;
                    return Task.FromResult(Job(AgentJobStatus.Completed, "bad"));
                }));

        result.Parsed.Should().BeFalse();
        result.JobResponse.Should().BeNull();
        result.SubmittedJobId.Should().BeNull();
        result.ToolNotation.Should().BeEmpty();
        result.StreamEvents.Should().BeEmpty();
        result.ToolResultMessage.Should().Be(
            ToolAwareMessage.ToolResult(
                "call-1",
                ChatNativeToolCallParser.MalformedToolCallResult));
        submitted.Should().BeFalse();
    }

    [Test]
    public async Task ExecuteAsync_WhenJobCompletes_ReturnsToolStartNotationAndResultMessage()
    {
        var agentId = Guid.NewGuid();
        var channelId = Guid.NewGuid();
        var resourceId = Guid.NewGuid();
        SubmitAgentJobRequest? submittedRequest = null;
        var executor = CreateExecutor();

        var result = await executor.ExecuteAsync(
            Request(
                new ChatToolCall(
                    "call-1",
                    "run",
                    $$"""{"resourceId":"{{resourceId:D}}","value":5}"""),
                agentId: agentId,
                channelId: channelId,
                supportsVision: true,
                emitStreamEvents: true,
                submit: (targetChannelId, request, _) =>
                {
                    targetChannelId.Should().Be(channelId);
                    submittedRequest = request;
                    return Task.FromResult(
                        Job(
                            AgentJobStatus.Completed,
                            "ok",
                            actionKey: request.ActionKey,
                            resourceId: request.ResourceId));
                }));

        result.Parsed.Should().BeTrue();
        result.SubmittedJobId.Should().Be(result.JobResponse!.Id);
        result.JobResponse.Status.Should().Be(AgentJobStatus.Completed);
        result.ToolResultMessage.Content.Should().Be("status=Completed result=ok");
        result.ToolNotation.Should().Contain("[run]");
        result.ToolNotation.Should().Contain("Completed");
        result.AwaitingUnresolvableApproval.Should().BeFalse();
        result.StreamEvents.Should().ContainSingle();
        result.StreamEvents[0].Type.Should().Be(ChatStreamEventType.ToolCallStart);
        result.StreamEvents[0].Job!.Id.Should().Be(result.JobResponse.Id);

        submittedRequest.Should().NotBeNull();
        submittedRequest!.CallerAgentId.Should().Be(agentId);
        submittedRequest.ActionKey.Should().Be("run");
        submittedRequest.ResourceId.Should().Be(resourceId);
        submittedRequest.ScriptJson.Should().Contain("\"module\":\"test_module\"");
    }

    [Test]
    public async Task ExecuteAsync_WhenApprovalIsGranted_EmitsApprovalEventsAndUsesApprovedJob()
    {
        var submittedJob = Job(AgentJobStatus.AwaitingApproval, null);
        var approvedJob = submittedJob with
        {
            Status = AgentJobStatus.Completed,
            ResultData = "approved"
        };
        var callbackCalls = 0;
        var approveCalls = 0;
        var executor = CreateExecutor();

        var result = await executor.ExecuteAsync(
            Request(
                new ChatToolCall("call-1", "run", "{}"),
                emitStreamEvents: true,
                submit: (_, _, _) => Task.FromResult(submittedJob),
                canApprove: (agentId, resourceId, actionKey, _) =>
                {
                    agentId.Should().NotBeEmpty();
                    resourceId.Should().BeNull();
                    actionKey.Should().Be("run");
                    return Task.FromResult(true);
                },
                approvalCallback: (job, _) =>
                {
                    callbackCalls++;
                    job.Id.Should().Be(submittedJob.Id);
                    return Task.FromResult(true);
                },
                approve: (jobId, _) =>
                {
                    approveCalls++;
                    jobId.Should().Be(submittedJob.Id);
                    return Task.FromResult<AgentJobResponse?>(approvedJob);
                },
                cancel: (_, _) => throw new InvalidOperationException(
                    "Cancel should not be called.")));

        result.JobResponse.Should().Be(approvedJob);
        result.SubmittedJobId.Should().Be(submittedJob.Id);
        result.ToolResultMessage.Content.Should().Be(
            "status=Completed result=approved");
        result.ToolNotation.Should().Contain("Completed");
        result.AwaitingUnresolvableApproval.Should().BeFalse();
        callbackCalls.Should().Be(1);
        approveCalls.Should().Be(1);

        result.StreamEvents.Select(e => e.Type).Should().Equal(
            ChatStreamEventType.ApprovalRequired,
            ChatStreamEventType.ApprovalResult);
        result.StreamEvents[0].PendingJob!.Id.Should().Be(submittedJob.Id);
        result.StreamEvents[1].ApprovalOutcome!.Status.Should().Be(
            AgentJobStatus.Completed);
    }

    [Test]
    public async Task ExecuteAsync_WhenApprovalCannotBeSatisfied_CancelsWithoutCallback()
    {
        var submittedJob = Job(AgentJobStatus.AwaitingApproval, null);
        var cancelledJob = submittedJob with { Status = AgentJobStatus.Cancelled };
        var callbackCalls = 0;
        var cancelCalls = 0;
        var executor = CreateExecutor();

        var result = await executor.ExecuteAsync(
            Request(
                new ChatToolCall("call-1", "run", "{}"),
                emitStreamEvents: true,
                submit: (_, _, _) => Task.FromResult(submittedJob),
                canApprove: (_, _, _, _) => Task.FromResult(false),
                approvalCallback: (_, _) =>
                {
                    callbackCalls++;
                    return Task.FromResult(true);
                },
                cancel: (jobId, _) =>
                {
                    cancelCalls++;
                    jobId.Should().Be(submittedJob.Id);
                    return Task.FromResult<AgentJobResponse?>(cancelledJob);
                },
                approve: (_, _) => throw new InvalidOperationException(
                    "Approve should not be called.")));

        result.JobResponse.Should().Be(cancelledJob);
        result.ToolResultMessage.Content.Should().Be("status=Cancelled");
        result.ToolNotation.Should().Contain("Cancelled");
        result.AwaitingUnresolvableApproval.Should().BeFalse();
        callbackCalls.Should().Be(0);
        cancelCalls.Should().Be(1);
        result.StreamEvents.Select(e => e.Type).Should().Equal(
            ChatStreamEventType.ApprovalResult);
    }

    [Test]
    public async Task ExecuteAsync_WhenBufferedApprovalHasNoCallback_LeavesAwaitingApproval()
    {
        var submittedJob = Job(AgentJobStatus.AwaitingApproval, null);
        var executor = CreateExecutor();

        var result = await executor.ExecuteAsync(
            Request(
                new ChatToolCall("call-1", "run", "{}"),
                emitStreamEvents: false,
                submit: (_, _, _) => Task.FromResult(submittedJob),
                canApprove: (_, _, _, _) => throw new InvalidOperationException(
                    "Permission check should not run without a callback.")));

        result.JobResponse.Should().Be(submittedJob);
        result.SubmittedJobId.Should().Be(submittedJob.Id);
        result.ToolResultMessage.Content.Should().Be("status=AwaitingApproval");
        result.ToolNotation.Should().Contain("AwaitingApproval");
        result.AwaitingUnresolvableApproval.Should().BeTrue();
        result.StreamEvents.Should().BeEmpty();
    }

    private static ChatNativeJobToolExecutor CreateExecutor()
        => new(new ChatNativeToolCallParser(), new ChatToolResultEngine());

    private static ChatNativeJobToolExecutionRequest Request(
        ChatToolCall toolCall,
        Guid? agentId = null,
        Guid? channelId = null,
        bool supportsVision = false,
        bool emitStreamEvents = false,
        Func<Guid, SubmitAgentJobRequest, CancellationToken,
            Task<AgentJobResponse>>? submit = null,
        Func<Guid, Guid?, string?, CancellationToken, Task<bool>>?
            canApprove = null,
        Func<Guid, CancellationToken, Task<AgentJobResponse?>>? cancel = null,
        Func<AgentJobResponse, CancellationToken, Task<bool>>?
            approvalCallback = null,
        Func<Guid, CancellationToken, Task<AgentJobResponse?>>? approve = null)
        => new(
            new ChatNativeToolCallResolutionRequest(
                toolCall,
                CreateRegistry(),
                new ModuleToolExecutionPlanner()),
            agentId ?? Guid.NewGuid(),
            channelId ?? Guid.NewGuid(),
            supportsVision,
            emitStreamEvents,
            submit ?? throw new InvalidOperationException(
                "Submit delegate is required for parsed tool calls."),
            canApprove ?? ((_, _, _, _) => Task.FromResult(false)),
            cancel ?? ((_, _) => throw new InvalidOperationException(
                "Cancel delegate should not be called.")),
            approvalCallback,
            approve);

    private static AgentJobResponse Job(
        AgentJobStatus status,
        string? resultData,
        string? actionKey = "run",
        Guid? resourceId = null)
        => new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            actionKey,
            resourceId,
            status,
            PermissionClearance.Independent,
            resultData,
            ErrorLog: null,
            Logs: [],
            CreatedAt: DateTimeOffset.UtcNow,
            StartedAt: null,
            CompletedAt: null);

    private static ModuleRegistry CreateRegistry()
    {
        var registry = new ModuleRegistry();
        registry.Register(new TestModule());
        return registry;
    }

    private static JsonElement Json(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private sealed class TestModule : ISharpClawCoreModule
    {
        public string Id => "test_module";
        public string DisplayName => "Test Module";
        public string ToolPrefix => "test";

        public void ConfigureServices(IServiceCollection services)
        {
        }

        public IReadOnlyList<ModuleToolDefinition> GetToolDefinitions() =>
        [
            new(
                "run",
                "Run",
                Json("""{"type":"object"}"""),
                new ModuleToolPermission(
                    IsPerResource: true,
                    Check: (_, _, _, _) => Task.FromResult(
                        AgentActionResult.Approve(
                            "ok",
                            PermissionClearance.ApprovedByWhitelistedUser))))
        ];

        public Task<string> ExecuteToolAsync(
            string toolName,
            JsonElement parameters,
            AgentJobContext job,
            IServiceProvider scopedServices,
            CancellationToken ct) =>
            throw new NotSupportedException();
    }
}
