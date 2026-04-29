using Microsoft.EntityFrameworkCore;

using SharpClaw.Contracts.DTOs.AgentActions;
using SharpClaw.Contracts.DTOs.Chat;
using SharpClaw.Contracts.Modules;
using SharpClaw.Modules.Transcription.DTOs;
using SharpClaw.Modules.Transcription.Models;

namespace SharpClaw.Modules.Transcription.Services;

/// <summary>
/// Manages transcription job queries.
/// Job lifecycle (submit, approve, cancel, pause, resume) is handled
/// by <see cref="SharpClaw.Application.Services.AgentJobService"/> via the
/// job/permission system.  This service owns the transcription-specific
/// DTO mapping so the core stays free of transcription knowledge.
/// </summary>
public sealed class TranscriptionService(
    TranscriptionDbContext db,
    IAgentJobReader jobReader)
{
    // ═══════════════════════════════════════════════════════════════
    // Transcription job queries
    // ═══════════════════════════════════════════════════════════════

    private const string TranscriptionActionPrefix = "transcribe_from_audio";

    /// <summary>
    /// Retrieves a single transcription job by ID, enriched with module-owned
    /// transcription parameters and segments.
    /// Returns <see langword="null"/> if the job does not exist or is not a
    /// transcription job.
    /// </summary>
    public async Task<TranscriptionJobResponse?> GetTranscriptionJobAsync(
        Guid jobId, CancellationToken ct = default)
    {
        var job = await jobReader.GetJobAsync(jobId, ct);
        if (job is null || !IsTranscriptionAction(job.ActionKey))
            return null;

        var txJob = await db.TranscriptionJobs
            .FirstOrDefaultAsync(t => t.AgentJobId == jobId, ct);

        var segments = await db.TranscriptionSegments
            .Where(s => s.AgentJobId == jobId)
            .OrderBy(s => s.StartTime)
            .ToListAsync(ct);

        return ToTranscriptionResponse(job, txJob, segments);
    }

    /// <summary>
    /// Lists all transcription jobs, optionally filtered by input audio device.
    /// </summary>
    public async Task<IReadOnlyList<TranscriptionJobResponse>> ListTranscriptionJobsAsync(
        Guid? inputAudioId = null, CancellationToken ct = default)
    {
        var jobs = await jobReader.ListJobsByActionPrefixAsync(
            TranscriptionActionPrefix, inputAudioId, ct);

        var jobIds = jobs.Select(j => j.Id).ToList();
        var txJobs = await db.TranscriptionJobs
            .Where(t => jobIds.Contains(t.AgentJobId))
            .ToDictionaryAsync(t => t.AgentJobId, ct);

        var segments = await db.TranscriptionSegments
            .Where(s => jobIds.Contains(s.AgentJobId))
            .OrderBy(s => s.StartTime)
            .ToListAsync(ct);

        var segmentsByJob = segments.GroupBy(s => s.AgentJobId)
            .ToDictionary(g => g.Key, g => g.ToList());

        return jobs
            .Select(j => ToTranscriptionResponse(
                j,
                txJobs.GetValueOrDefault(j.Id),
                segmentsByJob.GetValueOrDefault(j.Id) ?? []))
            .ToList();
    }

    /// <summary>
    /// Lists lightweight transcription job summaries — no segments or heavy payloads.
    /// </summary>
    public async Task<IReadOnlyList<TranscriptionJobSummaryResponse>> ListTranscriptionJobSummariesAsync(
        Guid? inputAudioId = null, CancellationToken ct = default)
    {
        var summaries = await jobReader.ListJobSummariesByActionPrefixAsync(
            TranscriptionActionPrefix, inputAudioId, ct);

        var jobIds = summaries.Select(s => s.Id).ToList();
        var txJobs = await db.TranscriptionJobs
            .Where(t => jobIds.Contains(t.AgentJobId))
            .ToDictionaryAsync(t => t.AgentJobId, ct);

        var segmentStats = await db.TranscriptionSegments
            .Where(s => jobIds.Contains(s.AgentJobId))
            .GroupBy(s => s.AgentJobId)
            .Select(g => new
            {
                AgentJobId = g.Key,
                Total = g.Count(),
                Finalized = g.Count(s => !s.IsProvisional),
                Provisional = g.Count(s => s.IsProvisional),
                MinStart = g.Min(s => s.StartTime),
                MaxEnd = g.Max(s => s.EndTime)
            })
            .ToDictionaryAsync(x => x.AgentJobId, ct);

        return summaries
            .Select(s =>
            {
                var tx = txJobs.GetValueOrDefault(s.Id);
                var stats = segmentStats.GetValueOrDefault(s.Id);
                double? duration = stats is not null ? stats.MaxEnd - stats.MinStart : null;
                return new TranscriptionJobSummaryResponse(
                    Id: s.Id,
                    ChannelId: s.ChannelId,
                    AgentId: s.AgentId,
                    ActionKey: s.ActionKey,
                    ResourceId: s.ResourceId,
                    Status: s.Status,
                    CreatedAt: s.CreatedAt,
                    StartedAt: s.StartedAt,
                    CompletedAt: s.CompletedAt,
                    TranscriptionModelId: tx?.ModelId,
                    Language: tx?.Language,
                    TranscriptionMode: tx?.Mode,
                    TotalSegments: stats?.Total ?? 0,
                    FinalizedSegments: stats?.Finalized ?? 0,
                    ProvisionalSegments: stats?.Provisional ?? 0,
                    TranscribedDurationSeconds: duration);
            })
            .ToList();
    }

    /// <summary>
    /// Retrieves transcription segments for a job, optionally filtered by timestamp.
    /// Standalone polling alternative to WebSocket/SSE streaming.
    /// </summary>
    public async Task<IReadOnlyList<TranscriptionSegmentResponse>?> GetSegmentsAsync(
        Guid jobId, DateTimeOffset? since = null, CancellationToken ct = default)
    {
        var exists = await jobReader.JobExistsWithActionPrefixAsync(
            jobId, TranscriptionActionPrefix, ct);
        if (!exists)
            return null;

        var threshold = since ?? DateTimeOffset.MinValue;
        var segments = await db.TranscriptionSegments
            .Where(s => s.AgentJobId == jobId && s.Timestamp > threshold)
            .OrderBy(s => s.StartTime)
            .ToListAsync(ct);

        return segments.Select(ToSegmentResponse).ToList();
    }

    // ═══════════════════════════════════════════════════════════════
    // Mapping
    // ═══════════════════════════════════════════════════════════════

    private static TranscriptionJobResponse ToTranscriptionResponse(
        AgentJobResponse job,
        TranscriptionJobDB? txJob,
        IReadOnlyList<TranscriptionSegmentDB> segments)
    {
        var segmentResponses = segments.Select(ToSegmentResponse).ToList();
        var finalized = segmentResponses.Count(s => !s.IsProvisional);
        var provisional = segmentResponses.Count(s => s.IsProvisional);
        var duration = segments.Count > 0
            ? segments.Max(s => s.EndTime) - segments.Min(s => s.StartTime)
            : (double?)null;

        return new TranscriptionJobResponse(
            Id: job.Id,
            ChannelId: job.ChannelId,
            AgentId: job.AgentId,
            ActionKey: job.ActionKey,
            ResourceId: job.ResourceId,
            Status: job.Status,
            EffectiveClearance: job.EffectiveClearance,
            ResultData: job.ResultData,
            ErrorLog: job.ErrorLog,
            Logs: job.Logs,
            CreatedAt: job.CreatedAt,
            StartedAt: job.StartedAt,
            CompletedAt: job.CompletedAt,
            TranscriptionModelId: txJob?.ModelId,
            Language: txJob?.Language,
            TranscriptionMode: txJob?.Mode,
            WindowSeconds: txJob?.WindowSeconds,
            StepSeconds: txJob?.StepSeconds,
            Segments: segmentResponses,
            TotalSegments: segmentResponses.Count,
            FinalizedSegments: finalized,
            ProvisionalSegments: provisional,
            TranscribedDurationSeconds: duration,
            JobCost: job.JobCost);
    }

    private static TranscriptionSegmentResponse ToSegmentResponse(TranscriptionSegmentDB s) =>
        new(s.Id, s.Text, s.StartTime, s.EndTime, s.Confidence, s.Timestamp, s.IsProvisional);

    private static bool IsTranscriptionAction(string? actionKey) =>
        actionKey is not null
        && actionKey.StartsWith(TranscriptionActionPrefix, StringComparison.OrdinalIgnoreCase);
}
