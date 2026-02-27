using Microsoft.EntityFrameworkCore;
using SharpClaw.Application.Infrastructure.Models.Context;
using SharpClaw.Contracts.DTOs.DefaultResources;
using SharpClaw.Infrastructure.Persistence;

namespace SharpClaw.Application.Services;

/// <summary>
/// Manages <see cref="DefaultResourceSetDB"/> entities attached to
/// channels and contexts.
/// </summary>
public sealed class DefaultResourceSetService(SharpClawDbContext db)
{
    /// <summary>
    /// Gets the default resources for a channel.  Falls through to the
    /// context's set for any unset fields.
    /// </summary>
    public async Task<DefaultResourcesResponse?> GetForChannelAsync(
        Guid channelId, CancellationToken ct = default)
    {
        var ch = await db.Channels
            .Include(c => c.DefaultResourceSet)
            .Include(c => c.AgentContext).ThenInclude(ctx => ctx!.DefaultResourceSet)
            .FirstOrDefaultAsync(c => c.Id == channelId, ct);

        if (ch is null) return null;

        var chDrs = ch.DefaultResourceSet;
        var ctxDrs = ch.AgentContext?.DefaultResourceSet;

        return Merge(chDrs, ctxDrs);
    }

    /// <summary>
    /// Gets the default resources for a context.
    /// </summary>
    public async Task<DefaultResourcesResponse?> GetForContextAsync(
        Guid contextId, CancellationToken ct = default)
    {
        var ctx = await db.AgentContexts
            .Include(c => c.DefaultResourceSet)
            .FirstOrDefaultAsync(c => c.Id == contextId, ct);

        if (ctx is null) return null;

        return ctx.DefaultResourceSet is { } drs
            ? ToResponse(drs)
            : EmptyResponse(Guid.Empty);
    }

    /// <summary>
    /// Sets the default resources for a channel (creates or replaces).
    /// </summary>
    public async Task<DefaultResourcesResponse?> SetForChannelAsync(
        Guid channelId, SetDefaultResourcesRequest request,
        CancellationToken ct = default)
    {
        var ch = await db.Channels
            .Include(c => c.DefaultResourceSet)
            .FirstOrDefaultAsync(c => c.Id == channelId, ct);

        if (ch is null) return null;

        if (ch.DefaultResourceSet is { } existing)
        {
            Apply(existing, request);
        }
        else
        {
            var drs = new DefaultResourceSetDB();
            Apply(drs, request);
            db.DefaultResourceSets.Add(drs);
            await db.SaveChangesAsync(ct);
            ch.DefaultResourceSetId = drs.Id;
        }

        await db.SaveChangesAsync(ct);
        return ToResponse(ch.DefaultResourceSet!);
    }

    /// <summary>
    /// Sets the default resources for a context (creates or replaces).
    /// </summary>
    public async Task<DefaultResourcesResponse?> SetForContextAsync(
        Guid contextId, SetDefaultResourcesRequest request,
        CancellationToken ct = default)
    {
        var ctx = await db.AgentContexts
            .Include(c => c.DefaultResourceSet)
            .FirstOrDefaultAsync(c => c.Id == contextId, ct);

        if (ctx is null) return null;

        if (ctx.DefaultResourceSet is { } existing)
        {
            Apply(existing, request);
        }
        else
        {
            var drs = new DefaultResourceSetDB();
            Apply(drs, request);
            db.DefaultResourceSets.Add(drs);
            await db.SaveChangesAsync(ct);
            ctx.DefaultResourceSetId = drs.Id;
        }

        await db.SaveChangesAsync(ct);
        return ToResponse(ctx.DefaultResourceSet!);
    }

    // ── Helpers ───────────────────────────────────────────────────

    private static void Apply(DefaultResourceSetDB drs, SetDefaultResourcesRequest r)
    {
        drs.DangerousShellResourceId = r.DangerousShellResourceId;
        drs.SafeShellResourceId = r.SafeShellResourceId;
        drs.ContainerResourceId = r.ContainerResourceId;
        drs.WebsiteResourceId = r.WebsiteResourceId;
        drs.SearchEngineResourceId = r.SearchEngineResourceId;
        drs.LocalInfoStoreResourceId = r.LocalInfoStoreResourceId;
        drs.ExternalInfoStoreResourceId = r.ExternalInfoStoreResourceId;
        drs.AudioDeviceResourceId = r.AudioDeviceResourceId;
        drs.AgentResourceId = r.AgentResourceId;
        drs.TaskResourceId = r.TaskResourceId;
        drs.SkillResourceId = r.SkillResourceId;
        drs.TranscriptionModelId = r.TranscriptionModelId;
    }

    private static DefaultResourcesResponse ToResponse(DefaultResourceSetDB drs) =>
        new(drs.Id,
            drs.DangerousShellResourceId,
            drs.SafeShellResourceId,
            drs.ContainerResourceId,
            drs.WebsiteResourceId,
            drs.SearchEngineResourceId,
            drs.LocalInfoStoreResourceId,
            drs.ExternalInfoStoreResourceId,
            drs.AudioDeviceResourceId,
            drs.AgentResourceId,
            drs.TaskResourceId,
            drs.SkillResourceId,
            drs.TranscriptionModelId);

    private static DefaultResourcesResponse EmptyResponse(Guid id) =>
        new(id, null, null, null, null, null, null, null, null, null, null, null, null);

    /// <summary>
    /// Merges channel and context default resource sets.  Channel values
    /// take precedence; context fills in any gaps.
    /// </summary>
    private static DefaultResourcesResponse Merge(
        DefaultResourceSetDB? ch, DefaultResourceSetDB? ctx)
    {
        if (ch is null && ctx is null)
            return EmptyResponse(Guid.Empty);

        if (ch is null) return ToResponse(ctx!);
        if (ctx is null) return ToResponse(ch);

        return new(ch.Id,
            ch.DangerousShellResourceId ?? ctx.DangerousShellResourceId,
            ch.SafeShellResourceId ?? ctx.SafeShellResourceId,
            ch.ContainerResourceId ?? ctx.ContainerResourceId,
            ch.WebsiteResourceId ?? ctx.WebsiteResourceId,
            ch.SearchEngineResourceId ?? ctx.SearchEngineResourceId,
            ch.LocalInfoStoreResourceId ?? ctx.LocalInfoStoreResourceId,
            ch.ExternalInfoStoreResourceId ?? ctx.ExternalInfoStoreResourceId,
            ch.AudioDeviceResourceId ?? ctx.AudioDeviceResourceId,
            ch.AgentResourceId ?? ctx.AgentResourceId,
            ch.TaskResourceId ?? ctx.TaskResourceId,
            ch.SkillResourceId ?? ctx.SkillResourceId,
            ch.TranscriptionModelId ?? ctx.TranscriptionModelId);
    }
}
