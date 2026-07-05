using FluentAssertions;
using NUnit.Framework;
using SharpClaw.Core.Tasks;
using SharpClaw.Core.Tasks.Parsing;
using SharpClaw.Contracts.Tasks;
using SharpClaw.Modules.AgentOrchestration;

namespace SharpClaw.Tests.Tasks;

/// <summary>
/// Verifies that the parser sets the correct step key on every
/// parsed <see cref="TaskStatementDefinition.StatementKey"/> for representative step
/// kind. C# language statements are Core-owned; Agent Orchestration contributes
/// only module/tool operation calls and trigger names.
/// </summary>
[TestFixture]
public class TaskStatementKeyAssignmentTests
{
    // ── Helpers ──────────────────────────────────────────────────────────────

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

    // ── Statement constructs ──────────────────────────────────────────────────

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

    // ── Context-API method calls ──────────────────────────────────────────────

    [Test]
    public void Parse_LogCall_HasLogKey()
    {
        var result = TaskScriptEngine.Parse(Wrap("Log(\"hello\");"));

        result.Success.Should().BeTrue();
        result.Definition!.Statements.Single().StatementKey.Should().Be(TaskLanguageStatementKeys.Log);
    }

    [Test]
    public void Parse_ChatCall_HasChatKey()
    {
        var result = TaskScriptEngine.Parse(Wrap("var r = await Chat(agentId, \"hello\");"));

        result.Success.Should().BeTrue();
        result.Definition!.Statements.Single().StatementKey.Should().Be(AgentOrchestrationOperationKeys.Chat);
    }

    [Test]
    public void Parse_ChatStreamCall_HasChatStreamKey()
    {
        var result = TaskScriptEngine.Parse(Wrap("await ChatStream(agentId, \"msg\");"));

        result.Success.Should().BeTrue();
        result.Definition!.Statements.Single().StatementKey.Should().Be(AgentOrchestrationOperationKeys.ChatStream);
    }

    [Test]
    public void Parse_EmitCall_HasEmitKey()
    {
        var result = TaskScriptEngine.Parse(Wrap("await Emit(result);"));

        result.Success.Should().BeTrue();
        result.Definition!.Statements.Single().StatementKey.Should().Be(AgentOrchestrationOperationKeys.Emit);
    }

    [Test]
    public void Parse_ParseResponseCall_HasParseResponseKey()
    {
        var result = TaskScriptEngine.Parse(Wrap("var r = await ParseResponse<MyData>(reply);"));

        result.Success.Should().BeTrue();
        result.Definition!.Statements.Single().StatementKey.Should().Be(AgentOrchestrationOperationKeys.ParseResponse);
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
    [NonParallelizable]
    public void Parse_CoreLanguageStatements_DoesNotRequireAgentOrchestrationParserExtension()
    {
        try
        {
            TaskScriptParser.UnregisterModule(TaskScriptingParserExtension.Instance);

            var source = Wrap("""
var value = "start";
value = "done";
if (value == "done")
{
    Log(value);
}
while (false)
{
    await Task.Delay(1);
}
await WaitUntilStopped();
return;
""");

            var result = TaskScriptEngine.Parse(source);

            result.Success.Should().BeTrue(string.Join(Environment.NewLine, result.Diagnostics.Select(d => d.Message)));
            result.Definition!.Statements.Select(step => step.StatementKey).Should().Equal(
                TaskLanguageStatementKeys.DeclareVariable,
                TaskLanguageStatementKeys.Assign,
                TaskLanguageStatementKeys.Conditional,
                TaskLanguageStatementKeys.Loop,
                TaskLanguageStatementKeys.WaitUntilStopped,
                TaskLanguageStatementKeys.Return);
        }
        finally
        {
            TaskScriptParser.RegisterModule(TaskScriptingParserExtension.Instance);
        }
    }

    [Test]
    public void Parse_FindModelCall_HasFindModelKey()
    {
        var result = TaskScriptEngine.Parse(Wrap("var m = await FindModel(modelId);"));

        result.Success.Should().BeTrue();
        result.Definition!.Statements.Single().StatementKey.Should().Be(AgentOrchestrationOperationKeys.FindModel);
    }

    [Test]
    public void Parse_FindAgentCall_HasFindAgentKey()
    {
        var result = TaskScriptEngine.Parse(Wrap("var a = await FindAgent(agentId);"));

        result.Success.Should().BeTrue();
        result.Definition!.Statements.Single().StatementKey.Should().Be(AgentOrchestrationOperationKeys.FindAgent);
    }

    [Test]
    public void Parse_CreateAgentCall_HasCreateAgentKey()
    {
        var result = TaskScriptEngine.Parse(Wrap("var a = await CreateAgent(\"name\", modelId);"));

        result.Success.Should().BeTrue();
        result.Definition!.Statements.Single().StatementKey.Should().Be(AgentOrchestrationOperationKeys.CreateAgent);
    }

    [Test]
    public void Parse_CreateChannelCall_HasCreateChannelKey()
    {
        var result = TaskScriptEngine.Parse(Wrap("var c = await CreateChannel(\"title\", agentId);"));

        result.Success.Should().BeTrue();
        result.Definition!.Statements.Single().StatementKey.Should().Be(AgentOrchestrationOperationKeys.CreateChannel);
    }

    [Test]
    public void Parse_FindChannelCall_HasFindChannelKey()
    {
        var result = TaskScriptEngine.Parse(Wrap("var c = await FindChannel(channelId);"));

        result.Success.Should().BeTrue();
        result.Definition!.Statements.Single().StatementKey.Should().Be(AgentOrchestrationOperationKeys.FindChannel);
    }

    [Test]
    public void Parse_CreateRoleCall_HasCreateRoleKey()
    {
        var result = TaskScriptEngine.Parse(Wrap("var r = await CreateRole(\"admin\");"));

        result.Success.Should().BeTrue();
        result.Definition!.Statements.Single().StatementKey.Should().Be(AgentOrchestrationOperationKeys.CreateRole);
    }

    [Test]
    public void Parse_AssignRoleCall_HasAssignRoleKey()
    {
        var result = TaskScriptEngine.Parse(Wrap("await AssignRole(agentId, roleId);"));

        result.Success.Should().BeTrue();
        result.Definition!.Statements.Single().StatementKey.Should().Be(AgentOrchestrationOperationKeys.AssignRole);
    }

    [Test]
    public void AgentOrchestrationOperationKeys_UseModuleNamespace()
    {
        var keys = new[]
        {
            AgentOrchestrationOperationKeys.Chat,
            AgentOrchestrationOperationKeys.ChatStream,
            AgentOrchestrationOperationKeys.ChatToThread,
            AgentOrchestrationOperationKeys.Emit,
            AgentOrchestrationOperationKeys.ParseResponse,
            AgentOrchestrationOperationKeys.FindModel,
            AgentOrchestrationOperationKeys.FindProvider,
            AgentOrchestrationOperationKeys.FindAgent,
            AgentOrchestrationOperationKeys.CreateAgent,
            AgentOrchestrationOperationKeys.CreateThread,
            AgentOrchestrationOperationKeys.CreateRole,
            AgentOrchestrationOperationKeys.FindRole,
            AgentOrchestrationOperationKeys.SetRolePermissions,
            AgentOrchestrationOperationKeys.AssignRole,
            AgentOrchestrationOperationKeys.CreateChannel,
            AgentOrchestrationOperationKeys.FindChannel,
            AgentOrchestrationOperationKeys.AddAllowedAgent,
        };

        keys.Should().OnlyContain(key => key.StartsWith("sharpclaw_agent_orchestration.", StringComparison.Ordinal));
        keys.Should().NotContain(key => key.StartsWith("core.", StringComparison.Ordinal));
    }

    [Test]
    public void AgentOrchestrationExecutor_HandlesOnlyModuleOwnedOperationKeys()
    {
        var executor = new AgentOrchestrationTaskOperationExecutor();

        executor.CanExecute(AgentOrchestrationOperationKeys.ParseResponse).Should().BeTrue();
        executor.CanExecute(AgentOrchestrationOperationKeys.Chat).Should().BeTrue();
        executor.CanExecute("core.parse_response").Should().BeFalse();
        executor.CanExecute("core.chat").Should().BeFalse();
    }

    [Test]
    public void Parse_ConditionalNestedSteps_HaveCorrectKeys()
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
        var cond = result.Definition!.Statements.Single();
        cond.StatementKey.Should().Be(TaskLanguageStatementKeys.Conditional);
        cond.Body!.Single().StatementKey.Should().Be(TaskLanguageStatementKeys.Log);
        cond.ElseBody!.Single().StatementKey.Should().Be(TaskLanguageStatementKeys.Log);
    }

    [Test]
    public void Parse_LoopNestedSteps_HaveCorrectKeys()
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

