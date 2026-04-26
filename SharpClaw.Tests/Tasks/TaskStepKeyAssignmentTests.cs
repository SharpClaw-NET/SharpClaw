using FluentAssertions;
using NUnit.Framework;
using SharpClaw.Application.Infrastructure.Tasks;
using SharpClaw.Contracts.Tasks;

namespace SharpClaw.Tests.Tasks;

/// <summary>
/// Verifies that the parser sets the correct <see cref="WellKnownTaskStepKeys"/>
/// string key on every parsed <see cref="TaskStepDefinition.StepKey"/> for
/// representative core step kinds.
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
        result.Definition!.Steps.Single().StepKey.Should().Be(WellKnownTaskStepKeys.DeclareVariable);
    }

    [Test]
    public void Parse_Assignment_HasAssignKey()
    {
        var result = TaskScriptEngine.Parse(Wrap("x = 1;"));

        result.Success.Should().BeTrue();
        result.Definition!.Steps.Single().StepKey.Should().Be(WellKnownTaskStepKeys.Assign);
    }

    [Test]
    public void Parse_Conditional_HasConditionalKey()
    {
        var result = TaskScriptEngine.Parse(Wrap("if (true) { Log(\"y\"); }"));

        result.Success.Should().BeTrue();
        result.Definition!.Steps.Single().StepKey.Should().Be(WellKnownTaskStepKeys.Conditional);
    }

    [Test]
    public void Parse_WhileLoop_HasLoopKey()
    {
        var result = TaskScriptEngine.Parse(Wrap("while (false) { }"));

        result.Success.Should().BeTrue();
        result.Definition!.Steps.Single().StepKey.Should().Be(WellKnownTaskStepKeys.Loop);
    }

    [Test]
    public void Parse_ForEachLoop_HasLoopKey()
    {
        var result = TaskScriptEngine.Parse(Wrap("foreach (var item in items) { Log(item); }"));

        result.Success.Should().BeTrue();
        result.Definition!.Steps.Single().StepKey.Should().Be(WellKnownTaskStepKeys.Loop);
    }

    [Test]
    public void Parse_ReturnStatement_HasReturnKey()
    {
        var result = TaskScriptEngine.Parse(Wrap("return;"));

        result.Success.Should().BeTrue();
        result.Definition!.Steps.Single().StepKey.Should().Be(WellKnownTaskStepKeys.Return);
    }

    // ── Context-API method calls ──────────────────────────────────────────────

    [Test]
    public void Parse_LogCall_HasLogKey()
    {
        var result = TaskScriptEngine.Parse(Wrap("Log(\"hello\");"));

        result.Success.Should().BeTrue();
        result.Definition!.Steps.Single().StepKey.Should().Be(WellKnownTaskStepKeys.Log);
    }

    [Test]
    public void Parse_ChatCall_HasChatKey()
    {
        var result = TaskScriptEngine.Parse(Wrap("var r = await Chat(agentId, \"hello\");"));

        result.Success.Should().BeTrue();
        result.Definition!.Steps.Single().StepKey.Should().Be(WellKnownTaskStepKeys.Chat);
    }

    [Test]
    public void Parse_ChatStreamCall_HasChatStreamKey()
    {
        var result = TaskScriptEngine.Parse(Wrap("await ChatStream(agentId, \"msg\");"));

        result.Success.Should().BeTrue();
        result.Definition!.Steps.Single().StepKey.Should().Be(WellKnownTaskStepKeys.ChatStream);
    }

    [Test]
    public void Parse_EmitCall_HasEmitKey()
    {
        var result = TaskScriptEngine.Parse(Wrap("await Emit(result);"));

        result.Success.Should().BeTrue();
        result.Definition!.Steps.Single().StepKey.Should().Be(WellKnownTaskStepKeys.Emit);
    }

    [Test]
    public void Parse_ParseResponseCall_HasParseResponseKey()
    {
        var result = TaskScriptEngine.Parse(Wrap("var r = await ParseResponse<MyData>(reply);"));

        result.Success.Should().BeTrue();
        result.Definition!.Steps.Single().StepKey.Should().Be(WellKnownTaskStepKeys.ParseResponse);
    }

    [Test]
    public void Parse_HttpGetCall_HasHttpRequestKey()
    {
        var result = TaskScriptEngine.Parse(Wrap("var r = await HttpGet(\"https://example.com\");"));

        result.Success.Should().BeTrue();
        result.Definition!.Steps.Single().StepKey.Should().Be(WellKnownTaskStepKeys.HttpRequest);
    }

    [Test]
    public void Parse_HttpPostCall_HasHttpRequestKey()
    {
        var result = TaskScriptEngine.Parse(Wrap("var r = await HttpPost(\"https://example.com\");"));

        result.Success.Should().BeTrue();
        result.Definition!.Steps.Single().StepKey.Should().Be(WellKnownTaskStepKeys.HttpRequest);
    }

    [Test]
    public void Parse_TaskDelayCall_HasDelayKey()
    {
        var result = TaskScriptEngine.Parse(Wrap("await Task.Delay(1000);"));

        result.Success.Should().BeTrue();
        result.Definition!.Steps.Single().StepKey.Should().Be(WellKnownTaskStepKeys.Delay);
    }

    [Test]
    public void Parse_WaitUntilStoppedCall_HasWaitUntilStoppedKey()
    {
        var result = TaskScriptEngine.Parse(Wrap("await WaitUntilStopped();"));

        result.Success.Should().BeTrue();
        result.Definition!.Steps.Single().StepKey.Should().Be(WellKnownTaskStepKeys.WaitUntilStopped);
    }

    [Test]
    public void Parse_FindModelCall_HasFindModelKey()
    {
        var result = TaskScriptEngine.Parse(Wrap("var m = await FindModel(modelId);"));

        result.Success.Should().BeTrue();
        result.Definition!.Steps.Single().StepKey.Should().Be(WellKnownTaskStepKeys.FindModel);
    }

    [Test]
    public void Parse_FindAgentCall_HasFindAgentKey()
    {
        var result = TaskScriptEngine.Parse(Wrap("var a = await FindAgent(agentId);"));

        result.Success.Should().BeTrue();
        result.Definition!.Steps.Single().StepKey.Should().Be(WellKnownTaskStepKeys.FindAgent);
    }

    [Test]
    public void Parse_CreateAgentCall_HasCreateAgentKey()
    {
        var result = TaskScriptEngine.Parse(Wrap("var a = await CreateAgent(\"name\", modelId);"));

        result.Success.Should().BeTrue();
        result.Definition!.Steps.Single().StepKey.Should().Be(WellKnownTaskStepKeys.CreateAgent);
    }

    [Test]
    public void Parse_CreateChannelCall_HasCreateChannelKey()
    {
        var result = TaskScriptEngine.Parse(Wrap("var c = await CreateChannel(\"title\", agentId);"));

        result.Success.Should().BeTrue();
        result.Definition!.Steps.Single().StepKey.Should().Be(WellKnownTaskStepKeys.CreateChannel);
    }

    [Test]
    public void Parse_FindChannelCall_HasFindChannelKey()
    {
        var result = TaskScriptEngine.Parse(Wrap("var c = await FindChannel(channelId);"));

        result.Success.Should().BeTrue();
        result.Definition!.Steps.Single().StepKey.Should().Be(WellKnownTaskStepKeys.FindChannel);
    }

    [Test]
    public void Parse_CreateRoleCall_HasCreateRoleKey()
    {
        var result = TaskScriptEngine.Parse(Wrap("var r = await CreateRole(\"admin\");"));

        result.Success.Should().BeTrue();
        result.Definition!.Steps.Single().StepKey.Should().Be(WellKnownTaskStepKeys.CreateRole);
    }

    [Test]
    public void Parse_AssignRoleCall_HasAssignRoleKey()
    {
        var result = TaskScriptEngine.Parse(Wrap("await AssignRole(agentId, roleId);"));

        result.Success.Should().BeTrue();
        result.Definition!.Steps.Single().StepKey.Should().Be(WellKnownTaskStepKeys.AssignRole);
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
        cond.StepKey.Should().Be(WellKnownTaskStepKeys.Conditional);
        cond.Body!.Single().StepKey.Should().Be(WellKnownTaskStepKeys.Log);
        cond.ElseBody!.Single().StepKey.Should().Be(WellKnownTaskStepKeys.Log);
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
        loop.StepKey.Should().Be(WellKnownTaskStepKeys.Loop);
        loop.Body!.Single().StepKey.Should().Be(WellKnownTaskStepKeys.Log);
    }
}
