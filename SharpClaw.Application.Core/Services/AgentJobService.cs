using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using SharpClaw.Application.Infrastructure.Models.Jobs;
using SharpClaw.Contracts.DTOs.AgentActions;
using SharpClaw.Contracts.DTOs.Transcription;
using SharpClaw.Contracts.Enums;
using SharpClaw.Infrastructure.Models;
using SharpClaw.Infrastructure.Persistence;

namespace SharpClaw.Application.Services;

/// <summary>
/// Manages the lifecycle of agent action jobs: submission, permission
/// evaluation, optional approval, execution, and outcome tracking.
/// For transcription jobs, also manages live transcription
/// (orchestrator, channels, segments).
/// </summary>
public sealed class AgentJobService(
    SharpClawDbContext db,
    AgentActionService actions,
    LiveTranscriptionOrchestrator orchestrator)
{
    /// <summary>
    /// Per-job broadcast channels for live transcription streaming.
    /// </summary>
    private static readonly ConcurrentDictionary<Guid, Channel<TranscriptionSegmentResponse>> _channels = new();

    // ═══════════════════════════════════════════════════════════════
    // Public API
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Submit a new job.  Permission is evaluated immediately:
    /// <list type="bullet">
    ///   <item>Approved → executes inline, returns <see cref="AgentJobStatus.Completed"/>
    ///         or <see cref="AgentJobStatus.Failed"/> (or <see cref="AgentJobStatus.Executing"/>
    ///         for long-running jobs like transcription).</item>
    ///   <item>PendingApproval → returns <see cref="AgentJobStatus.AwaitingApproval"/>.</item>
    ///   <item>Denied → returns <see cref="AgentJobStatus.Denied"/>.</item>
    /// </list>
    /// </summary>
    public async Task<AgentJobResponse> SubmitAsync(
        Guid agentId,
        SubmitAgentJobRequest request,
        CancellationToken ct = default)
    {
        // When no agent is specified, infer from the conversation
        if (agentId == Guid.Empty && request.ConversationId is { } convId)
        {
            var conv = await db.Conversations
                .Include(c => c.AgentContext)
                .FirstOrDefaultAsync(c => c.Id == convId, ct)
                ?? throw new InvalidOperationException($"Conversation {convId} not found.");
            agentId = conv.AgentId;
        }

        if (agentId == Guid.Empty)
            throw new InvalidOperationException(
                "An agent ID is required. Provide one directly or specify --conv to infer it.");


        var effectiveResourceId = request.ResourceId;

        var job = new AgentJobDB
        {
            AgentId = agentId,
            CallerUserId = request.CallerUserId,
            CallerAgentId = request.CallerAgentId,
            ActionType = request.ActionType,
            ResourceId = effectiveResourceId,
            Status = AgentJobStatus.Queued,
            TranscriptionModelId = request.TranscriptionModelId,
            ConversationId = request.ConversationId,
            Language = request.Language,
        };

        db.AgentJobs.Add(job);
        AddLog(job, $"Job queued: {request.ActionType}.");
        await db.SaveChangesAsync(ct);

        var caller = new ActionCaller(request.CallerUserId, request.CallerAgentId);
        var result = await DispatchPermissionCheckAsync(
            agentId, job.ActionType, job.ResourceId, caller, ct);

        job.EffectiveClearance = result.EffectiveClearance;

        switch (result.Verdict)
        {
            case ClearanceVerdict.Approved:
                AddLog(job, $"Permission granted: {result.Reason}");
                await ExecuteJobAsync(job, ct);
                break;

            case ClearanceVerdict.PendingApproval:
                job.Status = AgentJobStatus.AwaitingApproval;
                AddLog(job, $"Awaiting approval: {result.Reason}");
                await db.SaveChangesAsync(ct);
                break;

            case ClearanceVerdict.Denied:
            default:
                job.Status = AgentJobStatus.Denied;
                AddLog(job, $"Denied: {result.Reason}", "Warning");
                await db.SaveChangesAsync(ct);
                break;
        }

        return ToResponse(job);
    }

    /// <summary>
    /// Approve a job that is <see cref="AgentJobStatus.AwaitingApproval"/>.
    /// </summary>
    public async Task<AgentJobResponse?> ApproveAsync(
        Guid jobId,
        ApproveAgentJobRequest request,
        CancellationToken ct = default)
    {
        var job = await LoadJobAsync(jobId, ct);
        if (job is null) return null;

        if (job.Status != AgentJobStatus.AwaitingApproval)
        {
            AddLog(job, $"Approve rejected: job is {job.Status}, not AwaitingApproval.", "Warning");
            await db.SaveChangesAsync(ct);
            return ToResponse(job);
        }

        var approver = new ActionCaller(request.ApproverUserId, request.ApproverAgentId);
        var result = await DispatchPermissionCheckAsync(
            job.AgentId, job.ActionType, job.ResourceId, approver, ct);

        switch (result.Verdict)
        {
            case ClearanceVerdict.Approved:
                job.ApprovedByUserId = request.ApproverUserId;
                job.ApprovedByAgentId = request.ApproverAgentId;
                AddLog(job, $"Approved by {FormatCaller(approver)}: {result.Reason}");
                await ExecuteJobAsync(job, ct);
                break;

            case ClearanceVerdict.PendingApproval:
                AddLog(job, $"Approval attempt by {FormatCaller(approver)} insufficient: {result.Reason}", "Warning");
                await db.SaveChangesAsync(ct);
                break;

            case ClearanceVerdict.Denied:
            default:
                AddLog(job, $"Approval attempt by {FormatCaller(approver)} denied: {result.Reason}", "Warning");
                await db.SaveChangesAsync(ct);
                break;
        }

        return ToResponse(job);
    }

    /// <summary>Cancel a job that has not yet completed.</summary>
    public async Task<AgentJobResponse?> CancelAsync(
        Guid jobId, CancellationToken ct = default)
    {
        var job = await LoadJobAsync(jobId, ct);
        if (job is null) return null;

        if (job.Status is AgentJobStatus.Completed or AgentJobStatus.Failed
                       or AgentJobStatus.Denied or AgentJobStatus.Cancelled)
        {
            AddLog(job, $"Cancel rejected: job is already {job.Status}.", "Warning");
            await db.SaveChangesAsync(ct);
            return ToResponse(job);
        }

        if (IsTranscriptionAction(job.ActionType))
        {
            orchestrator.Stop(jobId);
            CompleteChannel(jobId);
        }

        job.Status = AgentJobStatus.Cancelled;
        job.CompletedAt = DateTimeOffset.UtcNow;
        AddLog(job, "Job cancelled.");
        await db.SaveChangesAsync(ct);

        return ToResponse(job);
    }

    /// <summary>Stop a long-running transcription job (complete it normally).</summary>
    public async Task<AgentJobResponse?> StopTranscriptionAsync(
        Guid jobId, CancellationToken ct = default)
    {
        var job = await LoadJobAsync(jobId, ct);
        if (job is null) return null;

        if (!IsTranscriptionAction(job.ActionType))
        {
            AddLog(job, "Stop rejected: not a transcription job.", "Warning");
            await db.SaveChangesAsync(ct);
            return ToResponse(job);
        }

        if (job.Status != AgentJobStatus.Executing)
        {
            AddLog(job, $"Stop rejected: job is {job.Status}, not Executing.", "Warning");
            await db.SaveChangesAsync(ct);
            return ToResponse(job);
        }

        orchestrator.Stop(jobId);

        job.Status = AgentJobStatus.Completed;
        job.CompletedAt = DateTimeOffset.UtcNow;
        AddLog(job, "Transcription completed.");
        await db.SaveChangesAsync(ct);

        CompleteChannel(jobId);

        return ToResponse(job);
    }

    /// <summary>Retrieve a single job by ID.</summary>
    public async Task<AgentJobResponse?> GetAsync(
        Guid jobId, CancellationToken ct = default)
    {
        var job = await LoadJobAsync(jobId, ct);
        return job is null ? null : ToResponse(job);
    }

    /// <summary>List all jobs for an agent, most recent first.</summary>
    public async Task<IReadOnlyList<AgentJobResponse>> ListAsync(
        Guid agentId, CancellationToken ct = default)
    {
        var jobs = await db.AgentJobs
            .Include(j => j.LogEntries)
            .Include(j => j.TranscriptionSegments.OrderBy(s => s.StartTime))
            .Where(j => j.AgentId == agentId)
            .OrderByDescending(j => j.CreatedAt)
            .ToListAsync(ct);

        return jobs.Select(ToResponse).ToList();
    }

    /// <summary>List transcription jobs, optionally filtered by audio device.</summary>
    public async Task<IReadOnlyList<AgentJobResponse>> ListTranscriptionJobsAsync(
        Guid? audioDeviceId = null, CancellationToken ct = default)
    {
        var query = db.AgentJobs
            .Include(j => j.LogEntries)
            .Include(j => j.TranscriptionSegments.OrderBy(s => s.StartTime))
            .Where(j => j.ActionType == AgentActionType.TranscribeFromAudioDevice
                      || j.ActionType == AgentActionType.TranscribeFromAudioStream
                      || j.ActionType == AgentActionType.TranscribeFromAudioFile);

        if (audioDeviceId is not null)
            query = query.Where(j => j.ResourceId == audioDeviceId);

        var jobs = await query.OrderByDescending(j => j.CreatedAt).ToListAsync(ct);
        return jobs.Select(ToResponse).ToList();
    }

    // ═══════════════════════════════════════════════════════════════
    // Transcription: segments & streaming
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Pushes a new transcription segment into an executing job.
    /// Called by the orchestrator each time a segment is recognised.
    /// </summary>
    public async Task<TranscriptionSegmentResponse?> PushSegmentAsync(
        Guid jobId, string text, double startTime, double endTime,
        double? confidence = null, CancellationToken ct = default)
    {
        var job = await db.AgentJobs.FirstOrDefaultAsync(j => j.Id == jobId, ct);
        if (job is null || job.Status != AgentJobStatus.Executing)
            return null;

        var segment = new TranscriptionSegmentDB
        {
            AgentJobId = jobId,
            Text = text,
            StartTime = startTime,
            EndTime = endTime,
            Confidence = confidence,
            Timestamp = DateTimeOffset.UtcNow
        };

        db.TranscriptionSegments.Add(segment);
        await db.SaveChangesAsync(ct);

        var response = new TranscriptionSegmentResponse(
            segment.Id, segment.Text, segment.StartTime, segment.EndTime,
            segment.Confidence, segment.Timestamp);

        if (_channels.TryGetValue(jobId, out var channel))
            await channel.Writer.WriteAsync(response, ct);

        return response;
    }

    /// <summary>
    /// Returns a <see cref="ChannelReader{T}"/> for live segment updates.
    /// </summary>
    public ChannelReader<TranscriptionSegmentResponse>? Subscribe(Guid jobId)
    {
        return _channels.TryGetValue(jobId, out var channel)
            ? channel.Reader
            : null;
    }

    /// <summary>Retrieves segments added after a given timestamp.</summary>
    public async Task<IReadOnlyList<TranscriptionSegmentResponse>> GetSegmentsSinceAsync(
        Guid jobId, DateTimeOffset since, CancellationToken ct = default)
    {
        var segments = await db.TranscriptionSegments
            .Where(s => s.AgentJobId == jobId && s.Timestamp > since)
            .OrderBy(s => s.StartTime)
            .ToListAsync(ct);

        return segments.Select(s => new TranscriptionSegmentResponse(
            s.Id, s.Text, s.StartTime, s.EndTime, s.Confidence, s.Timestamp)).ToList();
    }

    // ═══════════════════════════════════════════════════════════════
    // Execution
    // ═══════════════════════════════════════════════════════════════

    private async Task ExecuteJobAsync(AgentJobDB job, CancellationToken ct)
    {
        job.Status = AgentJobStatus.Executing;
        job.StartedAt = DateTimeOffset.UtcNow;
        AddLog(job, "Execution started.");
        await db.SaveChangesAsync(ct);

        try
        {
            if (IsTranscriptionAction(job.ActionType))
            {
                await StartTranscriptionAsync(job, ct);
                return;
            }

            var resultData = await DispatchExecutionAsync(job, ct);
            job.Status = AgentJobStatus.Completed;
            job.CompletedAt = DateTimeOffset.UtcNow;
            job.ResultData = resultData;
            AddLog(job, "Job completed successfully.");
        }
        catch (Exception ex)
        {
            job.Status = AgentJobStatus.Failed;
            job.CompletedAt = DateTimeOffset.UtcNow;
            job.ErrorLog = ex.ToString();
            AddLog(job, $"Job failed: {ex.Message}", "Error");
        }

        await db.SaveChangesAsync(ct);
    }

    private async Task StartTranscriptionAsync(AgentJobDB job, CancellationToken ct)
    {
        ModelDB model;
        if (job.TranscriptionModelId is { } explicitModelId)
        {
            model = await db.Models.FirstOrDefaultAsync(m => m.Id == explicitModelId, ct)
                ?? throw new InvalidOperationException($"Model {explicitModelId} not found.");

            if (!model.Capabilities.HasFlag(ModelCapability.Transcription))
                throw new InvalidOperationException(
                    $"Model '{model.Name}' does not have the Transcription capability.");
        }
        else
        {
            model = await db.Models
                .FirstOrDefaultAsync(m => (m.Capabilities & ModelCapability.Transcription) != 0, ct)
                ?? throw new InvalidOperationException(
                    "No model with Transcription capability found. " +
                    "Register a model with 'model add <name> <providerId> --cap Transcription'.");
        }

        job.TranscriptionModelId = model.Id;

        var device = await db.AudioDevices.FirstOrDefaultAsync(d => d.Id == job.ResourceId, ct)
            ?? throw new InvalidOperationException("Audio device not found.");

        _channels.TryAdd(job.Id, Channel.CreateUnbounded<TranscriptionSegmentResponse>());

        AddLog(job, $"Transcription started with model '{model.Name}' on device '{device.Name}'.");
        await db.SaveChangesAsync(ct);

        orchestrator.Start(job.Id, model.Id, device.DeviceIdentifier, job.Language);
    }

    private static Task<string?> DispatchExecutionAsync(AgentJobDB job, CancellationToken ct)
    {
        _ = ct;
        return Task.FromResult<string?>(
            $"Action '{job.ActionType}' executed successfully (resource: {job.ResourceId?.ToString() ?? "n/a"}).");
    }

    // ═══════════════════════════════════════════════════════════════
    // Permission dispatch
    // ═══════════════════════════════════════════════════════════════

    private Task<AgentActionResult> DispatchPermissionCheckAsync(
        Guid agentId, AgentActionType actionType, Guid? resourceId,
        ActionCaller caller, CancellationToken ct)
    {
        return actionType switch
        {
            AgentActionType.ExecuteAsAdmin
                => actions.ExecuteAsAdminAsync(agentId, caller, ct: ct),
            AgentActionType.CreateSubAgent
                => actions.CreateSubAgentAsync(agentId, caller, ct: ct),
            AgentActionType.CreateContainer
                => actions.CreateContainerAsync(agentId, caller, ct: ct),
            AgentActionType.RegisterInfoStore
                => actions.RegisterInfoStoreAsync(agentId, caller, ct: ct),
            AgentActionType.EditAnyTask
                => actions.EditAnyTaskAsync(agentId, caller, ct: ct),
            AgentActionType.ExecuteAsSystemUser when resourceId.HasValue
                => actions.ExecuteAsSystemUserAsync(agentId, resourceId.Value, caller, ct: ct),
            AgentActionType.AccessLocalInfoStore when resourceId.HasValue
                => actions.AccessLocalInfoStoreAsync(agentId, resourceId.Value, caller, ct: ct),
            AgentActionType.AccessExternalInfoStore when resourceId.HasValue
                => actions.AccessExternalInfoStoreAsync(agentId, resourceId.Value, caller, ct: ct),
            AgentActionType.AccessWebsite when resourceId.HasValue
                => actions.AccessWebsiteAsync(agentId, resourceId.Value, caller, ct: ct),
            AgentActionType.QuerySearchEngine when resourceId.HasValue
                => actions.QuerySearchEngineAsync(agentId, resourceId.Value, caller, ct: ct),
            AgentActionType.AccessContainer when resourceId.HasValue
                => actions.AccessContainerAsync(agentId, resourceId.Value, caller, ct: ct),
            AgentActionType.ManageAgent when resourceId.HasValue
                => actions.ManageAgentAsync(agentId, resourceId.Value, caller, ct: ct),
            AgentActionType.EditTask when resourceId.HasValue
                => actions.EditTaskAsync(agentId, resourceId.Value, caller, ct: ct),
            AgentActionType.AccessSkill when resourceId.HasValue
                => actions.AccessSkillAsync(agentId, resourceId.Value, caller, ct: ct),
            AgentActionType.TranscribeFromAudioDevice when resourceId.HasValue
                => actions.AccessAudioDeviceAsync(agentId, resourceId.Value, caller, ct: ct),
            AgentActionType.TranscribeFromAudioStream when resourceId.HasValue
                => actions.AccessAudioDeviceAsync(agentId, resourceId.Value, caller, ct: ct),
            AgentActionType.TranscribeFromAudioFile when resourceId.HasValue
                => actions.AccessAudioDeviceAsync(agentId, resourceId.Value, caller, ct: ct),
            _ when IsPerResourceAction(actionType) && !resourceId.HasValue
                => Task.FromResult(AgentActionResult.Denied($"ResourceId is required for {actionType}.")),
            _ => Task.FromResult(AgentActionResult.Denied($"Unknown action type: {actionType}."))
        };
    }

    private static bool IsPerResourceAction(AgentActionType type) =>
        type is AgentActionType.ExecuteAsSystemUser
            or AgentActionType.AccessLocalInfoStore
            or AgentActionType.AccessExternalInfoStore
            or AgentActionType.AccessWebsite
            or AgentActionType.QuerySearchEngine
            or AgentActionType.AccessContainer
            or AgentActionType.ManageAgent
            or AgentActionType.EditTask
            or AgentActionType.AccessSkill
            or AgentActionType.TranscribeFromAudioDevice
            or AgentActionType.TranscribeFromAudioStream
            or AgentActionType.TranscribeFromAudioFile;

    // ═══════════════════════════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════════════════════════

    private async Task<AgentJobDB?> LoadJobAsync(Guid jobId, CancellationToken ct) =>
        await db.AgentJobs
            .Include(j => j.LogEntries)
            .Include(j => j.TranscriptionSegments.OrderBy(s => s.StartTime))
            .FirstOrDefaultAsync(j => j.Id == jobId, ct);

    private static void AddLog(AgentJobDB job, string message, string level = "Info")
    {
        job.LogEntries.Add(new AgentJobLogEntryDB
        {
            AgentJobId = job.Id,
            Message = message,
            Level = level
        });
    }

    private static string FormatCaller(ActionCaller caller) =>
        caller.UserId is not null ? $"user {caller.UserId}"
        : caller.AgentId is not null ? $"agent {caller.AgentId}"
        : "unknown";

    private static void CompleteChannel(Guid jobId)
    {
        if (_channels.TryRemove(jobId, out var channel))
            channel.Writer.TryComplete();
    }

    private static bool IsTranscriptionAction(AgentActionType type) =>
        type is AgentActionType.TranscribeFromAudioDevice
            or AgentActionType.TranscribeFromAudioStream
            or AgentActionType.TranscribeFromAudioFile;

    private static AgentJobResponse ToResponse(AgentJobDB job) =>
        new(
            Id: job.Id,
            AgentId: job.AgentId,
            ActionType: job.ActionType,
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
            ConversationId: job.ConversationId,
            Language: job.Language,
            Segments: IsTranscriptionAction(job.ActionType)
                ? job.TranscriptionSegments
                    .OrderBy(s => s.StartTime)
                    .Select(s => new TranscriptionSegmentResponse(
                        s.Id, s.Text, s.StartTime, s.EndTime, s.Confidence, s.Timestamp))
                    .ToList()
                : null);
}
