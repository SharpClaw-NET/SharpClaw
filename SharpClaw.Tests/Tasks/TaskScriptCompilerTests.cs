using System.Text.Json;
using FluentAssertions;
using NUnit.Framework;
using SharpClaw.Application.Infrastructure.Tasks;
using SharpClaw.Application.Infrastructure.Tasks.Models;

namespace SharpClaw.Tests.Tasks;

/// <summary>
/// Tests for task script compilation: verifies that parameter values are coerced
/// to their declared types, defaults are applied when values are absent, required
/// parameters without values are rejected, and the compiled plan reflects the
/// correct step structure.
/// </summary>
[TestFixture]
public class TaskScriptCompilerTests
{
    private const string SimpleSource = """
[Task("report")]
[Description("Generate a report")]
public class ReportTask
{
    public string Title { get; set; } = "Default Title";
    public int MaxItems { get; set; } = 10;
    public bool Verbose { get; set; } = false;

    public async Task RunAsync(CancellationToken ct)
    {
        Log("running");
        return;
    }
}
""";

    // ─────────────────────────────────────────────────────────────
    // Plan metadata
    // ─────────────────────────────────────────────────────────────

    [Test]
    public void Compile_TaskName_IsPreservedInPlan()
    {
        var result = TaskScriptEngine.ProcessScript(SimpleSource);

        result.Success.Should().BeTrue();
        result.Plan!.TaskName.Should().Be("report");
    }

    [Test]
    public void Compile_Description_IsPreservedInPlan()
    {
        var result = TaskScriptEngine.ProcessScript(SimpleSource);

        result.Success.Should().BeTrue();
        result.Plan!.Description.Should().Be("Generate a report");
    }

    // ─────────────────────────────────────────────────────────────
    // Parameter defaults
    // ─────────────────────────────────────────────────────────────

    [Test]
    public void Compile_WithNoSuppliedValues_DefaultsAreApplied()
    {
        var result = TaskScriptEngine.ProcessScript(SimpleSource);

        result.Success.Should().BeTrue();
        result.Plan!.ParameterValues["Title"].Should().Be("Default Title");
        result.Plan.ParameterValues["MaxItems"].Should().Be(10);
        result.Plan.ParameterValues["Verbose"].Should().Be(false);
    }

    [Test]
    public void Compile_SuppliedStringValue_OverridesDefault()
    {
        var result = TaskScriptEngine.ProcessScript(SimpleSource, new Dictionary<string, object?>
        {
            ["Title"] = "My Custom Report"
        });

        result.Success.Should().BeTrue();
        result.Plan!.ParameterValues["Title"].Should().Be("My Custom Report");
    }

    // ─────────────────────────────────────────────────────────────
    // Type coercion
    // ─────────────────────────────────────────────────────────────

    [Test]
    public void Compile_IntFromStringLiteral_IsCoerced()
    {
        var result = TaskScriptEngine.ProcessScript(SimpleSource, new Dictionary<string, object?>
        {
            ["MaxItems"] = "42"
        });

        result.Success.Should().BeTrue();
        result.Plan!.ParameterValues["MaxItems"].Should().Be(42);
    }

    [Test]
    public void Compile_BoolFromStringLiteral_IsCoerced()
    {
        var result = TaskScriptEngine.ProcessScript(SimpleSource, new Dictionary<string, object?>
        {
            ["Verbose"] = "true"
        });

        result.Success.Should().BeTrue();
        result.Plan!.ParameterValues["Verbose"].Should().Be(true);
    }

    [Test]
    public void Compile_IntFromJsonElement_IsCoerced()
    {
        var result = TaskScriptEngine.ProcessScript(SimpleSource, new Dictionary<string, object?>
        {
            ["MaxItems"] = JsonDocument.Parse("7").RootElement
        });

        result.Success.Should().BeTrue();
        result.Plan!.ParameterValues["MaxItems"].Should().Be(7);
    }

    [Test]
    public void Compile_ListOfStringParameter_IsCoercedToList()
    {
        var source = """
[Task("list-task")]
public class ListTask
{
    public List<string> Tags { get; set; } = new();

    public async Task RunAsync(CancellationToken ct)
    {
        Log("done");
    }
}
""";

        var result = TaskScriptEngine.ProcessScript(source, new Dictionary<string, object?>
        {
            ["Tags"] = "[\"alpha\",\"beta\",\"gamma\"]"
        });

        result.Success.Should().BeTrue();
        var tags = result.Plan!.ParameterValues["Tags"].Should().BeAssignableTo<List<object?>>().Subject;
        tags.Should().HaveCount(3);
    }

    // ─────────────────────────────────────────────────────────────
    // Required parameters
    // ─────────────────────────────────────────────────────────────

    [Test]
    public void Compile_RequiredParameterNotProvided_FailsWithTASK201()
    {
        var source = """
[Task("required-param")]
public class RequiredParamTask
{
    public string ApiKey { get; set; }

    public async Task RunAsync(CancellationToken ct)
    {
        Log(ApiKey);
    }
}
""";

        var result = TaskScriptEngine.ProcessScript(source);

        result.Success.Should().BeFalse();
        result.Diagnostics.Should().Contain(d => d.Code == "TASK201");
    }

    // ─────────────────────────────────────────────────────────────
    // Step structure
    // ─────────────────────────────────────────────────────────────

    [Test]
    public void Compile_ReturnStepTerminatesStepList()
    {
        var result = TaskScriptEngine.ProcessScript(SimpleSource);

        result.Success.Should().BeTrue();
        result.Plan!.ExecutionSteps.Last().Kind.Should().Be(TaskStepKind.Return);
    }

    [Test]
    public void Compile_ConditionalBodySteps_ArePreserved()
    {
        var source = """
[Task("conditional")]
public class ConditionalTask
{
    public async Task RunAsync(CancellationToken ct)
    {
        if (true)
        {
            Log("in-then");
        }
        else
        {
            Log("in-else");
        }
    }
}
""";

        var result = TaskScriptEngine.ProcessScript(source);

        result.Success.Should().BeTrue();
        var conditional = result.Plan!.ExecutionSteps.Single(s => s.Kind == TaskStepKind.Conditional);
        conditional.Body.Should().ContainSingle(s => s.Kind == TaskStepKind.Log);
        conditional.ElseBody.Should().ContainSingle(s => s.Kind == TaskStepKind.Log);
    }

    [Test]
    public void Compile_ForEachLoop_LoopKindIsNormalized()
    {
        var source = """
[Task("loop")]
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
            ["Items"] = "[\"a\",\"b\"]"
        });

        result.Success.Should().BeTrue();
        var loop = result.Plan!.ExecutionSteps.Single(s => s.Kind == TaskStepKind.Loop);
        loop.LoopKind.Should().Be(TaskLoopKind.ForEach);
    }

    [Test]
    public void Compile_HttpGetStep_HttpMethodIsGet()
    {
        var source = """
[Task("http-get")]
public class HttpGetTask
{
    public async Task RunAsync(CancellationToken ct)
    {
        var resp = await HttpGet("https://api.example.com/data");
    }
}
""";

        var result = TaskScriptEngine.ProcessScript(source);

        result.Success.Should().BeTrue();
        var httpStep = result.Plan!.ExecutionSteps.Single(s => s.Kind == TaskStepKind.HttpRequest);
        httpStep.HttpMethod.Should().Be("GET");
    }
}
