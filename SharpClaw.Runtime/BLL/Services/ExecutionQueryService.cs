using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using SharpClaw.Contracts.DTOs.AgentActions;
using SharpClaw.Contracts.DTOs.Diagnostics;
using SharpClaw.Runtime.INF.Persistence.Entities.Core.Jobs;
using SharpClaw.Contracts.Enums;
using SharpClaw.Runtime.INF.DurableStorage;
using SharpClaw.Runtime.INF.Persistence;

namespace SharpClaw.Runtime.BLL.Services;

/// <summary>
/// Bounded relational query surface for execution state and compact audit
/// metadata. Diagnostic payload retrieval remains in the durable store.
/// </summary>
public sealed class ExecutionQueryService(
    SharpClawDbContext db,
    DurableExecutionPersistence persistence,
    DatabaseCursorCodec cursors)
{
    private const int DefaultTake = 50;
    private const int MaximumTake = 200;

    public async Task<AgentJobDetailResponse?> GetJobAsync(
        Guid jobId,
        CancellationToken cancellationToken = default)
    {
        var job = await db.AgentJobs.FindAsync(
            [jobId],
            cancellationToken);
        return job is null ? null : persistence.ToJobDetail(job);
    }

    public async Task<AgentJobSummaryResponse?> GetJobSummaryAsync(
        Guid jobId,
        CancellationToken cancellationToken = default)
    {
        return await db.AgentJobs
            .Where(job => job.Id == jobId)
            .Select(job => ToJobSummary(job))
            .FirstOrDefaultAsync(cancellationToken);
    }

    public Task<AgentJobSummaryPageResponse> ListChannelJobsAsync(
        Guid channelId,
        string? cursor,
        int take = DefaultTake,
        CancellationToken cancellationToken = default)
    {
        return ReadJobPageAsync(
            db.AgentJobs.Where(job => job.ChannelId == channelId),
            $"jobs:channel:{channelId:D}",
            cursor,
            take,
            cancellationToken);
    }

    public Task<AgentJobSummaryPageResponse> ListJobsByActionPrefixAsync(
        string actionKeyPrefix,
        Guid? resourceId,
        string? cursor,
        int take = DefaultTake,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actionKeyPrefix);
        var query = db.AgentJobs.Where(job =>
            job.ActionKey != null
            && job.ActionKey.StartsWith(actionKeyPrefix)
            && (resourceId == null || job.ResourceId == resourceId));
        var fingerprint = Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(
                    $"{actionKeyPrefix}\n{resourceId?.ToString("D")}")))
            .ToLowerInvariant();
        return ReadJobPageAsync(
            query,
            $"jobs:prefix:{fingerprint}",
            cursor,
            take,
            cancellationToken);
    }

    public async Task<ExecutionAuditPageResponse> ReadAuditAsync(
        ExecutionOwnerKind ownerKind,
        Guid ownerId,
        string? cursor,
        int take = DefaultTake,
        CancellationToken cancellationToken = default)
    {
        var boundedTake = BoundTake(take);
        var scope = $"audit:{ownerKind}:{ownerId:D}";
        var query = db.ExecutionAuditEvents.Where(audit =>
            audit.OwnerKind == ownerKind && audit.OwnerId == ownerId);
        if (!string.IsNullOrWhiteSpace(cursor))
        {
            var decoded = cursors.Decode(cursor, scope);
            query = query.Where(audit =>
                audit.CreatedAt < decoded.CreatedAt
                || (audit.CreatedAt == decoded.CreatedAt
                    && audit.Id.CompareTo(decoded.Id) < 0));
        }

        var loaded = await query
            .OrderByDescending(audit => audit.CreatedAt)
            .ThenByDescending(audit => audit.Id)
            .Take(boundedTake + 1)
            .ToListAsync(cancellationToken);
        var hasMore = loaded.Count > boundedTake;
        if (hasMore)
            loaded.RemoveAt(loaded.Count - 1);
        var nextCursor = hasMore && loaded.Count > 0
            ? cursors.Encode(
                scope,
                loaded[^1].CreatedAt,
                loaded[^1].Id)
            : null;
        return new ExecutionAuditPageResponse(
            loaded.Select(audit => new ExecutionAuditEventResponse(
                audit.Id,
                audit.OwnerKind,
                audit.OwnerId,
                audit.EventKind,
                audit.PreviousState,
                audit.NewState,
                audit.ActorKind,
                audit.ActorId,
                audit.ReasonCode,
                audit.CreatedAt)).ToArray(),
            nextCursor,
            hasMore);
    }

    private async Task<AgentJobSummaryPageResponse> ReadJobPageAsync(
        IQueryable<AgentJobDB> query,
        string scope,
        string? cursor,
        int take,
        CancellationToken cancellationToken)
    {
        var boundedTake = BoundTake(take);
        query = ApplyCursor(query, scope, cursor);
        var loaded = await query
            .OrderByDescending(job => job.CreatedAt)
            .ThenByDescending(job => job.Id)
            .Take(boundedTake + 1)
            .Select(job => new JobSummaryRow(
                job.Id,
                job.ChannelId,
                job.AgentId,
                job.ActionKey,
                job.ResourceId,
                job.Status,
                job.CreatedAt,
                job.StartedAt,
                job.CompletedAt))
            .ToListAsync(cancellationToken);
        var hasMore = loaded.Count > boundedTake;
        if (hasMore)
            loaded.RemoveAt(loaded.Count - 1);
        var nextCursor = hasMore && loaded.Count > 0
            ? cursors.Encode(
                scope,
                loaded[^1].CreatedAt,
                loaded[^1].Id)
            : null;
        return new AgentJobSummaryPageResponse(
            loaded.Select(row => new AgentJobSummaryResponse(
                row.Id,
                row.ChannelId,
                row.AgentId,
                row.ActionKey,
                row.ResourceId,
                row.Status,
                row.CreatedAt,
                row.StartedAt,
                row.CompletedAt)).ToArray(),
            nextCursor,
            hasMore);
    }

    private IQueryable<TEntity> ApplyCursor<TEntity>(
        IQueryable<TEntity> query,
        string scope,
        string? cursor)
        where TEntity : SharpClaw.Contracts.Entities.BaseEntity
    {
        if (string.IsNullOrWhiteSpace(cursor))
            return query;
        var decoded = cursors.Decode(cursor, scope);
        return query.Where(entity =>
            entity.CreatedAt < decoded.CreatedAt
            || (entity.CreatedAt == decoded.CreatedAt
                && entity.Id.CompareTo(decoded.Id) < 0));
    }

    private static AgentJobSummaryResponse ToJobSummary(AgentJobDB job) =>
        new(
            job.Id,
            job.ChannelId,
            job.AgentId,
            job.ActionKey,
            job.ResourceId,
            job.Status,
            job.CreatedAt,
            job.StartedAt,
            job.CompletedAt);

    private static int BoundTake(int take) =>
        Math.Clamp(take <= 0 ? DefaultTake : take, 1, MaximumTake);

    private sealed record JobSummaryRow(
        Guid Id,
        Guid ChannelId,
        Guid AgentId,
        string? ActionKey,
        Guid? ResourceId,
        AgentJobStatus Status,
        DateTimeOffset CreatedAt,
        DateTimeOffset? StartedAt,
        DateTimeOffset? CompletedAt);
}
