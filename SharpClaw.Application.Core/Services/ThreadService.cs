using Microsoft.EntityFrameworkCore;
using SharpClaw.Application.Infrastructure.Models.Context;
using SharpClaw.Contracts.DTOs.Threads;
using SharpClaw.Infrastructure.Persistence;

namespace SharpClaw.Application.Services;

public sealed class ThreadService(SharpClawDbContext db)
{
    public async Task<ThreadResponse> CreateAsync(
        Guid channelId, CreateThreadRequest request, CancellationToken ct = default)
    {
        var channel = await db.Channels.FindAsync([channelId], ct)
            ?? throw new ArgumentException($"Channel {channelId} not found.");

        var thread = new ChatThreadDB
        {
            Name = request.Name ?? $"Thread {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm}",
            MaxMessages = request.MaxMessages,
            MaxCharacters = request.MaxCharacters,
            ChannelId = channel.Id,
            CustomId = request.CustomId,
        };

        db.ChatThreads.Add(thread);
        await db.SaveChangesAsync(ct);

        return ToResponse(thread);
    }

    public async Task<ThreadResponse?> GetByIdAsync(
        Guid threadId, CancellationToken ct = default)
    {
        var thread = await db.ChatThreads.FindAsync([threadId], ct);
        return thread is not null ? ToResponse(thread) : null;
    }

    public async Task<IReadOnlyList<ThreadResponse>> ListAsync(
        Guid channelId, CancellationToken ct = default)
    {
        return await db.ChatThreads
            .Where(t => t.ChannelId == channelId)
            .OrderByDescending(t => t.UpdatedAt)
            .Select(t => new ThreadResponse(
                t.Id, t.Name, t.ChannelId,
                t.MaxMessages, t.MaxCharacters,
                t.CreatedAt, t.UpdatedAt, t.CustomId))
            .ToListAsync(ct);
    }

    public async Task<ThreadResponse?> UpdateAsync(
        Guid threadId, UpdateThreadRequest request, CancellationToken ct = default)
    {
        var thread = await db.ChatThreads.FindAsync([threadId], ct);
        if (thread is null) return null;

        if (request.Name is not null)
            thread.Name = request.Name;
        if (request.MaxMessages is not null)
            thread.MaxMessages = request.MaxMessages.Value == 0 ? null : request.MaxMessages;
        if (request.MaxCharacters is not null)
            thread.MaxCharacters = request.MaxCharacters.Value == 0 ? null : request.MaxCharacters;
        if (request.CustomId is not null)
            thread.CustomId = request.CustomId;

        await db.SaveChangesAsync(ct);
        return ToResponse(thread);
    }

    public async Task<bool> DeleteAsync(Guid threadId, CancellationToken ct = default)
    {
        var thread = await db.ChatThreads.FindAsync([threadId], ct);
        if (thread is null) return false;

        db.ChatThreads.Remove(thread);
        await db.SaveChangesAsync(ct);
        return true;
    }

    private static ThreadResponse ToResponse(ChatThreadDB thread) =>
        new(thread.Id, thread.Name, thread.ChannelId,
            thread.MaxMessages, thread.MaxCharacters,
            thread.CreatedAt, thread.UpdatedAt, thread.CustomId);
}
