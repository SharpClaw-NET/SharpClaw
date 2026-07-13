using System.Text.Json;
using SharpClaw.Contracts.Entities.Core;
using SharpClaw.Contracts.Entities.Core.Context;
using SharpClaw.Contracts.Models;
using SharpClaw.Contracts.Providers;
using SharpClaw.Core.Chat;

namespace SharpClaw.Tests.Core;

[TestFixture]
public sealed class ChatRequestPlanningEngineTests
{
    [Test]
    public void BuildBufferedPlan_WhenProviderSupportsNativeTools_EnablesToolsAndUsesChannelAwareness()
    {
        var channelAwareness = new ToolAwarenessSetDB
        {
            Name = "Channel tools",
            Tools = new Dictionary<string, bool> { ["channel_tool"] = true }
        };
        var agentAwareness = new ToolAwarenessSetDB
        {
            Name = "Agent tools",
            Tools = new Dictionary<string, bool> { ["agent_tool"] = true }
        };
        var agent = CreateAgent(
            supportsNativeToolCalling: true,
            requiresApiKey: false);
        agent.ToolAwarenessSet = agentAwareness;
        agent.ProviderParameters = new Dictionary<string, JsonElement>
        {
            ["custom"] = JsonDocument.Parse("1").RootElement.Clone()
        };
        var channel = new ChannelDB
        {
            Title = "Channel",
            ToolAwarenessSet = channelAwareness
        };
        var planner = CreatePlanner(agent);

        var plan = planner.BuildBufferedPlan(
            channel,
            agent,
            threadId: Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            disableDefaultSystemPrompt: false,
            disableCustomProviderParameters: false,
            providerFacts: CreateProviderFacts(agent));

        plan.UseNativeTools.Should().BeTrue();
        plan.EnableTools.Should().BeTrue();
        plan.DisableTools.Should().BeFalse();
        plan.SystemPrompt.Should().Contain("Statuses:");
        plan.ToolAwareness.Should().BeSameAs(channelAwareness.Tools);
        plan.ProviderParameters.Should().BeSameAs(agent.ProviderParameters);
        plan.CompletionParameters.ThreadId.Should().Be(
            Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));
        plan.CompletionParameters.ModelId.Should().Be(agent.ModelId);
    }

    [Test]
    public void BuildBufferedPlan_WhenProviderDoesNotSupportNativeTools_DisablesCoreToolPrompt()
    {
        var agent = CreateAgent(
            supportsNativeToolCalling: false,
            requiresApiKey: false);
        var planner = CreatePlanner(agent);

        var plan = planner.BuildBufferedPlan(
            new ChannelDB { Title = "Channel" },
            agent,
            threadId: null,
            disableDefaultSystemPrompt: false,
            disableCustomProviderParameters: false,
            providerFacts: CreateProviderFacts(agent));

        plan.UseNativeTools.Should().BeFalse();
        plan.EnableTools.Should().BeFalse();
        plan.SystemPrompt.Should().Be(agent.SystemPrompt);
        plan.ToolAwareness.Should().BeNull();
    }

    [Test]
    public void BuildStreamingPlan_WhenProviderDoesNotSupportNativeTools_StillEnablesToolSchemas()
    {
        var agent = CreateAgent(
            supportsNativeToolCalling: false,
            requiresApiKey: false,
            capabilityTags: "chat,vision");
        var planner = CreatePlanner(agent);

        var plan = planner.BuildStreamingPlan(
            new ChannelDB { Title = "Channel" },
            agent,
            threadId: null,
            disableDefaultSystemPrompt: false,
            disableCustomProviderParameters: false,
            providerFacts: CreateProviderFacts(agent));

        plan.UseNativeTools.Should().BeFalse();
        plan.EnableTools.Should().BeTrue();
        plan.SupportsVision.Should().BeTrue();
        plan.SystemPrompt.Should().Contain("Statuses:");
    }

    [Test]
    public void BuildStreamingPlan_WhenChannelDisablesTools_RemovesToolAwarenessAndCoreToolPrompt()
    {
        var agent = CreateAgent(
            supportsNativeToolCalling: true,
            requiresApiKey: false);
        agent.ToolAwarenessSet = new ToolAwarenessSetDB
        {
            Name = "Agent tools",
            Tools = new Dictionary<string, bool> { ["agent_tool"] = true }
        };
        var planner = CreatePlanner(agent);

        var plan = planner.BuildStreamingPlan(
            new ChannelDB
            {
                Title = "Channel",
                DisableToolSchemas = true
            },
            agent,
            threadId: null,
            disableDefaultSystemPrompt: false,
            disableCustomProviderParameters: false,
            providerFacts: CreateProviderFacts(agent));

        plan.DisableTools.Should().BeTrue();
        plan.EnableTools.Should().BeFalse();
        plan.SystemPrompt.Should().Be(agent.SystemPrompt);
        plan.ToolAwareness.Should().BeNull();
    }

    [Test]
    public void BuildBufferedPlan_WhenCustomProviderParametersAreDisabled_ReturnsNullProviderParameters()
    {
        var agent = CreateAgent(
            supportsNativeToolCalling: true,
            requiresApiKey: false);
        agent.ProviderParameters = new Dictionary<string, JsonElement>
        {
            ["custom"] = JsonDocument.Parse("1").RootElement.Clone()
        };
        var planner = CreatePlanner(agent);

        var plan = planner.BuildBufferedPlan(
            new ChannelDB { Title = "Channel" },
            agent,
            threadId: null,
            disableDefaultSystemPrompt: false,
            disableCustomProviderParameters: true,
            providerFacts: CreateProviderFacts(agent));

        plan.ProviderParameters.Should().BeNull();
    }

    [Test]
    public void BuildBufferedPlan_WhenRequiredApiKeyIsMissing_Throws()
    {
        var agent = CreateAgent(
            supportsNativeToolCalling: true,
            requiresApiKey: true,
            encryptedApiKey: null);
        var planner = CreatePlanner(agent);

        var act = () => planner.BuildBufferedPlan(
            new ChannelDB { Title = "Channel" },
            agent,
            threadId: null,
            disableDefaultSystemPrompt: false,
            disableCustomProviderParameters: false,
            providerFacts: CreateProviderFacts(agent));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("Provider does not have an API key configured.");
    }

    private static ChatRequestPlanningEngine CreatePlanner(AgentDB agent)
    {
        _ = agent;
        return new ChatRequestPlanningEngine(new ChatPromptEngine());
    }

    private static ChatProviderPlanningFacts CreateProviderFacts(AgentDB agent)
    {
        var provider = agent.Model.Provider;
        var requiresApiKey = provider.ProviderKey.Contains(
            "requires-key",
            StringComparison.Ordinal);
        return new ChatProviderPlanningFacts(
            ProviderAccessSatisfied: !requiresApiKey
                || !string.IsNullOrEmpty(provider.EncryptedApiKey),
            SupportsNativeToolCalling: provider.ProviderKey.Contains(
                "native",
                StringComparison.Ordinal),
            ICompletionParameterSpec.Passthrough);
    }

    private static AgentDB CreateAgent(
        bool supportsNativeToolCalling,
        bool requiresApiKey,
        string capabilityTags = "chat",
        string? encryptedApiKey = "encrypted")
    {
        var providerKey = supportsNativeToolCalling ? "native-test" : "plain-test";
        if (requiresApiKey)
            providerKey += "-requires-key";
        var provider = new ProviderDB
        {
            Id = Guid.NewGuid(),
            Name = "Provider",
            ProviderKey = providerKey,
            EncryptedApiKey = requiresApiKey ? encryptedApiKey : null
        };
        var model = new ModelDB
        {
            Id = Guid.NewGuid(),
            Name = "model",
            ProviderId = provider.Id,
            Provider = provider,
            CapabilityTagsRaw = capabilityTags
        };

        return new AgentDB
        {
            Id = Guid.NewGuid(),
            Name = "Agent",
            SystemPrompt = "Agent prompt",
            ModelId = model.Id,
            Model = model,
            Temperature = 0.3f,
            MaxCompletionTokens = 512
        };
    }
}
