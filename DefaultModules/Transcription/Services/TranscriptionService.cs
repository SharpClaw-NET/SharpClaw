using Microsoft.EntityFrameworkCore;

using SharpClaw.Application.Infrastructure.Models.Jobs;
using SharpClaw.Application.Infrastructure.Models.Messages;
using SharpClaw.Application.Infrastructure.Models.Resources;
using SharpClaw.Contracts.DTOs.AgentActions;
using SharpClaw.Contracts.DTOs.Chat;
using SharpClaw.Contracts.DTOs.Transcription;
using SharpClaw.Infrastructure.Persistence;
using SharpClaw.Modules.Transcription.Clients;

namespace SharpClaw.Modules.Transcription.Services;

/// <summary>
/// Manages input audio CRUD and transcription job queries.
/// Job lifecycle (submit, approve, cancel, pause, resume) is handled
/// by <see cref="SharpClaw.Application.Services.AgentJobService"/> via the
/// job/permission system.  This service owns the transcription-specific
/// DTO mapping so the core stays free of transcription knowledge.
/// </summary>
public sealed class TranscriptionService(SharpClawDbContext db, IAudioCaptureProvider capture)
{
    // ═══════════════════════════════════════════════════════════════
    // Input audio CRUD
    // ═══════════════════════════════════════════════════════════════

    public async Task<InputAudioResponse> CreateDeviceAsync(CreateInputAudioRequest request, CancellationToken ct = default)
    {
        var device = new InputAudioDB
        {
            Name = request.Name,
            DeviceIdentifier = request.DeviceIdentifier,
            Description = request.Description
        };

        db.InputAudios.Add(device);
        await db.SaveChangesAsync(ct);

        return ToResponse(device);
    }

    public async Task<IReadOnlyList<InputAudioResponse>> ListDevicesAsync(CancellationToken ct = default)
    {
        var devices = await db.InputAudios
            .OrderBy(d => d.Name)
            .ToListAsync(ct);

        return devices.Select(ToResponse).ToList();
    }

    public async Task<InputAudioResponse?> GetDeviceByIdAsync(Guid id, CancellationToken ct = default)
    {
        var device = await db.InputAudios.FirstOrDefaultAsync(d => d.Id == id, ct);
        return device is not null ? ToResponse(device) : null;
    }

    public async Task<InputAudioResponse?> UpdateDeviceAsync(Guid id, UpdateInputAudioRequest request, CancellationToken ct = default)
    {
        var device = await db.InputAudios.FirstOrDefaultAsync(d => d.Id == id, ct);
        if (device is null) return null;

        if (request.Name is not null) device.Name = request.Name;
        if (request.DeviceIdentifier is not null) device.DeviceIdentifier = request.DeviceIdentifier;
        if (request.Description is not null) device.Description = request.Description;

        await db.SaveChangesAsync(ct);
        return ToResponse(device);
    }

    public async Task<bool> DeleteDeviceAsync(Guid id, CancellationToken ct = default)
    {
        var device = await db.InputAudios.FirstOrDefaultAsync(d => d.Id == id, ct);
        if (device is null) return false;

        db.InputAudios.Remove(device);
        await db.SaveChangesAsync(ct);
        return true;
    }

    // ═══════════════════════════════════════════════════════════════
    // Sync — discover system audio devices and import new ones
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Discovers all audio input devices on the current system and
    /// imports any that are not already in the database. Duplicates
    /// (matched by <see cref="InputAudioDB.DeviceIdentifier"/>) are
    /// skipped.
    /// </summary>
    public async Task<InputAudioSyncResult> SyncDevicesAsync(CancellationToken ct = default)
    {
        var systemDevices = capture.ListDevices();

        var existingIdentifiers = await db.InputAudios
            .Where(d => d.DeviceIdentifier != null)
            .Select(d => d.DeviceIdentifier!)
            .ToListAsync(ct);

        var existingSet = new HashSet<string>(
            existingIdentifiers, StringComparer.OrdinalIgnoreCase);

        var imported = new List<string>();
        var skipped = new List<string>();

        foreach (var (id, name) in systemDevices)
        {
            if (existingSet.Contains(id))
            {
                skipped.Add(name);
                continue;
            }

            var device = new InputAudioDB
            {
                Name = name,
                DeviceIdentifier = id,
                Description = "Synced from system audio devices",
            };

            db.InputAudios.Add(device);
            imported.Add(name);
        }

        if (imported.Count > 0)
            await db.SaveChangesAsync(ct);

        return new InputAudioSyncResult(
            imported.Count,
            skipped.Count,
            imported,
            skipped);
    }

    private static InputAudioResponse ToResponse(InputAudioDB d) =>
        new(d.Id, d.Name, d.DeviceIdentifier, d.Description, d.SkillId, d.CreatedAt);

    // ═══════════════════════════════════════════════════════════════
    // Transcription job queries
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Retrieves a single transcription job by ID and maps it to the
    /// transcription-specific DTO that includes segments and computed stats.
    /// Returns <c>null</c> if the job does not exist or is not a
    /// transcription job.
    /// </summary>
    public async Task<TranscriptionJobResponse?> GetTranscriptionJobAsync(
        Guid jobId, CancellationToken ct = default)
    {
        var job = await LoadTranscriptionJobAsync(jobId, ct);
        if (job is null || !IsTranscriptionAction(job.ActionKey))
            return null;

        return ToTranscriptionResponse(job);
    }

    /// <summary>
    /// Lists all transcription jobs, optionally filtered by input audio
    /// device.  Returns the full transcription DTO with segments.
    /// </summary>
    public async Task<IReadOnlyList<TranscriptionJobResponse>> ListTranscriptionJobsAsync(
        Guid? inputAudioId = null, CancellationToken ct = default)
    {
        var query = db.AgentJobs
            .Include(j => j.LogEntries)
            .Include(j => j.TranscriptionSegments.OrderBy(s => s.StartTime))
            .Where(j => j.ActionKey != null && j.ActionKey.StartsWith("transcribe_from_audio"));

        if (inputAudioId is not null)
            query = query.Where(j => j.ResourceId == inputAudioId);

        var jobs = await query.OrderByDescending(j => j.CreatedAt).ToListAsync(ct);
        return jobs.Select(ToTranscriptionResponse).ToList();
    }

    /// <summary>
    /// Lists lightweight transcription job summaries — no segments or
    /// heavy payloads.  Suitable for dropdowns and list views.
    /// </summary>
    public async Task<IReadOnlyList<TranscriptionJobSummaryResponse>> ListTranscriptionJobSummariesAsync(
        Guid? inputAudioId = null, CancellationToken ct = default)
    {
        var query = db.AgentJobs
            .Include(j => j.TranscriptionSegments)
            .Where(j => j.ActionKey != null && j.ActionKey.StartsWith("transcribe_from_audio"));

        if (inputAudioId is not null)
            query = query.Where(j => j.ResourceId == inputAudioId);

        var jobs = await query.OrderByDescending(j => j.CreatedAt).ToListAsync(ct);
        return jobs.Select(ToTranscriptionSummary).ToList();
    }

    /// <summary>
    /// Retrieves transcription segments for a job, optionally filtered
    /// by timestamp.  Standalone polling alternative to WebSocket/SSE
    /// streaming.
    /// </summary>
    public async Task<IReadOnlyList<TranscriptionSegmentResponse>?> GetSegmentsAsync(
        Guid jobId, DateTimeOffset? since = null, CancellationToken ct = default)
    {
        var jobExists = await db.AgentJobs
            .AnyAsync(j => j.Id == jobId
                && j.ActionKey != null
                && j.ActionKey.StartsWith("transcribe_from_audio"), ct);
        if (!jobExists)
            return null;

        var threshold = since ?? DateTimeOffset.MinValue;
        var segments = await db.TranscriptionSegments
            .Where(s => s.AgentJobId == jobId && s.Timestamp > threshold)
            .OrderBy(s => s.StartTime)
            .ToListAsync(ct);

        return segments.Select(ToSegmentResponse).ToList();
    }

    // ═══════════════════════════════════════════════════════════════
    // Transcription job mapping
    // ═══════════════════════════════════════════════════════════════

    private static TranscriptionJobResponse ToTranscriptionResponse(AgentJobDB job)
    {
        var segments = job.TranscriptionSegments
            .OrderBy(s => s.StartTime)
            .Select(ToSegmentResponse)
            .ToList();

        var finalized = segments.Count(s => !s.IsProvisional);
        var provisional = segments.Count(s => s.IsProvisional);
        var duration = segments.Count > 0
            ? segments.Max(s => s.EndTime) - segments.Min(s => s.StartTime)
            : (double?)null;

        var jobCost = job.PromptTokens is not null || job.CompletionTokens is not null
            ? new TokenUsageResponse(
                job.PromptTokens ?? 0,
                job.CompletionTokens ?? 0,
                (job.PromptTokens ?? 0) + (job.CompletionTokens ?? 0))
            : null;

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
            Logs: job.LogEntries
                .OrderBy(l => l.CreatedAt)
                .Select(l => new AgentJobLogResponse(l.Message, l.Level, l.CreatedAt))
                .ToList(),
            CreatedAt: job.CreatedAt,
            StartedAt: job.StartedAt,
            CompletedAt: job.CompletedAt,
            TranscriptionModelId: job.TranscriptionModelId,
            Language: job.Language,
            TranscriptionMode: job.TranscriptionMode,
            WindowSeconds: job.WindowSeconds,
            StepSeconds: job.StepSeconds,
            Segments: segments,
            TotalSegments: segments.Count,
            FinalizedSegments: finalized,
            ProvisionalSegments: provisional,
            TranscribedDurationSeconds: duration,
            JobCost: jobCost);
    }

    private static TranscriptionJobSummaryResponse ToTranscriptionSummary(AgentJobDB job)
    {
        var total = job.TranscriptionSegments.Count;
        var finalized = job.TranscriptionSegments.Count(s => !s.IsProvisional);
        var provisional = total - finalized;
        var duration = total > 0
            ? job.TranscriptionSegments.Max(s => s.EndTime) - job.TranscriptionSegments.Min(s => s.StartTime)
            : (double?)null;

        return new TranscriptionJobSummaryResponse(
            Id: job.Id,
            ChannelId: job.ChannelId,
            AgentId: job.AgentId,
            ActionKey: job.ActionKey,
            ResourceId: job.ResourceId,
            Status: job.Status,
            CreatedAt: job.CreatedAt,
            StartedAt: job.StartedAt,
            CompletedAt: job.CompletedAt,
            TranscriptionModelId: job.TranscriptionModelId,
            Language: job.Language,
            TranscriptionMode: job.TranscriptionMode,
            TotalSegments: total,
            FinalizedSegments: finalized,
            ProvisionalSegments: provisional,
            TranscribedDurationSeconds: duration);
    }

    private static TranscriptionSegmentResponse ToSegmentResponse(TranscriptionSegmentDB s) =>
        new(s.Id, s.Text, s.StartTime, s.EndTime, s.Confidence, s.Timestamp, s.IsProvisional);

    private static async Task<AgentJobDB?> LoadTranscriptionJobAsync(
        SharpClawDbContext db, Guid jobId, CancellationToken ct) =>
        await db.AgentJobs
            .Include(j => j.LogEntries)
            .Include(j => j.TranscriptionSegments.OrderBy(s => s.StartTime))
            .FirstOrDefaultAsync(j => j.Id == jobId, ct);

    private Task<AgentJobDB?> LoadTranscriptionJobAsync(Guid jobId, CancellationToken ct) =>
        LoadTranscriptionJobAsync(db, jobId, ct);

    private static bool IsTranscriptionAction(string? actionKey) =>
        actionKey is not null && actionKey.StartsWith("transcribe_from_audio", StringComparison.OrdinalIgnoreCase);
}
