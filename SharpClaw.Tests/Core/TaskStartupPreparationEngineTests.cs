using System.Text.Json;
using SharpClaw.Contracts.Entities.Core.Tasks;
using SharpClaw.Contracts.Enums;
using SharpClaw.Core.Tasks.Runtime;

namespace SharpClaw.Tests.Core;

[TestFixture]
public sealed class TaskStartupPreparationEngineTests
{
    [Test]
    public void Prepare_WhenQueuedAndParameterJsonIsNull_ReturnsCompiledPlan()
    {
        var definition = Definition(LogOnlySource("startup-null-parameters"));
        var instance = Instance(TaskInstanceStatus.Queued);

        var result = new TaskStartupPreparationEngine().Prepare(
            instance,
            definition);

        result.Kind.Should().Be(TaskStartupPreparationKind.StartExecution);
        result.Plan.Should().NotBeNull();
        result.Plan!.TaskName.Should().Be("startup-null-parameters");
        result.Plan.ParameterValues.Should().ContainKey("Topic")
            .WhoseValue.Should().Be("default");
        instance.Status.Should().Be(TaskInstanceStatus.Queued);
    }

    [Test]
    public void Prepare_WhenQueuedAndParameterJsonExists_UsesCurrentJsonDeserializerBehavior()
    {
        var definition = Definition(LogOnlySource("startup-json-parameters"));
        var instance = Instance(
            TaskInstanceStatus.Queued,
            """{"Topic":"from-json"}""");

        var result = new TaskStartupPreparationEngine().Prepare(
            instance,
            definition);

        result.Kind.Should().Be(TaskStartupPreparationKind.StartExecution);
        result.Plan!.ParameterValues.Should().ContainKey("Topic");
        result.Plan.ParameterValues["Topic"].Should().Be("from-json");
    }

    [Test]
    public void Prepare_WhenParameterJsonIsInvalid_PropagatesJsonExceptionWithoutCompilationFailure()
    {
        var definition = Definition(LogOnlySource("startup-invalid-json"));
        var instance = Instance(TaskInstanceStatus.Queued, "{not-json");

        var act = () => new TaskStartupPreparationEngine().Prepare(
            instance,
            definition);

        act.Should().Throw<JsonException>();
        instance.Status.Should().Be(TaskInstanceStatus.Queued);
        instance.ErrorMessage.Should().BeNull();
        instance.CompletedAt.Should().BeNull();
    }

    [Test]
    public void Prepare_WhenInstanceIsNotQueued_ThrowsCanonicalStatusMessage()
    {
        var definition = Definition(LogOnlySource("startup-not-queued"));
        var instance = Instance(TaskInstanceStatus.Cancelled);

        var act = () => new TaskStartupPreparationEngine().Prepare(
            instance,
            definition);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"Task instance {instance.Id} is Cancelled, expected Queued.");
    }

    [Test]
    public void Prepare_WhenCompilationFails_ReturnsJoinedDiagnosticsWithoutMutatingInstance()
    {
        var definition = Definition(InvalidSource("startup-compile-failure"));
        var instance = Instance(TaskInstanceStatus.Queued);
        var engine = new TaskStartupPreparationEngine();

        var result = engine.Prepare(instance, definition);

        result.Kind.Should().Be(TaskStartupPreparationKind.CompilationFailed);
        result.Plan.Should().BeNull();
        result.CompilationErrors.Should().NotBeNullOrWhiteSpace();
        result.CompilationErrors.Should().NotContain(Environment.NewLine);
        result.DiagnosticCount.Should().BeGreaterThan(0);
        instance.Status.Should().Be(TaskInstanceStatus.Queued);
        instance.CompletedAt.Should().BeNull();
        instance.ErrorMessage.Should().BeNull();
    }

    private static TaskDefinitionDB Definition(string sourceText) => new()
    {
        Id = Guid.NewGuid(),
        Name = "definition",
        SourceText = sourceText
    };

    private static TaskInstanceDB Instance(
        TaskInstanceStatus status,
        string? parameterValuesJson = null) => new()
    {
        Id = Guid.NewGuid(),
        Status = status,
        ParameterValuesJson = parameterValuesJson
    };

    private static string LogOnlySource(string name) => $$"""
[Task("{{name}}")]
public class LogOnlyTask
{
    public string Topic { get; set; } = "default";

    public async Task RunAsync(CancellationToken ct)
    {
        Log(Topic);
    }
}
""";

    private static string InvalidSource(string name) => $$"""
[Task("{{name}}")]
public class InvalidTask
{
    public async Task NotTheEntryPoint(CancellationToken ct)
    {
    }
}
""";
}
