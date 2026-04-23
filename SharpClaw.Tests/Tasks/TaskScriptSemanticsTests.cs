using System.Text.Json;
using SharpClaw.Application.Infrastructure.Tasks;
using SharpClaw.Application.Infrastructure.Tasks.Models;

namespace SharpClaw.Tests.Tasks;

[TestFixture]
public class TaskScriptSemanticsTests
{
    [Test]
    public void ProcessScript_ForEachLoop_SetsForEachLoopKind()
    {
        var source = """
[Task("LoopTask")]
public class LoopTask
{
    public List<string> Items { get; set; } = new();

    public async Task RunAsync(CancellationToken ct)
    {
        foreach (var item in Items)
        {
            Log(item);
        }
    }
}
""";

        var result = TaskScriptEngine.ProcessScript(source, new Dictionary<string, object?>
        {
            ["Items"] = "[\"one\",\"two\"]"
        });

        result.Success.Should().BeTrue();
        result.Plan.Should().NotBeNull();
        result.Plan!.ExecutionSteps.Should().ContainSingle();
        result.Plan.ExecutionSteps[0].Kind.Should().Be(TaskStepKind.Loop);
        result.Plan.ExecutionSteps[0].LoopKind.Should().Be(TaskLoopKind.ForEach);
        result.Plan.ParameterValues["Items"].Should().BeAssignableTo<List<object?>>();
    }

    [Test]
    public void ProcessScript_WhileLoop_SetsWhileLoopKind()
    {
        var source = """
[Task("WhileTask")]
public class WhileTask
{
    public async Task RunAsync(CancellationToken ct)
    {
        while (true)
        {
            return;
        }
    }
}
""";

        var result = TaskScriptEngine.ProcessScript(source);

        result.Success.Should().BeTrue();
        result.Plan.Should().NotBeNull();
        result.Plan!.ExecutionSteps.Should().ContainSingle();
        result.Plan.ExecutionSteps[0].LoopKind.Should().Be(TaskLoopKind.While);
    }

    [Test]
    public void Validate_ParseResponseWithUnknownType_ReturnsDiagnostic()
    {
        var source = """
[Task("ParseTask")]
public class ParseTask
{
    public async Task RunAsync(CancellationToken ct)
    {
        var parsed = ParseResponse<MissingType>("{}");
        Log(parsed);
    }
}
""";

        var parseResult = TaskScriptEngine.Parse(source);
        parseResult.Success.Should().BeTrue();
        parseResult.Definition.Should().NotBeNull();

        var validation = TaskScriptEngine.Validate(parseResult.Definition!);

        validation.IsValid.Should().BeFalse();
        validation.Diagnostics.Should().Contain(d => d.Code == "TASK108");
    }

    [Test]
    public void Compile_ConvertsPrimitiveParameterValues()
    {
        var source = """
[Task("ParameterTask")]
public class ParameterTask
{
    public int Count { get; set; }
    public bool Enabled { get; set; }

    public async Task RunAsync(CancellationToken ct)
    {
        Log("done");
    }
}
""";

        var result = TaskScriptEngine.ProcessScript(source, new Dictionary<string, object?>
        {
            ["Count"] = JsonDocument.Parse("5").RootElement,
            ["Enabled"] = "true"
        });

        result.Success.Should().BeTrue();
        result.Plan.Should().NotBeNull();
        result.Plan!.ParameterValues["Count"].Should().Be(5);
        result.Plan.ParameterValues["Enabled"].Should().Be(true);
    }
}
