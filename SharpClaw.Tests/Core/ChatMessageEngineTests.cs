using SharpClaw.Contracts.Chat;
using SharpClaw.Contracts.DTOs.Chat;
using SharpClaw.Contracts.Entities.Core;
using SharpClaw.Contracts.Entities.Core.Clearance;
using SharpClaw.Contracts.Entities.Core.Messages;
using SharpClaw.Contracts.Enums;

namespace SharpClaw.Tests.Core;

[TestFixture]
public sealed class ChatMessageEngineTests
{
    private readonly ChatMessageEngine _engine = new();

    [Test]
    public void CreateUserMessage_CapturesUserSenderAndRequestClient()
    {
        var channelId = Guid.NewGuid();
        var threadId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var roleId = Guid.NewGuid();

        var message = _engine.CreateUserMessage(
            channelId,
            threadId,
            new ChatRequest("hello", ClientType: "cli"),
            userId,
            "marko",
            roleId,
            "Admin");

        message.Role.Should().Be(ChatRoles.User);
        message.Origin.Should().Be(MessageOrigin.User);
        message.Content.Should().Be("hello");
        message.ChannelId.Should().Be(channelId);
        message.ThreadId.Should().Be(threadId);
        message.SenderUserId.Should().Be(userId);
        message.SenderUsername.Should().Be("marko");
        message.PermissionRoleId.Should().Be(roleId);
        message.PermissionRoleName.Should().Be("Admin");
        message.ClientType.Should().Be("cli");
    }

    [Test]
    public void CreateAssistantMessage_CapturesAgentSenderAndPositiveTokensOnly()
    {
        var roleId = Guid.NewGuid();
        var agent = new AgentDB
        {
            Id = Guid.NewGuid(),
            Name = "agent",
            ModelId = Guid.NewGuid(),
            RoleId = roleId,
            Role = new RoleDB { Id = roleId, Name = "Worker" }
        };

        var message = _engine.CreateAssistantMessage(
            Guid.NewGuid(),
            null,
            new ChatRequest("prompt", ClientType: "web"),
            agent,
            "answer",
            promptTokens: 0,
            completionTokens: 12,
            providerMetadataJson: "{\"id\":\"provider\"}");

        message.Role.Should().Be(ChatRoles.Assistant);
        message.Origin.Should().Be(MessageOrigin.Assistant);
        message.SenderAgentId.Should().Be(agent.Id);
        message.SenderAgentName.Should().Be("agent");
        message.PermissionRoleId.Should().Be(roleId);
        message.PermissionRoleName.Should().Be("Worker");
        message.ClientType.Should().Be("web");
        message.PromptTokens.Should().BeNull();
        message.CompletionTokens.Should().Be(12);
        message.ProviderMetadataJson.Should().Be("{\"id\":\"provider\"}");
    }

    [Test]
    public void CreateSystemErrorMessage_UsesSystemRoleAndVisibleErrorText()
    {
        var message = _engine.CreateSystemErrorMessage(
            Guid.NewGuid(),
            Guid.NewGuid(),
            new ChatRequest("prompt", ClientType: "sse"),
            "boom");

        message.Role.Should().Be(ChatRoles.System);
        message.Origin.Should().Be(MessageOrigin.System);
        message.Content.Should().Be("⚠ Error: boom");
        message.ClientType.Should().Be("sse");
    }

    [Test]
    public void ToOrderedHistoryResponses_OrdersByCreatedAtThenIdAndProjectsSenders()
    {
        var firstId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var secondId = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var later = new ChatMessageDB
        {
            Id = secondId,
            CreatedAt = new DateTimeOffset(2026, 6, 30, 12, 1, 0, TimeSpan.Zero),
            Role = ChatRoles.Assistant,
            Origin = MessageOrigin.Assistant,
            Content = "later",
            ChannelId = Guid.NewGuid(),
            SenderAgentId = Guid.NewGuid(),
            SenderAgentName = "agent",
            ClientType = "api"
        };
        var firstTie = new ChatMessageDB
        {
            Id = firstId,
            CreatedAt = new DateTimeOffset(2026, 6, 30, 12, 0, 0, TimeSpan.Zero),
            Role = ChatRoles.User,
            Origin = MessageOrigin.User,
            Content = "first",
            ChannelId = Guid.NewGuid(),
            SenderUsername = "user",
            ClientType = "cli"
        };
        var secondTie = new ChatMessageDB
        {
            Id = secondId,
            CreatedAt = firstTie.CreatedAt,
            Role = ChatRoles.User,
            Origin = MessageOrigin.User,
            Content = "second",
            ChannelId = Guid.NewGuid(),
            SenderUsername = "user",
            ClientType = "cli"
        };

        var responses = _engine.ToOrderedHistoryResponses(
            [later, secondTie, firstTie]);

        responses.Select(response => response.Content)
            .Should().Equal("first", "second", "later");
        responses[0].SenderUsername.Should().Be("user");
        responses[2].SenderAgentName.Should().Be("agent");
        responses[2].ClientType.Should().Be("api");
    }
}
