using FluentAssertions;
using NUnit.Framework;
using SharpClaw.Core.Tasks;
using SharpClaw.Contracts.Tasks;

namespace SharpClaw.Tests.Tasks;

[TestFixture]
public class TaskStatementKeyAssignmentTests
{
    private static string Wrap(string body) => $$"""
[Task("test")]
public class TestTask
{
    public async Task RunAsync(CancellationToken ct)
    {
        {{body}}
    }
}
""";

    [Test]
    public void Parse_Declaration_HasDeclareVariableKey()
    {
        var result = TaskScriptEngine.Parse(Wrap("var x = 1;"));

        result.Success.Should().BeTrue();
        result.Definition!.Statements.Single().StatementKey.Should().Be(TaskLanguageStatementKeys.DeclareVariable);
    }

    [Test]
    public void Parse_Assignment_HasAssignKey()
    {
        var result = TaskScriptEngine.Parse(Wrap("x = 1;"));

        result.Success.Should().BeTrue();
        result.Definition!.Statements.Single().StatementKey.Should().Be(TaskLanguageStatementKeys.Assign);
    }

    [Test]
    public void Parse_Conditional_HasConditionalKey()
    {
        var result = TaskScriptEngine.Parse(Wrap("if (true) { Log(\"y\"); }"));

        result.Success.Should().BeTrue();
        result.Definition!.Statements.Single().StatementKey.Should().Be(TaskLanguageStatementKeys.Conditional);
    }

    [Test]
    public void Parse_WhileLoop_HasLoopKey()
    {
        var result = TaskScriptEngine.Parse(Wrap("while (false) { }"));

        result.Success.Should().BeTrue();
        result.Definition!.Statements.Single().StatementKey.Should().Be(TaskLanguageStatementKeys.Loop);
    }

    [Test]
    public void Parse_ForEachLoop_HasLoopKey()
    {
        var result = TaskScriptEngine.Parse(Wrap("foreach (var item in items) { Log(item); }"));

        result.Success.Should().BeTrue();
        result.Definition!.Statements.Single().StatementKey.Should().Be(TaskLanguageStatementKeys.Loop);
    }

    [Test]
    public void Parse_ReturnStatement_HasReturnKey()
    {
        var result = TaskScriptEngine.Parse(Wrap("return;"));

        result.Success.Should().BeTrue();
        result.Definition!.Statements.Single().StatementKey.Should().Be(TaskLanguageStatementKeys.Return);
    }

    [Test]
    public void Parse_LogCall_HasLogKey()
    {
        var result = TaskScriptEngine.Parse(Wrap("Log(\"hello\");"));

        result.Success.Should().BeTrue();
        result.Definition!.Statements.Single().StatementKey.Should().Be(TaskLanguageStatementKeys.Log);
    }

    [Test]
    public void Parse_TaskDelayCall_HasDelayKey()
    {
        var result = TaskScriptEngine.Parse(Wrap("await Task.Delay(1000);"));

        result.Success.Should().BeTrue();
        result.Definition!.Statements.Single().StatementKey.Should().Be(TaskLanguageStatementKeys.Delay);
    }

    [Test]
    public void Parse_WaitUntilStoppedCall_HasWaitUntilStoppedKey()
    {
        var result = TaskScriptEngine.Parse(Wrap("await WaitUntilStopped();"));

        result.Success.Should().BeTrue();
        result.Definition!.Statements.Single().StatementKey.Should().Be(TaskLanguageStatementKeys.WaitUntilStopped);
    }

    [Test]
    public void Parse_ConditionalNestedStatements_HaveCorrectKeys()
    {
        var source = Wrap("""
if (true)
{
    Log("yes");
}
else
{
    Log("no");
}
""");

        var result = TaskScriptEngine.Parse(source);

        result.Success.Should().BeTrue();
        var conditional = result.Definition!.Statements.Single();
        conditional.StatementKey.Should().Be(TaskLanguageStatementKeys.Conditional);
        conditional.Body!.Single().StatementKey.Should().Be(TaskLanguageStatementKeys.Log);
        conditional.ElseBody!.Single().StatementKey.Should().Be(TaskLanguageStatementKeys.Log);
    }

    [Test]
    public void Parse_LoopNestedStatements_HaveCorrectKeys()
    {
        var source = Wrap("""
while (true)
{
    Log("tick");
}
""");

        var result = TaskScriptEngine.Parse(source);

        result.Success.Should().BeTrue();
        var loop = result.Definition!.Statements.Single();
        loop.StatementKey.Should().Be(TaskLanguageStatementKeys.Loop);
        loop.Body!.Single().StatementKey.Should().Be(TaskLanguageStatementKeys.Log);
    }
}
