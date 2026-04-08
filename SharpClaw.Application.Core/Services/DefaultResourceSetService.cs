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

    // ── Per-key operations ──────────────────────────────────────────

    private static readonly HashSet<string> ValidKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "dangshell", "safeshell", "container", "website", "search",
        "internaldb", "externaldb", "inputaudio", "displaydevice",
        "agent", "task", "skill", "transcriptionmodel", "editor"
    };

    /// <summary>
    /// Validates a default-resource key name. Returns <see langword="false"/>
    /// if the key is not recognised.
    /// </summary>
    public static bool IsValidKey(string key) =>
        ValidKeys.Contains(key);

    /// <summary>
    /// Sets a single default resource by key for a channel.
    /// </summary>
    public async Task<DefaultResourcesResponse?> SetKeyForChannelAsync(
        Guid channelId, string key, Guid resourceId,
        CancellationToken ct = default)
    {
        var ch = await db.Channels
            .Include(c => c.DefaultResourceSet)
            .FirstOrDefaultAsync(c => c.Id == channelId, ct);
        if (ch is null) return null;

        if (ch.DefaultResourceSet is null)
        {
            var drs = new DefaultResourceSetDB();
            db.DefaultResourceSets.Add(drs);
            await db.SaveChangesAsync(ct);
            ch.DefaultResourceSetId = drs.Id;
            ch.DefaultResourceSet = drs;
        }

        ApplyKey(ch.DefaultResourceSet, key, resourceId);
        await db.SaveChangesAsync(ct);
        return ToResponse(ch.DefaultResourceSet);
    }

    /// <summary>
    /// Clears a single default resource by key for a channel.
    /// </summary>
    public async Task<DefaultResourcesResponse?> ClearKeyForChannelAsync(
        Guid channelId, string key, CancellationToken ct = default)
    {
        var ch = await db.Channels
            .Include(c => c.DefaultResourceSet)
            .FirstOrDefaultAsync(c => c.Id == channelId, ct);
        if (ch is null) return null;
        if (ch.DefaultResourceSet is null) return EmptyResponse(Guid.Empty);

        ApplyKey(ch.DefaultResourceSet, key, null);
        await db.SaveChangesAsync(ct);
        return ToResponse(ch.DefaultResourceSet);
    }

    /// <summary>
    /// Sets a single default resource by key for a context.
    /// </summary>
    public async Task<DefaultResourcesResponse?> SetKeyForContextAsync(
        Guid contextId, string key, Guid resourceId,
        CancellationToken ct = default)
    {
        var ctx = await db.AgentContexts
            .Include(c => c.DefaultResourceSet)
            .FirstOrDefaultAsync(c => c.Id == contextId, ct);
        if (ctx is null) return null;

        if (ctx.DefaultResourceSet is null)
        {
            var drs = new DefaultResourceSetDB();
            db.DefaultResourceSets.Add(drs);
            await db.SaveChangesAsync(ct);
            ctx.DefaultResourceSetId = drs.Id;
            ctx.DefaultResourceSet = drs;
        }

        ApplyKey(ctx.DefaultResourceSet, key, resourceId);
        await db.SaveChangesAsync(ct);
        return ToResponse(ctx.DefaultResourceSet);
    }

    /// <summary>
    /// Clears a single default resource by key for a context.
    /// </summary>
    public async Task<DefaultResourcesResponse?> ClearKeyForContextAsync(
        Guid contextId, string key, CancellationToken ct = default)
    {
        var ctx = await db.AgentContexts
            .Include(c => c.DefaultResourceSet)
            .FirstOrDefaultAsync(c => c.Id == contextId, ct);
        if (ctx is null) return null;
        if (ctx.DefaultResourceSet is null) return EmptyResponse(Guid.Empty);

        ApplyKey(ctx.DefaultResourceSet, key, null);
        await db.SaveChangesAsync(ct);
        return ToResponse(ctx.DefaultResourceSet);
    }

    private static void ApplyKey(DefaultResourceSetDB drs, string key, Guid? value)
    {
        switch (key.ToLowerInvariant())
        {
            case "dangshell": drs.DangerousShellResourceId = value; break;
            case "safeshell": drs.SafeShellResourceId = value; break;
            case "container": drs.ContainerResourceId = value; break;
            case "website": drs.WebsiteResourceId = value; break;
            case "search": drs.SearchEngineResourceId = value; break;
            case "internaldb": drs.InternalDatabaseResourceId = value; break;
            case "externaldb": drs.ExternalDatabaseResourceId = value; break;
            case "inputaudio": drs.InputAudioResourceId = value; break;
            case "displaydevice": drs.DisplayDeviceResourceId = value; break;
            case "agent": drs.AgentResourceId = value; break;
            case "task": drs.TaskResourceId = value; break;
            case "skill": drs.SkillResourceId = value; break;
            case "transcriptionmodel": drs.TranscriptionModelId = value; break;
            case "editor": drs.EditorSessionResourceId = value; break;
            case "document": drs.DocumentSessionResourceId = value; break;
            case "nativeapp": drs.NativeApplicationResourceId = value; break;
            default: throw new ArgumentException($"Unknown default resource key: {key}");
        }
    }

    // ── Helpers ───────────────────────────────────────────────────

    private static void Apply(DefaultResourceSetDB drs, SetDefaultResourcesRequest r)
    {
        drs.DangerousShellResourceId = r.DangerousShellResourceId;
        drs.SafeShellResourceId = r.SafeShellResourceId;
        drs.ContainerResourceId = r.ContainerResourceId;
        drs.WebsiteResourceId = r.WebsiteResourceId;
        drs.SearchEngineResourceId = r.SearchEngineResourceId;
        drs.InternalDatabaseResourceId = r.InternalDatabaseResourceId;
        drs.ExternalDatabaseResourceId = r.ExternalDatabaseResourceId;
        drs.InputAudioResourceId = r.InputAudioResourceId;
        drs.DisplayDeviceResourceId = r.DisplayDeviceResourceId;
        drs.AgentResourceId = r.AgentResourceId;
        drs.TaskResourceId = r.TaskResourceId;
        drs.SkillResourceId = r.SkillResourceId;
        drs.TranscriptionModelId = r.TranscriptionModelId;
        drs.EditorSessionResourceId = r.EditorSessionResourceId;
        drs.DocumentSessionResourceId = r.DocumentSessionResourceId;
        drs.NativeApplicationResourceId = r.NativeApplicationResourceId;
    }

    private static DefaultResourcesResponse ToResponse(DefaultResourceSetDB drs) =>
        new(drs.Id,
            drs.DangerousShellResourceId,
            drs.SafeShellResourceId,
            drs.ContainerResourceId,
            drs.WebsiteResourceId,
            drs.SearchEngineResourceId,
            drs.InternalDatabaseResourceId,
            drs.ExternalDatabaseResourceId,
            drs.InputAudioResourceId,
            drs.DisplayDeviceResourceId,
            drs.AgentResourceId,
            drs.TaskResourceId,
            drs.SkillResourceId,
            drs.TranscriptionModelId,
            drs.EditorSessionResourceId,
            drs.DocumentSessionResourceId,
            drs.NativeApplicationResourceId);

    private static DefaultResourcesResponse EmptyResponse(Guid id) =>
        new(id, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null);

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
            ch.InternalDatabaseResourceId ?? ctx.InternalDatabaseResourceId,
            ch.ExternalDatabaseResourceId ?? ctx.ExternalDatabaseResourceId,
            ch.InputAudioResourceId ?? ctx.InputAudioResourceId,
            ch.DisplayDeviceResourceId ?? ctx.DisplayDeviceResourceId,
            ch.AgentResourceId ?? ctx.AgentResourceId,
            ch.TaskResourceId ?? ctx.TaskResourceId,
            ch.SkillResourceId ?? ctx.SkillResourceId,
            ch.TranscriptionModelId ?? ctx.TranscriptionModelId,
            ch.EditorSessionResourceId ?? ctx.EditorSessionResourceId,
            ch.DocumentSessionResourceId ?? ctx.DocumentSessionResourceId,
            ch.NativeApplicationResourceId ?? ctx.NativeApplicationResourceId);
    }
}
