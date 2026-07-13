using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.Chat;
using SharpClaw.Contracts.DTOs.Chat;
using SharpClaw.Contracts.Entities.Core.Context;
using SharpClaw.Contracts.Entities.Core.Messages;
using SharpClaw.Contracts.Enums;
using SharpClaw.Contracts.Modules;
using SharpClaw.Core.Conversation;
using SharpClaw.Core.Threads;
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
    public async Task AddAsync_DefaultClientTypePublishesThreadActivityAfterSave()
    {
        await using var host = ChatHarnessHost.Create();
        var seeded = await host.SeedChatAsync(TestHarnessConstants.PlainProviderKey);
        var thread = await CreateThreadAsync(host, seeded.Channel.Id);
        var steering = host.Services.GetRequiredService<IConversationSteering>();
        var threadActivity = host.RootServices.GetRequiredService<ThreadActivitySignal>();
        using var subscription = threadActivity.Subscribe(thread.Id);

        var response = await steering.AddAsync(new ConversationSteeringRequest(
            seeded.Channel.Id,
            thread.Id,
            "Remember that the last build failed.",
            ClientType: " "));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var evt = await subscription.Reader.ReadAsync(cts.Token);
        evt.Type.Should().Be(ThreadActivityEventType.NewMessages);
        evt.ClientType.Should().Be(WellKnownClientKeys.Api);

        var message = await host.Db.ChatMessages.SingleAsync(row => row.Id == response.MessageId);
        message.ClientType.Should().Be(WellKnownClientKeys.Api);
        message.Content.Should().Contain("last build failed");
    }

    [Test]
    public async Task AddAsync_ChannelLevelSteeringDoesNotPublishThreadActivity()
    {
        await using var host = ChatHarnessHost.Create();
        var seeded = await host.SeedChatAsync(TestHarnessConstants.PlainProviderKey);
        var thread = await CreateThreadAsync(host, seeded.Channel.Id);
        var steering = host.Services.GetRequiredService<IConversationSteering>();
        var threadActivity = host.RootServices.GetRequiredService<ThreadActivitySignal>();
        using var subscription = threadActivity.Subscribe(thread.Id);

        var response = await steering.AddAsync(new ConversationSteeringRequest(
            seeded.Channel.Id,
            ThreadId: null,
            "Channel-level steering only."));

        var message = await host.Db.ChatMessages.SingleAsync(row => row.Id == response.MessageId);
        message.ThreadId.Should().BeNull();

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        try
        {
            var hasEvent = await subscription.Reader.WaitToReadAsync(cts.Token);
            hasEvent.Should().BeFalse();
        }
        catch (OperationCanceledException)
        {
        }
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

    [Test]
    public async Task AddAsync_RejectsThreadFromDifferentChannel()
    {
        await using var host = ChatHarnessHost.Create();
        var seeded = await host.SeedChatAsync(TestHarnessConstants.PlainProviderKey);
        var thread = await CreateThreadAsync(host, seeded.Channel.Id);
        var steering = host.Services.GetRequiredService<IConversationSteering>();
        var wrongChannelId = Guid.NewGuid();

        var act = async () => await steering.AddAsync(new ConversationSteeringRequest(
            wrongChannelId,
            thread.Id,
            "This should not be stored.",
            Source: "module_dev"));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage($"*belongs to channel '{seeded.Channel.Id}'*not '{wrongChannelId}'*");
        host.Db.ChatMessages.Should().BeEmpty();
    }

    [Test]
    public async Task AddAsync_RejectsMissingChannelAndMissingThread()
    {
        await using var host = ChatHarnessHost.Create();
        var seeded = await host.SeedChatAsync(TestHarnessConstants.PlainProviderKey);
        var steering = host.Services.GetRequiredService<IConversationSteering>();
        var missingChannelId = Guid.NewGuid();
        var missingThreadId = Guid.NewGuid();

        var missingChannel = async () => await steering.AddAsync(new ConversationSteeringRequest(
            missingChannelId,
            ThreadId: null,
            "This should not be stored."));
        var missingThread = async () => await steering.AddAsync(new ConversationSteeringRequest(
            seeded.Channel.Id,
            missingThreadId,
            "This should not be stored."));

        await missingChannel.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage($"Channel '{missingChannelId}' was not found.");
        await missingThread.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage($"Thread '{missingThreadId}' was not found.");
        host.Db.ChatMessages.Should().BeEmpty();
    }

    [Test]
    public async Task ListAsync_PerformanceShape_ClampsLimitToNewestOneHundredSteeringMessages()
    {
        await using var host = ChatHarnessHost.Create();
        var seeded = await host.SeedChatAsync(TestHarnessConstants.PlainProviderKey);
        var thread = await CreateThreadAsync(host, seeded.Channel.Id);
        var steering = host.Services.GetRequiredService<IConversationSteering>();

        for (var i = 0; i < 105; i++)
        {
            await steering.AddAsync(new ConversationSteeringRequest(
                seeded.Channel.Id,
                thread.Id,
                $"Steering message {i:D3}.",
                Source: "module_dev",
                Category: "load"));
        }

        var baseTimestamp = DateTimeOffset.Parse("2026-07-02T18:30:00Z");
        var rows = await host.Db.ChatMessages
            .Where(message =>
                message.ChannelId == seeded.Channel.Id
                && message.ThreadId == thread.Id
                && message.Content.StartsWith(ConversationSteeringEngine.ContentPrefix))
            .ToListAsync();
        foreach (var row in rows)
        {
            var marker = row.Content.Split("Steering message ", StringSplitOptions.None)[1][..3];
            row.CreatedAt = baseTimestamp.AddMinutes(int.Parse(marker));
        }
        await host.Db.SaveChangesAsync();

        var results = await steering.ListAsync(seeded.Channel.Id, thread.Id, limit: 500);

        results.Should().HaveCount(100);
        results.Should().OnlyContain(row => row.ThreadId == thread.Id);
        results.Should().OnlyContain(row => row.Source == "module_dev");
        results.Should().OnlyContain(row => row.Category == "load");
        results[0].Content.Should().Contain("Steering message 005.");
        results[^1].Content.Should().Contain("Steering message 104.");
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
