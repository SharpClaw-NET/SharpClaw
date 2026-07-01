using Microsoft.EntityFrameworkCore;
using SharpClaw.Core.Conversation;
using SharpClaw.Contracts.Entities.Core.Context;
using SharpClaw.Contracts.DTOs.Threads;
using SharpClaw.Infrastructure.Persistence;

namespace SharpClaw.Application.Services;

public sealed class ThreadService(
    SharpClawDbContext db,
    ConversationTopologyEngine conversation,
    ChatRuntimeInvalidationPlanner invalidations,
    ChatCache chatCache)
{
    public async Task<ThreadResponse> CreateAsync(
        Guid channelId, CreateThreadRequest request, CancellationToken ct = default)
    {
        var channel = await db.Channels.FindAsync([channelId], ct)
            ?? throw new ArgumentException($"Channel {channelId} not found.");

        var thread = conversation.CreateThread(
            channel.Id,
            request,
            DateTimeOffset.UtcNow);

        db.ChatThreads.Add(thread);
        await db.SaveChangesAsync(ct);
        InvalidateThreadRuntimeState(thread.Id);

        return conversation.ToThreadResponse(thread);
    }

    public async Task<ThreadResponse?> GetByIdAsync(
        Guid threadId, CancellationToken ct = default)
    {
        var thread = await db.ChatThreads.FindAsync([threadId], ct);
        return thread is not null ? conversation.ToThreadResponse(thread) : null;
    }

    public async Task<IReadOnlyList<ThreadResponse>> ListAsync(
        Guid channelId, CancellationToken ct = default)
    {
        var threads = await db.ChatThreads
            .Where(t => t.ChannelId == channelId)
            .OrderByDescending(t => t.UpdatedAt)
            .ToListAsync(ct);

        return threads.Select(conversation.ToThreadResponse).ToList();
    }

    public async Task<ThreadResponse?> UpdateAsync(
        Guid threadId, UpdateThreadRequest request, CancellationToken ct = default)
    {
        var thread = await db.ChatThreads.FindAsync([threadId], ct);
        if (thread is null) return null;

        conversation.ApplyThreadUpdate(thread, request);

        await db.SaveChangesAsync(ct);
        InvalidateThreadRuntimeState(threadId);
        return conversation.ToThreadResponse(thread);
    }

    public async Task<bool> DeleteAsync(Guid threadId, CancellationToken ct = default)
    {
        var thread = await db.ChatThreads.FindAsync([threadId], ct);
        if (thread is null) return false;

        db.ChatThreads.Remove(thread);
        await db.SaveChangesAsync(ct);
        InvalidateThreadRuntimeState(threadId);
        return true;
    }

    private void InvalidateThreadRuntimeState(Guid threadId)
    {
        invalidations.ThreadChanged(threadId).ApplyTo(chatCache);
    }

}
