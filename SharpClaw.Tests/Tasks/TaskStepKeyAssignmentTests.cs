using FluentAssertions;
using NUnit.Framework;
using SharpClaw.Application.Infrastructure.Tasks;
using SharpClaw.Contracts.Tasks;
using SharpClaw.Modules.AgentOrchestration;
using SharpClaw.Modules.Http;

namespace SharpClaw.Tests.Tasks;

/// <summary>
/// Verifies that the parser sets the correct module-owned step key on every
/// parsed <see cref="TaskStepDefinition.StepKey"/> for representative step
/// kinds. Step keys are owned by the TaskScripting, AgentOrchestration, and
/// HTTP modules; the literal string values intentionally match the legacy
/// <c>core.*</c> wire format for backward compatibility.
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
        result.Definition!.Steps.Single().StepKey.Should().Be(TaskScriptingStepKeys.DeclareVariable);
    }

    [Test]
    public void Parse_Assignment_HasAssignKey()
    {
        var result = TaskScriptEngine.Parse(Wrap("x = 1;"));

        result.Success.Should().BeTrue();
        result.Definition!.Steps.Single().StepKey.Should().Be(TaskScriptingStepKeys.Assign);
    }

    [Test]
    public void Parse_Conditional_HasConditionalKey()
    {
        var result = TaskScriptEngine.Parse(Wrap("if (true) { Log(\"y\"); }"));

        result.Success.Should().BeTrue();
        result.Definition!.Steps.Single().StepKey.Should().Be(TaskScriptingStepKeys.Conditional);
    }

    [Test]
    public void Parse_WhileLoop_HasLoopKey()
    {
        var result = TaskScriptEngine.Parse(Wrap("while (false) { }"));

        result.Success.Should().BeTrue();
        result.Definition!.Steps.Single().StepKey.Should().Be(TaskScriptingStepKeys.Loop);
    }

    [Test]
    public void Parse_ForEachLoop_HasLoopKey()
    {
        var result = TaskScriptEngine.Parse(Wrap("foreach (var item in items) { Log(item); }"));

        result.Success.Should().BeTrue();
        result.Definition!.Steps.Single().StepKey.Should().Be(TaskScriptingStepKeys.Loop);
    }

    [Test]
    public void Parse_ReturnStatement_HasReturnKey()
    {
        var result = TaskScriptEngine.Parse(Wrap("return;"));

        result.Success.Should().BeTrue();
        result.Definition!.Steps.Single().StepKey.Should().Be(TaskScriptingStepKeys.Return);
    }

    // ── Context-API method calls ──────────────────────────────────────────────

    [Test]
    public void Parse_LogCall_HasLogKey()
    {
        var result = TaskScriptEngine.Parse(Wrap("Log(\"hello\");"));

        result.Success.Should().BeTrue();
        result.Definition!.Steps.Single().StepKey.Should().Be(TaskScriptingStepKeys.Log);
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
    public void Parse_HttpGetCall_HasHttpRequestKey()
    {
        var result = TaskScriptEngine.Parse(Wrap("var r = await HttpGet(\"https://example.com\");"));

        result.Success.Should().BeTrue();
        result.Definition!.Steps.Single().StepKey.Should().Be(HttpStepKeys.HttpRequest);
    }

    [Test]
    public void Parse_HttpPostCall_HasHttpRequestKey()
    {
        var result = TaskScriptEngine.Parse(Wrap("var r = await HttpPost(\"https://example.com\");"));

        result.Success.Should().BeTrue();
        result.Definition!.Steps.Single().StepKey.Should().Be(HttpStepKeys.HttpRequest);
    }

    [Test]
    public void Parse_TaskDelayCall_HasDelayKey()
    {
        var result = TaskScriptEngine.Parse(Wrap("await Task.Delay(1000);"));

        result.Success.Should().BeTrue();
        result.Definition!.Steps.Single().StepKey.Should().Be(TaskScriptingStepKeys.Delay);
    }

    [Test]
    public void Parse_WaitUntilStoppedCall_HasWaitUntilStoppedKey()
    {
        var result = TaskScriptEngine.Parse(Wrap("await WaitUntilStopped();"));

        result.Success.Should().BeTrue();
        result.Definition!.Steps.Single().StepKey.Should().Be(TaskScriptingStepKeys.WaitUntilStopped);
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
        cond.StepKey.Should().Be(TaskScriptingStepKeys.Conditional);
        cond.Body!.Single().StepKey.Should().Be(TaskScriptingStepKeys.Log);
        cond.ElseBody!.Single().StepKey.Should().Be(TaskScriptingStepKeys.Log);
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
        loop.StepKey.Should().Be(TaskScriptingStepKeys.Loop);
        loop.Body!.Single().StepKey.Should().Be(TaskScriptingStepKeys.Log);
    }
}

