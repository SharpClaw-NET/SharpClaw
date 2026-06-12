using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.Chat;
using SharpClaw.Contracts.DTOs.Chat;
using SharpClaw.Contracts.Entities.Core.Context;
using SharpClaw.Contracts.Entities.Core.Messages;
using SharpClaw.Contracts.Enums;
using SharpClaw.Contracts.Modules;
using SharpClaw.Modules.TestHarness;
using SharpClaw.Tests.TestHarness;

namespace SharpClaw.Tests.Modules;

[TestFixture]
public sealed class ConversationSteeringTests
{
    [Test]
    public async Task AddAsync_PersistsSystemMessageIntoThreadHistory()
    {
        await using var host = ChatHarnessHost.Create();
        var seeded = await host.SeedChatAsync(TestHarnessConstants.PlainProviderKey);
        var thread = await CreateThreadAsync(host, seeded.Channel.Id);
        var steering = host.Services.GetRequiredService<IConversationSteering>();

        var response = await steering.AddAsync(new ConversationSteeringRequest(
            seeded.Channel.Id,
            thread.Id,
            "The generated module failed build with CS1002 in Module.cs.",
            Source: "module_dev",
            Category: "module_build",
            Details: "Keep the next attempt scoped to the missing semicolon.",
            ClientType: "module-dev"));

        var message = await host.Db.ChatMessages.SingleAsync(row => row.Id == response.MessageId);
        message.Role.Should().Be(ChatRoles.System);
        message.Origin.Should().Be(MessageOrigin.System);
        message.ThreadId.Should().Be(thread.Id);
        message.ChannelId.Should().Be(seeded.Channel.Id);
        message.ClientType.Should().Be("module-dev");
        message.ProviderMetadataJson.Should().Contain("sharpclaw.conversation_steering");
        message.Content.Should().Contain("[SharpClaw conversation steering]");
        message.Content.Should().Contain("CS1002");

        var history = await host.Chat.GetHistoryAsync(seeded.Channel.Id, thread.Id);
        history.Should().Contain(row =>
            row.Role == ChatRoles.System
            && row.Content.Contains("CS1002", StringComparison.Ordinal));
    }

    [Test]
    public async Task ListAsync_ReturnsSteeringMessagesForRequestedThreadOnly()
    {
        await using var host = ChatHarnessHost.Create();
        var seeded = await host.SeedChatAsync(TestHarnessConstants.PlainProviderKey);
        var targetThread = await CreateThreadAsync(host, seeded.Channel.Id);
        var otherThread = await CreateThreadAsync(host, seeded.Channel.Id);
        var steering = host.Services.GetRequiredService<IConversationSteering>();

        await steering.AddAsync(new ConversationSteeringRequest(
            seeded.Channel.Id,
            otherThread.Id,
            "Do not return this steering message.",
            Source: "module_dev"));
        await steering.AddAsync(new ConversationSteeringRequest(
            seeded.Channel.Id,
            targetThread.Id,
            "Retry the hot-load after fixing the manifest entrypoint.",
            Source: "module_dev",
            Category: "hot_load"));

        var results = await steering.ListAsync(seeded.Channel.Id, targetThread.Id);

        results.Should().ContainSingle();
        results[0].ThreadId.Should().Be(targetThread.Id);
        results[0].Source.Should().Be("module_dev");
        results[0].Category.Should().Be("hot_load");
        results[0].Content.Should().Contain("manifest entrypoint");
    }

    private static async Task<ChatThreadDB> CreateThreadAsync(
        ChatHarnessHost host,
        Guid channelId)
    {
        var now = DateTimeOffset.UtcNow;
        var thread = new ChatThreadDB
        {
            Id = Guid.NewGuid(),
            ChannelId = channelId,
            Name = "Agent Work",
            CreatedAt = now,
            UpdatedAt = now,
        };

        host.Db.ChatThreads.Add(thread);
        await host.Db.SaveChangesAsync();
        return thread;
    }
}
