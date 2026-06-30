using System.Text.Json;
using SharpClaw.Contracts.DTOs.Tasks;
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

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
