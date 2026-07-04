using FluentAssertions;
using NUnit.Framework;
using SharpClaw.Core.Tasks;
using SharpClaw.Core.Tasks.Parsing;
using SharpClaw.Contracts.Tasks;
using SharpClaw.Modules.AgentOrchestration;

namespace SharpClaw.Tests.Tasks;

/// <summary>
/// Verifies that the parser sets the correct step key on every
/// parsed <see cref="TaskStepDefinition.StepKey"/> for representative step
/// kind. C# language statements are Core-owned; Agent Orchestration contributes
/// only module/tool operation calls and trigger names.
/// </summary>
[TestFixture]
public class TaskStepKeyAssignmentTests
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
        result.Definition!.Steps.Single().StepKey.Should().Be(TaskLanguageStepKeys.DeclareVariable);
    }

    [Test]
    public void Parse_Assignment_HasAssignKey()
    {
        var result = TaskScriptEngine.Parse(Wrap("x = 1;"));

        result.Success.Should().BeTrue();
        result.Definition!.Steps.Single().StepKey.Should().Be(TaskLanguageStepKeys.Assign);
    }

    [Test]
    public void Parse_Conditional_HasConditionalKey()
    {
        var result = TaskScriptEngine.Parse(Wrap("if (true) { Log(\"y\"); }"));

        result.Success.Should().BeTrue();
        result.Definition!.Steps.Single().StepKey.Should().Be(TaskLanguageStepKeys.Conditional);
    }

    [Test]
    public void Parse_WhileLoop_HasLoopKey()
    {
        var result = TaskScriptEngine.Parse(Wrap("while (false) { }"));

        result.Success.Should().BeTrue();
        result.Definition!.Steps.Single().StepKey.Should().Be(TaskLanguageStepKeys.Loop);
    }

    [Test]
    public void Parse_ForEachLoop_HasLoopKey()
    {
        var result = TaskScriptEngine.Parse(Wrap("foreach (var item in items) { Log(item); }"));

        result.Success.Should().BeTrue();
        result.Definition!.Steps.Single().StepKey.Should().Be(TaskLanguageStepKeys.Loop);
    }

    [Test]
    public void Parse_ReturnStatement_HasReturnKey()
    {
        var result = TaskScriptEngine.Parse(Wrap("return;"));

        result.Success.Should().BeTrue();
        result.Definition!.Steps.Single().StepKey.Should().Be(TaskLanguageStepKeys.Return);
    }

    // ── Context-API method calls ──────────────────────────────────────────────

    [Test]
    public void Parse_LogCall_HasLogKey()
    {
        var result = TaskScriptEngine.Parse(Wrap("Log(\"hello\");"));

        result.Success.Should().BeTrue();
        result.Definition!.Steps.Single().StepKey.Should().Be(TaskLanguageStepKeys.Log);
    }

    [Test]
    public void Parse_ChatCall_HasChatKey()
    {
        var result = TaskScriptEngine.Parse(Wrap("var r = await Chat(agentId, \"hello\");"));

        result.Success.Should().BeTrue();
        result.Definition!.Steps.Single().StepKey.Should().Be(AgentOrchestrationStepKeys.Chat);
    }

    [Test]
    public void Parse_ChatStreamCall_HasChatStreamKey()
    {
        var result = TaskScriptEngine.Parse(Wrap("await ChatStream(agentId, \"msg\");"));

        result.Success.Should().BeTrue();
        result.Definition!.Steps.Single().StepKey.Should().Be(AgentOrchestrationStepKeys.ChatStream);
    }

    [Test]
    public void Parse_EmitCall_HasEmitKey()
    {
        var result = TaskScriptEngine.Parse(Wrap("await Emit(result);"));

        result.Success.Should().BeTrue();
        result.Definition!.Steps.Single().StepKey.Should().Be(AgentOrchestrationStepKeys.Emit);
    }

    [Test]
    public void Parse_ParseResponseCall_HasParseResponseKey()
    {
        var result = TaskScriptEngine.Parse(Wrap("var r = await ParseResponse<MyData>(reply);"));

        result.Success.Should().BeTrue();
        result.Definition!.Steps.Single().StepKey.Should().Be(AgentOrchestrationStepKeys.ParseResponse);
    }

    [Test]
    public void Parse_TaskDelayCall_HasDelayKey()
    {
        var result = TaskScriptEngine.Parse(Wrap("await Task.Delay(1000);"));

        result.Success.Should().BeTrue();
        result.Definition!.Steps.Single().StepKey.Should().Be(TaskLanguageStepKeys.Delay);
    }

    [Test]
    public void Parse_WaitUntilStoppedCall_HasWaitUntilStoppedKey()
    {
        var result = TaskScriptEngine.Parse(Wrap("await WaitUntilStopped();"));

        result.Success.Should().BeTrue();
        result.Definition!.Steps.Single().StepKey.Should().Be(TaskLanguageStepKeys.WaitUntilStopped);
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
            result.Definition!.Steps.Select(step => step.StepKey).Should().Equal(
                TaskLanguageStepKeys.DeclareVariable,
                TaskLanguageStepKeys.Assign,
                TaskLanguageStepKeys.Conditional,
                TaskLanguageStepKeys.Loop,
                TaskLanguageStepKeys.WaitUntilStopped,
                TaskLanguageStepKeys.Return);
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
        result.Definition!.Steps.Single().StepKey.Should().Be(AgentOrchestrationStepKeys.FindModel);
    }

    [Test]
    public void Parse_FindAgentCall_HasFindAgentKey()
    {
        var result = TaskScriptEngine.Parse(Wrap("var a = await FindAgent(agentId);"));

        result.Success.Should().BeTrue();
        result.Definition!.Steps.Single().StepKey.Should().Be(AgentOrchestrationStepKeys.FindAgent);
    }

    [Test]
    public void Parse_CreateAgentCall_HasCreateAgentKey()
    {
        var result = TaskScriptEngine.Parse(Wrap("var a = await CreateAgent(\"name\", modelId);"));

        result.Success.Should().BeTrue();
        result.Definition!.Steps.Single().StepKey.Should().Be(AgentOrchestrationStepKeys.CreateAgent);
    }

    [Test]
    public void Parse_CreateChannelCall_HasCreateChannelKey()
    {
        var result = TaskScriptEngine.Parse(Wrap("var c = await CreateChannel(\"title\", agentId);"));

        result.Success.Should().BeTrue();
        result.Definition!.Steps.Single().StepKey.Should().Be(AgentOrchestrationStepKeys.CreateChannel);
    }

    [Test]
    public void Parse_FindChannelCall_HasFindChannelKey()
    {
        var result = TaskScriptEngine.Parse(Wrap("var c = await FindChannel(channelId);"));

        result.Success.Should().BeTrue();
        result.Definition!.Steps.Single().StepKey.Should().Be(AgentOrchestrationStepKeys.FindChannel);
    }

    [Test]
    public void Parse_CreateRoleCall_HasCreateRoleKey()
    {
        var result = TaskScriptEngine.Parse(Wrap("var r = await CreateRole(\"admin\");"));

        result.Success.Should().BeTrue();
        result.Definition!.Steps.Single().StepKey.Should().Be(AgentOrchestrationStepKeys.CreateRole);
    }

    [Test]
    public void Parse_AssignRoleCall_HasAssignRoleKey()
    {
        var result = TaskScriptEngine.Parse(Wrap("await AssignRole(agentId, roleId);"));

        result.Success.Should().BeTrue();
        result.Definition!.Steps.Single().StepKey.Should().Be(AgentOrchestrationStepKeys.AssignRole);
    }

    [Test]
    public void AgentOrchestrationOperationKeys_UseModuleNamespace()
    {
        var keys = new[]
        {
            AgentOrchestrationStepKeys.Chat,
            AgentOrchestrationStepKeys.ChatStream,
            AgentOrchestrationStepKeys.ChatToThread,
            AgentOrchestrationStepKeys.Emit,
            AgentOrchestrationStepKeys.ParseResponse,
            AgentOrchestrationStepKeys.FindModel,
            AgentOrchestrationStepKeys.FindProvider,
            AgentOrchestrationStepKeys.FindAgent,
            AgentOrchestrationStepKeys.CreateAgent,
            AgentOrchestrationStepKeys.CreateThread,
            AgentOrchestrationStepKeys.CreateRole,
            AgentOrchestrationStepKeys.FindRole,
            AgentOrchestrationStepKeys.SetRolePermissions,
            AgentOrchestrationStepKeys.AssignRole,
            AgentOrchestrationStepKeys.CreateChannel,
            AgentOrchestrationStepKeys.FindChannel,
            AgentOrchestrationStepKeys.AddAllowedAgent,
        };

        keys.Should().OnlyContain(key => key.StartsWith("sharpclaw_agent_orchestration.", StringComparison.Ordinal));
        keys.Should().NotContain(key => key.StartsWith("core.", StringComparison.Ordinal));
    }

    [Test]
    public void AgentOrchestrationExecutor_HandlesOnlyModuleOwnedOperationKeys()
    {
        var executor = new AgentOrchestrationTaskStepExecutor();

        executor.CanExecute(AgentOrchestrationStepKeys.ParseResponse).Should().BeTrue();
        executor.CanExecute(AgentOrchestrationStepKeys.Chat).Should().BeTrue();
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
        var cond = result.Definition!.Steps.Single();
        cond.StepKey.Should().Be(TaskLanguageStepKeys.Conditional);
        cond.Body!.Single().StepKey.Should().Be(TaskLanguageStepKeys.Log);
        cond.ElseBody!.Single().StepKey.Should().Be(TaskLanguageStepKeys.Log);
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
        var loop = result.Definition!.Steps.Single();
        loop.StepKey.Should().Be(TaskLanguageStepKeys.Loop);
        loop.Body!.Single().StepKey.Should().Be(TaskLanguageStepKeys.Log);
    }
}

