using FluentAssertions;
using SharpClaw.Contracts.Entities.Core.Messages;
using SharpClaw.Core.Chat;

namespace SharpClaw.Tests.Core;

[TestFixture]
public sealed class ChatCostEngineTests
{
    private readonly ChatCostEngine _engine = new();

    [Test]
    public void BuildChannelCost_GroupsAgentUsageAndOrdersByTotalTokens()
    {
        var channelId = Guid.NewGuid();
        var smallAgentId = Guid.NewGuid();
        var largeAgentId = Guid.NewGuid();
        var messages = new[]
        {
            Message(channelId, largeAgentId, "large", prompt: 8, completion: 2),
            Message(channelId, smallAgentId, "small", prompt: 1, completion: 1),
            Message(channelId, largeAgentId, "large", prompt: 4, completion: null),
            new ChatMessageDB
            {
                Role = "user",
                Content = "ignored",
                ChannelId = channelId,
                SenderAgentId = null,
                PromptTokens = 100,
                CompletionTokens = 100
            }
        };

        var response = _engine.BuildChannelCost(channelId, messages);

        response.ChannelId.Should().Be(channelId);
        response.TotalPromptTokens.Should().Be(13);
        response.TotalCompletionTokens.Should().Be(3);
        response.TotalTokens.Should().Be(16);
        response.AgentBreakdown.Select(item => item.AgentId)
            .Should().Equal(largeAgentId, smallAgentId);
        response.AgentBreakdown[0].PromptTokens.Should().Be(12);
        response.AgentBreakdown[0].CompletionTokens.Should().Be(2);
        response.AgentBreakdown[1].PromptTokens.Should().Be(1);
        response.AgentBreakdown[1].CompletionTokens.Should().Be(1);
    }

    [Test]
    public void BuildThreadCost_UsesSuppliedThreadAndChannelIds()
    {
        var channelId = Guid.NewGuid();
        var threadId = Guid.NewGuid();
        var agentId = Guid.NewGuid();

        var response = _engine.BuildThreadCost(
            channelId,
            threadId,
            [Message(channelId, agentId, "agent", prompt: 5, completion: 7)]);

        response.ThreadId.Should().Be(threadId);
        response.ChannelId.Should().Be(channelId);
        response.TotalPromptTokens.Should().Be(5);
        response.TotalCompletionTokens.Should().Be(7);
        response.TotalTokens.Should().Be(12);
        response.AgentBreakdown.Should().ContainSingle()
            .Which.AgentName.Should().Be("agent");
    }

    [Test]
    public void BuildAgentCost_GroupsByChannelAndOrdersByTotalTokens()
    {
        var agentId = Guid.NewGuid();
        var smallChannelId = Guid.NewGuid();
        var largeChannelId = Guid.NewGuid();
        var messages = new[]
        {
            Message(largeChannelId, agentId, "agent", prompt: 10, completion: 3),
            Message(smallChannelId, agentId, "agent", prompt: 1, completion: 1),
            new ChatMessageDB
            {
                Role = "assistant",
                Content = "ignored",
                ChannelId = largeChannelId,
                SenderAgentId = agentId,
                SenderAgentName = "agent",
                PromptTokens = null,
                CompletionTokens = 100
            }
        };

        var response = _engine.BuildAgentCost(agentId, "agent", messages);

        response.AgentId.Should().Be(agentId);
        response.AgentName.Should().Be("agent");
        response.TotalPromptTokens.Should().Be(11);
        response.TotalCompletionTokens.Should().Be(4);
        response.TotalTokens.Should().Be(15);
        response.ChannelBreakdown.Select(item => item.ChannelId)
            .Should().Equal(largeChannelId, smallChannelId);
    }

    private static ChatMessageDB Message(
        Guid channelId,
        Guid agentId,
        string agentName,
        int prompt,
        int? completion)
    {
        return new ChatMessageDB
        {
            Role = "assistant",
            Content = "message",
            ChannelId = channelId,
            SenderAgentId = agentId,
            SenderAgentName = agentName,
            PromptTokens = prompt,
            CompletionTokens = completion
        };
    }
}
