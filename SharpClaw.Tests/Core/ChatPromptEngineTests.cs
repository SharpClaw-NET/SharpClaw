using System.Text.Json;
using SharpClaw.Contracts.Entities.Core;
using SharpClaw.Core.Chat;

namespace SharpClaw.Tests.Core;

[TestFixture]
public sealed class ChatPromptEngineTests
{
    private readonly ChatPromptEngine _engine = new();

    [Test]
    public void BuildEffectiveSystemPrompt_WhenCorePromptEnabled_AppendsNativeToolSuffix()
    {
        var prompt = _engine.BuildEffectiveSystemPrompt(
            "Agent prompt",
            includeCorePrompt: true,
            disableDefaultSystemPrompt: false);

        prompt.Should().StartWith("Agent prompt");
        prompt.Should().Contain("Statuses:");
        prompt.Should().Contain("Permissions:");
    }

    [Test]
    public void BuildEffectiveSystemPrompt_WhenDefaultSystemPromptDisabled_ReturnsAgentPromptOnly()
    {
        var prompt = _engine.BuildEffectiveSystemPrompt(
            "Agent prompt",
            includeCorePrompt: true,
            disableDefaultSystemPrompt: true);

        prompt.Should().Be("Agent prompt");
    }

    [Test]
    public void BuildEffectiveSystemPrompt_WhenNoAgentPrompt_ReturnsNativeToolSuffix()
    {
        var prompt = _engine.BuildEffectiveSystemPrompt(
            agentPrompt: null,
            includeCorePrompt: true,
            disableDefaultSystemPrompt: false);

        prompt.Should().Be(_engine.NativeToolSystemSuffix);
    }

    [Test]
    public void BuildCompletionParameters_MapsAgentTuningFieldsAndHostIds()
    {
        var modelId = Guid.NewGuid();
        var threadId = Guid.NewGuid();
        var responseFormat = JsonDocument.Parse("""{"type":"json_object"}""")
            .RootElement
            .Clone();
        var agent = new AgentDB
        {
            Name = "Agent",
            ModelId = modelId,
            Temperature = 0.2f,
            TopP = 0.9f,
            TopK = 40,
            FrequencyPenalty = 0.1f,
            PresencePenalty = 0.3f,
            Stop = ["done"],
            Seed = 7,
            ResponseFormat = responseFormat,
            ReasoningEffort = "low"
        };

        var parameters = _engine.BuildCompletionParameters(
            agent,
            modelId,
            threadId);

        parameters.Temperature.Should().Be(0.2f);
        parameters.TopP.Should().Be(0.9f);
        parameters.TopK.Should().Be(40);
        parameters.FrequencyPenalty.Should().Be(0.1f);
        parameters.PresencePenalty.Should().Be(0.3f);
        parameters.Stop.Should().Equal("done");
        parameters.Seed.Should().Be(7);
        parameters.ResponseFormat.Should().Be(responseFormat);
        parameters.ReasoningEffort.Should().Be("low");
        parameters.ModelId.Should().Be(modelId);
        parameters.ThreadId.Should().Be(threadId);
    }
}
