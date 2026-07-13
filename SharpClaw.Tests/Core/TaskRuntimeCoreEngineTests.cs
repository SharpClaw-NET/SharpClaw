using System.Text.Json;
using SharpClaw.Contracts.DTOs.Tasks;
using SharpClaw.Contracts.Entities.Core;
using SharpClaw.Contracts.Entities.Core.Context;
using SharpClaw.Contracts.Entities.Core.Tasks;
using SharpClaw.Contracts.Enums;
using SharpClaw.Core.Tasks.Administration;
using SharpClaw.Core.Tasks.Models;
using SharpClaw.Core.Tasks.Runtime;

namespace SharpClaw.Tests.Core;

[TestFixture]
public sealed class TaskRuntimeCoreEngineTests
{
    [Test]
    public void ResolveExpression_UsesLongestVariableNamesBeforeConcatenation()
    {
        var engine = new TaskExpressionEngine();
        var variables = new Dictionary<string, object?>
        {
            ["name"] = "short",
            ["nameSuffix"] = "long"
        };

        var resolved = engine.ResolveExpression("\"hello \" + nameSuffix", variables);

        resolved.Should().Be("hello long");
    }

    [Test]
    public void EvaluateCondition_AppliesTaskRuntimeTruthinessAndComparisons()
    {
        var engine = new TaskExpressionEngine();
        var variables = new Dictionary<string, object?>
        {
            ["count"] = "7",
            ["state"] = "ready",
            ["missing"] = null
        };

        engine.EvaluateCondition("true", variables).Should().BeTrue();
        engine.EvaluateCondition("false", variables).Should().BeFalse();
        engine.EvaluateCondition("count >= 5", variables).Should().BeTrue();
        engine.EvaluateCondition("state == ready", variables).Should().BeTrue();
        engine.EvaluateCondition("missing == null", variables).Should().BeTrue();
        engine.EvaluateCondition("", variables).Should().BeFalse();
    }

    [Test]
    public void StructuredResponseParser_ExtractsAndValidatesDeclaredShape()
    {
        var parser = new TaskStructuredResponseParser();
        var dataType = new TaskDataTypeDefinition(
            "Result",
            [
                new TaskPropertyDefinition("name", "string"),
                new TaskPropertyDefinition("count", "int"),
                new TaskPropertyDefinition("items", "string", IsCollection: true)
            ]);

        var parsed = parser.Parse(
            "prefix {\"name\":\"alpha\",\"count\":3,\"items\":[\"a\"]} suffix",
            "Result",
            [dataType]);

        using var doc = JsonDocument.Parse(parsed);
        doc.RootElement.GetProperty("name").GetString().Should().Be("alpha");
        doc.RootElement.GetProperty("count").GetInt32().Should().Be(3);
        doc.RootElement.GetProperty("items").ValueKind.Should().Be(JsonValueKind.Array);
    }

    [Test]
    public void StructuredResponseParser_ThrowsForMissingDeclaredProperty()
    {
        var parser = new TaskStructuredResponseParser();
        var dataType = new TaskDataTypeDefinition(
            "Result",
            [new TaskPropertyDefinition("name", "string")]);

        var act = () => parser.Parse("{\"count\":3}", "Result", [dataType]);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("ParseResponse<Result> missing property 'name'.");
    }

    [Test]
    public void RuntimeLifecycleEngine_ProducesCanonicalTaskRuntimeEvents()
    {
        var engine = new TaskRuntimeLifecycleEngine();

        var started = engine.BuildStartedPlan();
        started.LogMessage.Should().Be("Task started.");
        started.OutputEvents.Single().Should().Be(
            new TaskRuntimeOutputEventPlan(TaskOutputEventType.StatusChange, "Running"));

        var failed = engine.BuildFailurePlan("boom");
        failed.LogLevel.Should().Be("Error");
        failed.LogMessage.Should().Be("Task failed: boom");
        failed.OutputEvents.Should().Equal(
            new TaskRuntimeOutputEventPlan(TaskOutputEventType.StatusChange, "Failed: boom"),
            new TaskRuntimeOutputEventPlan(TaskOutputEventType.Done, null));
    }

    [Test]
    public async Task RuntimeRegistry_RegistersStreamsPausesCancelsAndUnregistersEntry()
    {
        var now = new DateTimeOffset(2026, 7, 1, 8, 0, 0, TimeSpan.Zero);
        var registry = new TaskRuntimeRegistry(new FixedTimeProvider(now));
        var instanceId = Guid.NewGuid();

        var runtime = registry.Register(instanceId);
        var reader = registry.GetOutputReader(instanceId);

        registry.ActiveCount.Should().Be(1);
        registry.ActiveInstanceIds.Should().Contain(instanceId);
        registry.IsRunning(instanceId).Should().BeTrue();
        reader.Should().NotBeNull();

        await runtime.WriteEventAsync(TaskOutputEventType.Log, "hello");
        reader!.TryRead(out var evt).Should().BeTrue();
        evt.Should().Be(new TaskOutputEvent(
            TaskOutputEventType.Log,
            1,
            now,
            "hello"));

        registry.TryPause(instanceId).Should().BeTrue();
        var pauseWait = runtime.WaitIfPausedAsync(CancellationToken.None);
        pauseWait.IsCompleted.Should().BeFalse();

        registry.TryResume(instanceId).Should().BeTrue();
        await pauseWait.WaitAsync(TimeSpan.FromSeconds(1));

        (await registry.CancelAsync(instanceId)).Should().BeTrue();
        runtime.CancellationToken.IsCancellationRequested.Should().BeTrue();

        registry.Unregister(instanceId).Should().BeTrue();
        registry.ActiveCount.Should().Be(0);
        await reader.Completion.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Test]
    public async Task RuntimeRegistry_CancelAllCancelsEntriesActiveAtCallTime()
    {
        var registry = new TaskRuntimeRegistry();
        var first = registry.Register(Guid.NewGuid());
        var second = registry.Register(Guid.NewGuid());

        var cancelled = await registry.CancelAllAsync();

        cancelled.Should().Be(2);
        first.CancellationToken.IsCancellationRequested.Should().BeTrue();
        second.CancellationToken.IsCancellationRequested.Should().BeTrue();
        registry.ActiveCount.Should().Be(2);
    }

    [Test]
    public void ApplyRestartRecovery_MarksStaleInstanceFailedWithCanonicalMessage()
    {
        var now = new DateTimeOffset(2026, 6, 30, 12, 0, 0, TimeSpan.Zero);
        var engine = new TaskAdministrationEngine(new FixedTimeProvider(now));
        var instance = new TaskInstanceDB
        {
            Status = TaskInstanceStatus.Paused
        };

        var recovery = engine.ApplyRestartRecovery(instance);

        instance.Status.Should().Be(TaskInstanceStatus.Failed);
        instance.CompletedAt.Should().Be(now);
        instance.ErrorMessage.Should().Be(
            "Instance was Paused when the application restarted. Manual restart required.");
        recovery.PreviousStatus.Should().Be(TaskInstanceStatus.Paused);
        recovery.LogMessage.Should().Be("Recovery: instance was Paused at startup \u2014 marked Failed.");
    }

    [Test]
    public void HostBridgeProvisioningEngine_AppliesTaskScopedAgentChannelAndThreadRules()
    {
        var engine = new TaskHostBridgeProvisioningEngine();
        var now = new DateTimeOffset(2026, 6, 30, 12, 34, 0, TimeSpan.Zero);
        var modelId = Guid.NewGuid();
        var agentId = Guid.NewGuid();
        var channelId = Guid.NewGuid();
        var contextId = Guid.NewGuid();
        var threadId = Guid.NewGuid();

        var created = engine.ApplyAgentProvisioning(
            null,
            "Worker",
            modelId,
            "system",
            "worker.custom");

        created.Created.Should().BeTrue();
        created.Agent.Name.Should().Be("Worker");
        created.Agent.ModelId.Should().Be(modelId);
        created.Agent.SystemPrompt.Should().Be("system");
        created.Agent.CustomId.Should().Be("worker.custom");

        created.Agent.Id = agentId;
        var updated = engine.ApplyAgentProvisioning(
            created.Agent,
            "Worker 2",
            modelId,
            "new system",
            "ignored.custom");

        updated.Created.Should().BeFalse();
        updated.Agent.Name.Should().Be("Worker 2");
        updated.Agent.SystemPrompt.Should().Be("new system");
        updated.Agent.CustomId.Should().Be("worker.custom");

        var channel = new ChannelDB
        {
            Id = channelId,
            Title = "Old"
        };
        engine.ApplyExistingChannelProvisioning(
            channel,
            "Task Channel",
            agentId,
            "task.channel",
            contextId);

        channel.Title.Should().Be("Task Channel");
        channel.AgentId.Should().Be(agentId);
        channel.CustomId.Should().Be("task.channel");
        channel.AgentContextId.Should().Be(contextId);

        engine.AddChannelAllowedAgent(channel, created.Agent).Should().BeTrue();
        engine.AddChannelAllowedAgent(channel, created.Agent).Should().BeFalse();
        channel.AllowedAgents.Should().ContainSingle(a => a.Id == agentId);

        var thread = engine.CreateThread(channelId, null, now);
        thread.Name.Should().Be("Task Thread 12:34");
        thread.ChannelId.Should().Be(channelId);

        var instance = new TaskInstanceDB();
        engine.AdoptInstanceChannel(instance, channelId).Should().BeTrue();
        engine.AdoptInstanceChannel(instance, Guid.NewGuid()).Should().BeFalse();
        instance.ChannelId.Should().Be(channelId);

        TaskHostBridgeProvisioningEngine.BuildCreateAgentLog("Worker 2", agentId)
            .Should().Be($"CreateAgent 'Worker 2' \u2192 {agentId}");
        TaskHostBridgeProvisioningEngine.BuildCreateThreadLog(thread.Name, threadId)
            .Should().Be($"CreateThread 'Task Thread 12:34' \u2192 {threadId}");
        TaskHostBridgeProvisioningEngine.BuildCreateChannelLog(channel.Title, channelId)
            .Should().Be($"CreateChannel 'Task Channel' \u2192 {channelId}");
        TaskHostBridgeProvisioningEngine.BuildAddAllowedAgentLog(agentId, channelId)
            .Should().Be($"AddAllowedAgent agent={agentId} \u2192 channel={channelId}");
    }

    [Test]
    public void HostBridgeProvisioningEngine_ThrowsCanonicalMissingChannelMessage()
    {
        var engine = new TaskHostBridgeProvisioningEngine();
        var instanceId = Guid.NewGuid();

        var act = () => engine.RequireInstanceChannel(instanceId, null);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage(
                $"Task instance {instanceId} has no channel yet. " +
                "Call CreateChannel before using Chat, CreateThread, or other channel-dependent steps.");
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
