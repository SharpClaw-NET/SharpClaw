using SharpClaw.Contracts.DTOs.Channels;
using SharpClaw.Contracts.DTOs.Contexts;
using SharpClaw.Contracts.DTOs.Threads;
using SharpClaw.Contracts.Entities.Core;
using SharpClaw.Contracts.Entities.Core.Context;
using SharpClaw.Core.Conversation;

namespace SharpClaw.Tests.Core;

[TestFixture]
public sealed class ConversationTopologyEngineTests
{
    private readonly ConversationTopologyEngine _engine = new();

    [Test]
    public void ToChannelResponse_WhenChannelHasNoAgentOrAllowedAgents_UsesContextFallbacks()
    {
        var contextAgent = Agent("Context agent");
        var allowedAgent = Agent("Allowed agent");
        var permissionSetId = Guid.NewGuid();

        var context = new ChannelContextDB
        {
            Id = Guid.NewGuid(),
            Name = "Clinical",
            AgentId = contextAgent.Id,
            Agent = contextAgent,
            PermissionSetId = permissionSetId
        };
        context.AllowedAgents.Add(allowedAgent);

        var channel = new ChannelDB
        {
            Id = Guid.NewGuid(),
            Title = "Intake",
            AgentContextId = context.Id,
            AgentContext = context
        };

        var response = _engine.ToChannelResponse(channel);

        response.Agent!.Id.Should().Be(contextAgent.Id);
        response.ContextId.Should().Be(context.Id);
        response.EffectivePermissionSetId.Should().Be(permissionSetId);
        response.AllowedAgents.Select(agent => agent.Id)
            .Should().ContainSingle().Which.Should().Be(allowedAgent.Id);
    }

    [Test]
    public void ApplyChannelUpdate_WhenSentinelValuesProvided_ClearsReferences()
    {
        var channel = new ChannelDB
        {
            Title = "Before",
            AgentContextId = Guid.NewGuid(),
            AgentContext = new ChannelContextDB
            {
                Name = "Old context",
                AgentId = Guid.NewGuid(),
                Agent = Agent("Old agent")
            },
            PermissionSetId = Guid.NewGuid(),
            ToolAwarenessSetId = Guid.NewGuid(),
            CustomChatHeader = "header"
        };

        _engine.ApplyChannelUpdate(
            channel,
            new UpdateChannelRequest(
                ContextId: Guid.Empty,
                PermissionSetId: Guid.Empty,
                CustomChatHeader: "",
                ToolAwarenessSetId: Guid.Empty),
            context: null,
            replacementAllowedAgents: null);

        channel.AgentContextId.Should().BeNull();
        channel.AgentContext.Should().BeNull();
        channel.PermissionSetId.Should().BeNull();
        channel.ToolAwarenessSetId.Should().BeNull();
        channel.CustomChatHeader.Should().BeNull();
    }

    [Test]
    public void ApplyThreadUpdate_WhenLimitsAreZero_ResetsToInheritedDefaults()
    {
        var thread = new ChatThreadDB
        {
            Name = "Thread",
            ChannelId = Guid.NewGuid(),
            MaxMessages = 25,
            MaxCharacters = 1000
        };

        _engine.ApplyThreadUpdate(
            thread,
            new UpdateThreadRequest(MaxMessages: 0, MaxCharacters: 0));

        thread.MaxMessages.Should().BeNull();
        thread.MaxCharacters.Should().BeNull();
    }

    [Test]
    public void AddChannelAllowedAgent_WhenDuplicate_ReturnsFalse()
    {
        var agent = Agent("Existing");
        var channel = new ChannelDB { Title = "Channel" };
        channel.AllowedAgents.Add(agent);

        var changed = _engine.AddChannelAllowedAgent(channel, agent);

        changed.Should().BeFalse();
        channel.AllowedAgents.Should().ContainSingle();
    }

    [Test]
    public void EnsureContextNameAvailable_WhenDuplicateAfterTrimAndCase_Throws()
    {
        var act = () => _engine.EnsureContextNameAvailable(
            " Clinical ",
            ["clinical"]);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("A context named ' Clinical ' already exists.");
    }

    private static AgentDB Agent(string name)
    {
        var provider = new ProviderDB
        {
            Id = Guid.NewGuid(),
            Name = "Provider",
            ProviderKey = "provider"
        };

        var model = new ModelDB
        {
            Id = Guid.NewGuid(),
            Name = "Model",
            ProviderId = provider.Id,
            Provider = provider
        };

        return new AgentDB
        {
            Id = Guid.NewGuid(),
            Name = name,
            ModelId = model.Id,
            Model = model
        };
    }
}
