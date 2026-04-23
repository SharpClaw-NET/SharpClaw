using FluentAssertions;
using NUnit.Framework;
using SharpClaw.Application.Infrastructure.Tasks;
using SharpClaw.Application.Infrastructure.Tasks.Models;

namespace SharpClaw.Tests.Tasks;

/// <summary>
/// Tests for the task script parser: verifies that well-formed scripts are parsed
/// into the expected structural representation and that invalid scripts produce
/// precise diagnostic codes.
/// </summary>
[TestFixture]
public class TaskScriptParserTests
{
    // ─────────────────────────────────────────────────────────────
    // Task metadata
    // ─────────────────────────────────────────────────────────────

    [Test]
    public void Parse_TaskName_IsExtractedFromAttribute()
    {
        var source = """
[Task("send-report")]
public class SendReportTask
{
    public async Task RunAsync(CancellationToken ct)
    {
        Log("done");
    }
}
""";

        var result = TaskScriptEngine.Parse(source);

        result.Success.Should().BeTrue();
        result.Definition!.Name.Should().Be("send-report");
    }

    [Test]
    public void Parse_Description_IsExtractedFromAttribute()
    {
        var source = """
[Task("notify")]
[Description("Send a notification to the team")]
public class NotifyTask
{
    public async Task RunAsync(CancellationToken ct)
    {
        Log("notify");
    }
}
""";

        var result = TaskScriptEngine.Parse(source);

        result.Success.Should().BeTrue();
        result.Definition!.Description.Should().Be("Send a notification to the team");
    }

    [Test]
    public void Parse_NoDescription_DefinitionDescriptionIsNull()
    {
        var source = """
[Task("minimal")]
public class MinimalTask
{
    public async Task RunAsync(CancellationToken ct) { }
}
""";

        var result = TaskScriptEngine.Parse(source);

        result.Success.Should().BeTrue();
        result.Definition!.Description.Should().BeNull();
    }

    [Test]
    public void Parse_PublicProperties_BecomeParameters()
    {
        var source = """
[Task("greet")]
public class GreetTask
{
    public string Name { get; set; } = "World";
    public int Times { get; set; } = 1;

    public async Task RunAsync(CancellationToken ct)
    {
        Log($"Hello, {Name}!");
    }
}
""";

        var result = TaskScriptEngine.Parse(source);

        result.Success.Should().BeTrue();
        result.Definition!.Parameters.Should().HaveCount(2);
        result.Definition.Parameters.Select(p => p.Name).Should().Contain("Name").And.Contain("Times");
    }

    [Test]
    public void Parse_ParameterTypeNames_AreCorrect()
    {
        var source = """
[Task("typed")]
public class TypedTask
{
    public string Label { get; set; } = "";
    public int Count { get; set; } = 0;
    public bool Flag { get; set; } = false;

    public async Task RunAsync(CancellationToken ct) { }
}
""";

        var result = TaskScriptEngine.Parse(source);

        result.Success.Should().BeTrue();
        var paramMap = result.Definition!.Parameters.ToDictionary(p => p.Name, p => p.TypeName);
        paramMap["Label"].Should().Be("string");
        paramMap["Count"].Should().Be("int");
        paramMap["Flag"].Should().Be("bool");
    }

    // ─────────────────────────────────────────────────────────────
    // Step kinds
    // ─────────────────────────────────────────────────────────────

    [Test]
    public void Parse_LogCall_ProducesLogStep()
    {
        var source = """
[Task("log-task")]
public class LogTask
{
    public async Task RunAsync(CancellationToken ct)
    {
        Log("hello");
    }
}
""";

        var result = TaskScriptEngine.Parse(source);

        result.Success.Should().BeTrue();
        result.Definition!.Steps.Should().ContainSingle(s => s.Kind == TaskStepKind.Log);
    }

    [Test]
    public void Parse_ReturnStatement_ProducesReturnStep()
    {
        var source = """
[Task("early-exit")]
public class EarlyExitTask
{
    public async Task RunAsync(CancellationToken ct)
    {
        Log("before");
        return;
    }
}
""";

        var result = TaskScriptEngine.Parse(source);

        result.Success.Should().BeTrue();
        result.Definition!.Steps.Should().HaveCount(2);
        result.Definition.Steps.Last().Kind.Should().Be(TaskStepKind.Return);
    }

    [Test]
    public void Parse_IfStatement_ProducesConditionalStep()
    {
        var source = """
[Task("branching")]
public class BranchingTask
{
    public async Task RunAsync(CancellationToken ct)
    {
        if (true)
        {
            Log("yes");
        }
    }
}
""";

        var result = TaskScriptEngine.Parse(source);

        result.Success.Should().BeTrue();
        result.Definition!.Steps.Should().ContainSingle(s => s.Kind == TaskStepKind.Conditional);
    }

    [Test]
    public void Parse_IfElseStatement_ElseBodyIsPopulated()
    {
        var source = """
[Task("if-else")]
public class IfElseTask
{
    public async Task RunAsync(CancellationToken ct)
    {
        if (true)
        {
            Log("yes");
        }
        else
        {
            Log("no");
        }
    }
}
""";

        var result = TaskScriptEngine.Parse(source);

        result.Success.Should().BeTrue();
        var conditional = result.Definition!.Steps.Single(s => s.Kind == TaskStepKind.Conditional);
        conditional.Body.Should().ContainSingle(s => s.Kind == TaskStepKind.Log);
        conditional.ElseBody.Should().ContainSingle(s => s.Kind == TaskStepKind.Log);
    }

    [Test]
    public void Parse_ChatCall_ProducesChatStep()
    {
        var source = """
[Task("chat")]
public class ChatTask
{
    public async Task RunAsync(CancellationToken ct)
    {
        var reply = await Chat(agentId, "Summarise this.");
    }
}
""";

        var result = TaskScriptEngine.Parse(source);

        result.Success.Should().BeTrue();
        result.Definition!.Steps.Should().ContainSingle(s => s.Kind == TaskStepKind.Chat);
    }

    [Test]
    public void Parse_HttpGetCall_ProducesHttpRequestStep()
    {
        var source = """
[Task("http")]
public class HttpTask
{
    public async Task RunAsync(CancellationToken ct)
    {
        var response = await HttpGet("https://example.com/api");
    }
}
""";

        var result = TaskScriptEngine.Parse(source);

        result.Success.Should().BeTrue();
        var step = result.Definition!.Steps.Single(s => s.Kind == TaskStepKind.HttpRequest);
        step.HttpMethod.Should().Be("GET");
        step.Expression.Should().Contain("example.com");
    }

    // ─────────────────────────────────────────────────────────────
    // [ToolCall] hooks
    // ─────────────────────────────────────────────────────────────

    [Test]
    public void Parse_ToolCallHook_IsExtracted()
    {
        var source = """
[Task("hooked")]
public class HookedTask
{
    public async Task RunAsync(CancellationToken ct)
    {
        Log("main");
    }

    [ToolCall("get-status")]
    [Description("Returns the current status")]
    public string GetStatus(string context, CancellationToken ct)
    {
        Log("status called");
        return status;
    }
}
""";

        var result = TaskScriptEngine.Parse(source);

        result.Success.Should().BeTrue();
        result.Definition!.ToolCallHooks.Should().ContainSingle(h => h.Name == "get-status");
        var hook = result.Definition.ToolCallHooks.Single(h => h.Name == "get-status");
        hook.Description.Should().Be("Returns the current status");
        hook.Parameters.Should().ContainSingle(p => p.Name == "context");
    }

    // ─────────────────────────────────────────────────────────────
    // Invalid scripts
    // ─────────────────────────────────────────────────────────────

    [Test]
    public void Parse_NoTaskAttribute_FailsWithTaskError()
    {
        var source = """
public class NoAttributeTask
{
    public async Task RunAsync(CancellationToken ct) { }
}
""";

        var result = TaskScriptEngine.Parse(source);

        result.Success.Should().BeFalse();
        result.Diagnostics.Should().Contain(d => d.Code == "TASK001");
    }

    [Test]
    public void Parse_MissingRunAsync_FailsWithTaskError()
    {
        var source = """
[Task("bad")]
public class BadTask
{
    public void NotTheRightMethod() { }
}
""";

        var result = TaskScriptEngine.Parse(source);

        result.Success.Should().BeFalse();
        result.Diagnostics.Should().Contain(d => d.Code == "TASK003");
    }

    [Test]
    public void Parse_UnsupportedStatement_FailsWithTaskError()
    {
        var source = """
[Task("uses-for")]
public class UsesForTask
{
    public async Task RunAsync(CancellationToken ct)
    {
        for (int i = 0; i < 3; i++) { }
    }
}
""";

        var result = TaskScriptEngine.Parse(source);

        result.Success.Should().BeFalse();
        result.Diagnostics.Should().Contain(d => d.Code == "TASK011");
    }

    [Test]
    public void Parse_EmptyTaskName_FailsWithTaskError()
    {
        var source = """
[Task("")]
public class EmptyNameTask
{
    public async Task RunAsync(CancellationToken ct) { }
}
""";

        var result = TaskScriptEngine.Parse(source);

        result.Success.Should().BeFalse();
        result.Diagnostics.Should().Contain(d => d.Code == "TASK002");
    }
}
