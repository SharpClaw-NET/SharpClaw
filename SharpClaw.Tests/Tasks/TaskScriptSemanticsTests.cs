using System.Text.Json;
using SharpClaw.Core.Tasks;
using SharpClaw.Core.Tasks.Models;
using SharpClaw.Contracts.Tasks;

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
        result.Plan!.ExecutionStatements.Should().ContainSingle();
        result.Plan.ExecutionStatements[0].StatementKey.Should().Be(TaskLanguageStatementKeys.Loop);
        result.Plan.ExecutionStatements[0].VariableName.Should().Be("item");
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
        result.Plan!.ExecutionStatements.Should().ContainSingle();
        result.Plan.ExecutionStatements[0].VariableName.Should().BeNull();
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

