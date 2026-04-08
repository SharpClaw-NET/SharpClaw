using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Application.Core.Modules;
using SharpClaw.Application.Infrastructure.Models.Clearance;
using SharpClaw.Application.Infrastructure.Models.Context;
using SharpClaw.Application.Infrastructure.Models.Jobs;
using SharpClaw.Application.Infrastructure.Models.Messages;
using SharpClaw.Application.Infrastructure.Models.Resources;
using SharpClaw.Contracts;
using SharpClaw.Contracts.DTOs.AgentActions;
using SharpClaw.Contracts.DTOs.Transcription;
using SharpClaw.Contracts.Enums;
using SharpClaw.Contracts.Modules;
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
LiveTranscriptionOrchestrator orchestrator,
SessionService session,
BotMessageSenderService botMessageSender,
DocumentSessionService documentSessionService,
SearchEngineService searchEngineService,
ModuleRegistry moduleRegistry,
IServiceScopeFactory serviceScopeFactory,
IConfiguration configuration)
{
    /// <summary>
    /// Per-job broadcast channels for live transcription streaming.
    /// </summary>
    private static readonly ConcurrentDictionary<Guid, Channel<TranscriptionSegmentResponse>> _channels = new();

    // ═══════════════════════════════════════════════════════════════
    // Public API
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Submit a new job.  The channel is the primary anchor — the agent
    /// is inferred from the channel unless explicitly overridden in the
    /// request.  Permission is evaluated immediately:
    /// <list type="bullet">
    ///   <item>Approved → executes inline, returns <see cref="AgentJobStatus.Completed"/>
    ///         or <see cref="AgentJobStatus.Failed"/> (or <see cref="AgentJobStatus.Executing"/>
    ///         for long-running jobs like transcription).</item>
    ///   <item>PendingApproval → returns <see cref="AgentJobStatus.AwaitingApproval"/>.</item>
    ///   <item>Denied → returns <see cref="AgentJobStatus.Denied"/>.</item>
    /// </list>
    /// </summary>
    public async Task<AgentJobResponse> SubmitAsync(
        Guid channelId,
        SubmitAgentJobRequest request,
        CancellationToken ct = default)
    {
        var ch = await db.Channels
            .Include(c => c.AgentContext).ThenInclude(ctx => ctx!.AllowedAgents)
            .Include(c => c.AllowedAgents)
            .FirstOrDefaultAsync(c => c.Id == channelId, ct)
            ?? throw new InvalidOperationException($"Channel {channelId} not found.");

        var agentId = ch.AgentId ?? ch.AgentContext?.AgentId
            ?? throw new InvalidOperationException(
                $"Channel {channelId} has no agent and no context agent.");

        // Allow overriding the agent if the requested agent is the
        // channel default or is in the allowed-agents set (channel-level
        // first, falling back to context-level).
        if (request.AgentId is { } requestedAgent && requestedAgent != agentId)
        {
            var effectiveAllowed = ch.AllowedAgents.Count > 0
                ? ch.AllowedAgents
                : (IEnumerable<AgentDB>)(ch.AgentContext?.AllowedAgents ?? []);

            if (!effectiveAllowed.Any(a => a.Id == requestedAgent))
                throw new InvalidOperationException(
                    $"Agent {requestedAgent} is not allowed on channel {channelId}. " +
                    "Add it to the channel's or context's allowed agents first.");
            agentId = requestedAgent;
        }

        var effectiveResourceId = request.ResourceId;

        // When no resource is specified for a per-resource action, resolve
        // the default from: channel DefaultResourceSet → context DefaultResourceSet
        // → channel/context/role PermissionSet defaults.
        if (!effectiveResourceId.HasValue && IsPerResourceAction(request.ActionType, request.ActionKey))
        {
            effectiveResourceId = await ResolveDefaultResourceIdAsync(
                request.ActionType, channelId, agentId, ct);
        }

        // Resolve default transcription model when not specified.
        var effectiveTranscriptionModelId = request.TranscriptionModelId;
        if (!effectiveTranscriptionModelId.HasValue && IsTranscriptionAction(request.ActionType))
        {
            effectiveTranscriptionModelId = await ResolveDefaultTranscriptionModelAsync(
                channelId, ct);
        }

        var job = new AgentJobDB
        {
            AgentId = agentId,
            ChannelId = channelId,
            CallerUserId = session.UserId,
            CallerAgentId = request.CallerAgentId,
            ActionType = request.ActionType,
            ActionKey = request.ActionKey,
            ResourceId = effectiveResourceId,
            Status = AgentJobStatus.Queued,
            DangerousShellType = request.DangerousShellType,
            SafeShellType = request.SafeShellType,
            ScriptJson = request.ScriptJson,
            WorkingDirectory = request.WorkingDirectory,
            TranscriptionModelId = effectiveTranscriptionModelId,
            Language = request.Language,
            TranscriptionMode = request.TranscriptionMode,
            WindowSeconds = request.WindowSeconds,
            StepSeconds = request.StepSeconds,
        };

        db.AgentJobs.Add(job);
        AddLog(job, $"Job queued: {request.ActionType}.");
        await db.SaveChangesAsync(ct);

        var caller = new ActionCaller(session.UserId, request.CallerAgentId);
        var result = await DispatchPermissionCheckAsync(
            agentId, job.ActionType, job.ResourceId, caller, ct, job.ActionKey);

        job.EffectiveClearance = result.EffectiveClearance;

        switch (result.Verdict)
        {
            case ClearanceVerdict.Approved:
                AddLog(job, $"Permission granted: {result.Reason}");
                await ExecuteJobAsync(job, ct);
                break;

            case ClearanceVerdict.PendingApproval:
                // Channel pre-auth counts as ApprovedByWhitelistedUser-
                // level authority for levels 2 and 4.  For level 1
                // (ApprovedBySameLevelUser), the session user must also
                // personally hold the same permission via their own role.
                // Level 3 (agent-only) is never pre-authorised.
                if (await HasChannelAuthorizationAsync(
                        channelId, job.ActionType,
                        job.ResourceId, result.EffectiveClearance,
                        session.UserId, ct, job.ActionKey))
                {
                    AddLog(job, "Pre-authorized by channel/context permission set.");
                    await ExecuteJobAsync(job, ct);
                }
                else
                {
                    job.Status = AgentJobStatus.AwaitingApproval;
                    AddLog(job, $"Awaiting approval: {result.Reason}");
                    await db.SaveChangesAsync(ct);
                }
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

        var approver = new ActionCaller(session.UserId, request.ApproverAgentId);
        var result = await DispatchPermissionCheckAsync(
            job.AgentId, job.ActionType, job.ResourceId, approver, ct);

        switch (result.Verdict)
        {
            case ClearanceVerdict.Approved:
                job.ApprovedByUserId = session.UserId;
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
                job.Status = AgentJobStatus.Denied;
                job.CompletedAt = DateTimeOffset.UtcNow;
                AddLog(job, $"Denied: agent permission revoked. Attempt by {FormatCaller(approver)}: {result.Reason}", "Warning");
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

        if (IsTranscriptionAction(job.ActionType)
            && job.Status is AgentJobStatus.Executing or AgentJobStatus.Paused)
        {
            if (job.Status == AgentJobStatus.Executing)
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

        if (job.Status is not AgentJobStatus.Executing and not AgentJobStatus.Paused)
        {
            AddLog(job, $"Stop rejected: job is {job.Status}, not Executing or Paused.", "Warning");
            await db.SaveChangesAsync(ct);
            return ToResponse(job);
        }

        if (job.Status == AgentJobStatus.Executing)
            orchestrator.Stop(jobId);

        job.Status = AgentJobStatus.Completed;
        job.CompletedAt = DateTimeOffset.UtcNow;
        AddLog(job, "Transcription completed.");
        await db.SaveChangesAsync(ct);

        CompleteChannel(jobId);

        return ToResponse(job);
    }

    /// <summary>
    /// Pause a long-running job.  For transcription jobs this stops the
    /// audio capture loop so no further inference calls (and therefore no
    /// token costs) are incurred.  The job can be resumed later with
    /// <see cref="ResumeAsync"/>.
    /// </summary>
    public async Task<AgentJobResponse?> PauseAsync(
        Guid jobId, CancellationToken ct = default)
    {
        var job = await LoadJobAsync(jobId, ct);
        if (job is null) return null;

        if (job.Status != AgentJobStatus.Executing)
        {
            AddLog(job, $"Pause rejected: job is {job.Status}, not Executing.", "Warning");
            await db.SaveChangesAsync(ct);
            return ToResponse(job);
        }

        if (IsTranscriptionAction(job.ActionType))
            orchestrator.Stop(jobId);

        job.Status = AgentJobStatus.Paused;
        AddLog(job, "Job paused.");
        await db.SaveChangesAsync(ct);

        return ToResponse(job);
    }

    /// <summary>
    /// Resume a previously paused job.  For transcription jobs this
    /// restarts the audio capture and inference loop using the original
    /// job parameters.
    /// </summary>
    public async Task<AgentJobResponse?> ResumeAsync(
        Guid jobId, CancellationToken ct = default)
    {
        var job = await LoadJobAsync(jobId, ct);
        if (job is null) return null;

        if (job.Status != AgentJobStatus.Paused)
        {
            AddLog(job, $"Resume rejected: job is {job.Status}, not Paused.", "Warning");
            await db.SaveChangesAsync(ct);
            return ToResponse(job);
        }

        job.Status = AgentJobStatus.Executing;
        AddLog(job, "Job resumed.");
        await db.SaveChangesAsync(ct);

        if (IsTranscriptionAction(job.ActionType))
            await RestartTranscriptionAsync(job, ct);

        return ToResponse(job);
    }

    /// <summary>Retrieve a single job by ID.</summary>
    public async Task<AgentJobResponse?> GetAsync(
        Guid jobId, CancellationToken ct = default)
    {
        var job = await LoadJobAsync(jobId, ct);
        return job is null ? null : ToResponse(job);
    }

    /// <summary>List all jobs for a channel, most recent first.</summary>
    public async Task<IReadOnlyList<AgentJobResponse>> ListAsync(
        Guid channelId, CancellationToken ct = default)
    {
        var jobs = await db.AgentJobs
            .Include(j => j.LogEntries)
            .Include(j => j.TranscriptionSegments.OrderBy(s => s.StartTime))
            .Where(j => j.ChannelId == channelId)
            .OrderByDescending(j => j.CreatedAt)
            .ToListAsync(ct);

        return jobs.Select(ToResponse).ToList();
    }

    /// <summary>
    /// List lightweight summaries for all jobs in a channel, most recent first.
    /// Does not load <c>ResultData</c>, <c>ErrorLog</c>, logs, or segments —
    /// suitable for populating dropdowns or list views without memory pressure.
    /// </summary>
    public async Task<IReadOnlyList<AgentJobSummaryResponse>> ListSummariesAsync(
        Guid channelId, CancellationToken ct = default)
    {
        return await db.AgentJobs
            .Where(j => j.ChannelId == channelId)
            .OrderByDescending(j => j.CreatedAt)
            .Select(j => new AgentJobSummaryResponse(
                j.Id, j.ChannelId, j.AgentId,
                j.ActionType, j.ActionKey, j.ResourceId, j.Status,
                j.CreatedAt, j.StartedAt, j.CompletedAt))
            .ToListAsync(ct);
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

    /// <summary>Returns the session user ID, or <c>null</c> if not authenticated.</summary>
    public Guid? GetSessionUserId() => session.UserId;

    /// <summary>
    /// Evaluates the permission check for an action without creating a job.
    /// Used by the streaming chat loop to determine whether the session
    /// user has authority to approve an awaiting job inline.
    /// </summary>
    public Task<AgentActionResult> CheckPermissionAsync(
        Guid agentId, AgentActionType actionType, Guid? resourceId,
        ActionCaller caller, CancellationToken ct = default,
        string? actionKey = null)
        => DispatchPermissionCheckAsync(agentId, actionType, resourceId, caller, ct, actionKey);

    // ═══════════════════════════════════════════════════════════════
    // Transcription: segments & streaming
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Pushes a new transcription segment into an executing job.
    /// Called by the orchestrator each time a segment is recognised.
    /// </summary>
    public async Task<TranscriptionSegmentResponse?> PushSegmentAsync(
        Guid jobId, string text, double startTime, double endTime,
        double? confidence = null, bool isProvisional = false,
        CancellationToken ct = default)
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
            Timestamp = DateTimeOffset.UtcNow,
            IsProvisional = isProvisional,
        };

        db.TranscriptionSegments.Add(segment);
        await db.SaveChangesAsync(ct);

        var response = new TranscriptionSegmentResponse(
            segment.Id, segment.Text, segment.StartTime, segment.EndTime,
            segment.Confidence, segment.Timestamp, segment.IsProvisional);

        if (_channels.TryGetValue(jobId, out var channel))
            await channel.Writer.WriteAsync(response, ct);

        return response;
    }

    /// <summary>
    /// Promotes a provisional segment to final, optionally updating its
    /// text and confidence.  Pushes the updated segment to streaming
    /// consumers so they can replace the provisional version in-place.
    /// </summary>
    public async Task<TranscriptionSegmentResponse?> FinalizeSegmentAsync(
        Guid jobId, Guid segmentId, string text, double? confidence = null,
        CancellationToken ct = default)
    {
        var segment = await db.TranscriptionSegments
            .FirstOrDefaultAsync(s => s.Id == segmentId && s.AgentJobId == jobId, ct);
        if (segment is null)
            return null;

        segment.Text = text;
        segment.IsProvisional = false;
        segment.Timestamp = DateTimeOffset.UtcNow;
        if (confidence.HasValue)
            segment.Confidence = confidence;

        await db.SaveChangesAsync(ct);

        var response = new TranscriptionSegmentResponse(
            segment.Id, segment.Text, segment.StartTime, segment.EndTime,
            segment.Confidence, segment.Timestamp, IsProvisional: false);

        if (_channels.TryGetValue(jobId, out var channel))
            await channel.Writer.WriteAsync(response, ct);

        return response;
    }

    /// <summary>
    /// Updates the text of a provisional segment without finalizing it.
    /// Used to merge sentence-completion fragments into their parent
    /// provisional instead of emitting a standalone segment.
    /// </summary>
    public async Task<bool> UpdateProvisionalTextAsync(
        Guid jobId, Guid segmentId, string text,
        CancellationToken ct = default)
    {
        var segment = await db.TranscriptionSegments
            .FirstOrDefaultAsync(s => s.Id == segmentId && s.AgentJobId == jobId && s.IsProvisional, ct);
        if (segment is null)
            return false;

        segment.Text = text;
        segment.Timestamp = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        if (_channels.TryGetValue(jobId, out var channel))
        {
            var response = new TranscriptionSegmentResponse(
                segment.Id, segment.Text, segment.StartTime, segment.EndTime,
                segment.Confidence, segment.Timestamp, IsProvisional: true);
            await channel.Writer.WriteAsync(response, ct);
        }

        return true;
    }

    /// <summary>
    /// Removes a provisional segment that was not confirmed by later
    /// inference ticks (likely a hallucination).  Deletes the DB record
    /// and pushes a zero-length "retracted" event so streaming consumers
    /// can remove it.
    /// </summary>
    public async Task RetractSegmentAsync(
        Guid jobId, Guid segmentId, CancellationToken ct = default)
    {
        var segment = await db.TranscriptionSegments
            .FirstOrDefaultAsync(s => s.Id == segmentId && s.AgentJobId == jobId, ct);
        if (segment is null)
            return;

        db.TranscriptionSegments.Remove(segment);
        await db.SaveChangesAsync(ct);

        // Push a tombstone so streaming consumers know to remove it.
        if (_channels.TryGetValue(jobId, out var channel))
        {
            var tombstone = new TranscriptionSegmentResponse(
                segment.Id, string.Empty, segment.StartTime, segment.EndTime,
                Confidence: null, Timestamp: DateTimeOffset.UtcNow,
                IsProvisional: false);
            await channel.Writer.WriteAsync(tombstone, ct);
        }
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
            model = await db.Models
                .Include(m => m.Provider)
                .FirstOrDefaultAsync(m => m.Id == explicitModelId, ct)
                ?? throw new InvalidOperationException($"Model {explicitModelId} not found.");

            if (!model.Capabilities.HasFlag(ModelCapability.Transcription))
                throw new InvalidOperationException(
                    $"Model '{model.Name}' does not have the Transcription capability.");
        }
        else
        {
            // No explicit transcription model — use the agent's own model.
            var agent = await db.Agents
                .Include(a => a.Model).ThenInclude(m => m.Provider)
                .FirstOrDefaultAsync(a => a.Id == job.AgentId, ct)
                ?? throw new InvalidOperationException($"Agent {job.AgentId} not found.");

            model = agent.Model;

            if (!model.Capabilities.HasFlag(ModelCapability.Transcription))
                throw new InvalidOperationException(
                    $"Agent '{agent.Name}' uses model '{model.Name}' which does not have " +
                    $"the Transcription capability. Either assign a transcription model to the " +
                    $"agent or specify one explicitly with '--model <id>'.");
        }

        // Verify the provider has a transcription client implementation.
        if (!orchestrator.SupportsProvider(model.Provider.ProviderType))
            throw new InvalidOperationException(
                $"Provider '{model.Provider.Name}' ({model.Provider.ProviderType}) does not " +
                $"support transcription. Use a provider with a transcription implementation " +
                $"(e.g. OpenAI Whisper, Groq, or a local Whisper GGML model).");

        job.TranscriptionModelId = model.Id;

        var device = await db.AudioDevices.FirstOrDefaultAsync(d => d.Id == job.ResourceId, ct)
            ?? throw new InvalidOperationException("Audio device not found.");

        _channels.TryAdd(job.Id, Channel.CreateUnbounded<TranscriptionSegmentResponse>());

        AddLog(job, $"Transcription started with model '{model.Name}' on device '{device.Name}'.");
        await db.SaveChangesAsync(ct);

        orchestrator.Start(
            job.Id, model.Id, device.DeviceIdentifier, job.Language,
            job.TranscriptionMode, job.WindowSeconds, job.StepSeconds);
    }

    /// <summary>
    /// Restarts the transcription capture loop for a resumed job using
    /// the parameters already persisted on the <see cref="AgentJobDB"/>.
    /// </summary>
    private async Task RestartTranscriptionAsync(AgentJobDB job, CancellationToken ct)
    {
        var modelId = job.TranscriptionModelId
            ?? throw new InvalidOperationException("Paused transcription job has no model.");

        var device = await db.AudioDevices.FirstOrDefaultAsync(d => d.Id == job.ResourceId, ct)
            ?? throw new InvalidOperationException("Audio device not found.");

        _channels.TryAdd(job.Id, Channel.CreateUnbounded<TranscriptionSegmentResponse>());

        orchestrator.Start(
            job.Id, modelId, device.DeviceIdentifier, job.Language,
            job.TranscriptionMode, job.WindowSeconds, job.StepSeconds);
    }

    private async Task<string?> DispatchExecutionAsync(AgentJobDB job, CancellationToken ct)
    {
        return job.ActionType switch
        {
            // Agent lifecycle
            AgentActionType.CreateSubAgent
                => await ExecuteCreateSubAgentAsync(job, ct),
            AgentActionType.ManageAgent
                => await ExecuteManageAgentAsync(job, ct),

            // Task management
            AgentActionType.EditTask
                => await ExecuteEditTaskAsync(job, ct),

            // Knowledge / skills
            AgentActionType.AccessSkill
                => await ExecuteAccessSkillAsync(job, ct),

            // Localhost access
            AgentActionType.AccessLocalhostInBrowser
                => await ExecuteAccessLocalhostInBrowserAsync(job, ct),
            AgentActionType.AccessLocalhostCli
                => await ExecuteAccessLocalhostCliAsync(job, ct),

            // External website access (per-resource: registered website)
            AgentActionType.AccessWebsite
                => await ExecuteAccessWebsiteAsync(job, ct),

            // Search engine query (per-resource: registered search engine)
            AgentActionType.QuerySearchEngine
                => await ExecuteQuerySearchEngineAsync(job, ct),

            // Bot messaging
            AgentActionType.SendBotMessage
                => await ExecuteSendBotMessageAsync(job, ct),

            // Module-provided tool calls — try ActionKey-based dispatch first;
            // fall back to full envelope deserialization.
            AgentActionType.ModuleAction
                => await TryDispatchByActionKeyAsync(job, ct)
                   ?? await DispatchModuleExecutionAsync(job, ct),

            // Default: try module resolution via ActionKey or enum-name derivation.
            _ => await TryDispatchByActionKeyAsync(job, ct)
                 ?? $"Action '{job.ActionType}' executed successfully " +
                    $"(resource: {job.ResourceId?.ToString() ?? "n/a"})."
        };
    }

    // ═══════════════════════════════════════════════════════════════
    // MODULE DISPATCH — executes module tool calls via ModuleRegistry
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Executes a <see cref="AgentActionType.ModuleAction"/> job by
    /// deserializing the <see cref="ModuleEnvelope"/> from <c>ScriptJson</c>,
    /// resolving the target module, and calling
    /// <see cref="ISharpClawModule.ExecuteToolAsync"/> inside a restricted
    /// <see cref="ModuleServiceScope"/> with a per-manifest timeout.
    /// </summary>
    private async Task<string?> DispatchModuleExecutionAsync(
        AgentJobDB job, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(job.ScriptJson))
            throw new InvalidOperationException(
                "Module action requires a ScriptJson envelope.");

        if (job.ScriptJson.Length > SecureJsonOptions.MaxEnvelopeSize)
            throw new InvalidOperationException(
                $"ScriptJson exceeds maximum envelope size ({SecureJsonOptions.MaxEnvelopeSize} bytes).");

        var envelope = JsonSerializer.Deserialize<ModuleEnvelope>(
            job.ScriptJson, SecureJsonOptions.Envelope)
            ?? throw new InvalidOperationException(
                "Failed to deserialize module envelope from ScriptJson.");

        var module = moduleRegistry.GetModule(envelope.Module)
            ?? throw new InvalidOperationException(
                $"Module '{envelope.Module}' is not loaded.");

        var jobContext = new AgentJobContext(
            JobId: job.Id,
            AgentId: job.AgentId,
            ChannelId: job.ChannelId,
            ResourceId: job.ResourceId,
            ActionType: job.ActionType,
            ActionKey: job.ActionKey,
            Language: job.Language);

        // Build a restricted service scope so the module cannot resolve
        // pipeline internals (AgentJobService, ChatService, DbContext, etc.).
        using var scope = serviceScopeFactory.CreateScope();
        var restrictedScope = new ModuleServiceScope(scope.ServiceProvider, module.Id);

        // Timeout: per-tool override → manifest default → 30s.
        var manifest = moduleRegistry.GetManifest(envelope.Module);
        var toolTimeout = moduleRegistry.GetToolTimeout(envelope.Module, envelope.Tool);
        var timeoutSeconds = toolTimeout ?? manifest?.ExecutionTimeoutSeconds ?? 30;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        try
        {
            return await module.ExecuteToolAsync(
                envelope.Tool, envelope.Params, jobContext, restrictedScope, cts.Token);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                $"Module tool '{envelope.Module}.{envelope.Tool}' " +
                $"exceeded timeout ({timeoutSeconds}s).");
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not InvalidOperationException)
        {
            throw new InvalidOperationException(
                ExceptionSanitizer.Sanitize(envelope.Module, envelope.Tool, ex.Message));
        }
    }

    /// <summary>
    /// Permission check for module-provided tool calls.
    /// Resolves the module tool's <see cref="ModuleToolPermission"/> descriptor
    /// and evaluates it:
    /// <list type="bullet">
    ///   <item>If <see cref="ModuleToolPermission.Check"/> is set, calls it directly.</item>
    ///   <item>If <see cref="ModuleToolPermission.DelegateTo"/> is set, routes to the
    ///         named <see cref="AgentActionService"/> method via the delegation map.</item>
    ///   <item>Otherwise, the action is denied (no permission descriptor = no access).</item>
    /// </list>
    /// </summary>
    private async Task<AgentActionResult> DispatchModulePermissionCheckAsync(
        Guid agentId, Guid? resourceId, ActionCaller caller,
        string? actionKey, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(actionKey))
            return AgentActionResult.Denied("Module action requires an ActionKey to resolve permissions.");

        if (!moduleRegistry.TryResolve(actionKey, out var moduleId, out var toolName))
            return AgentActionResult.Denied($"No module registered for tool '{actionKey}'.");

        var descriptor = moduleRegistry.GetPermissionDescriptor(moduleId, toolName);
        if (descriptor is null)
            return AgentActionResult.Denied($"Module tool '{actionKey}' has no permission descriptor.");

        if (descriptor.IsPerResource && !resourceId.HasValue)
            return AgentActionResult.Denied($"ResourceId is required for module tool '{actionKey}'.");

        // Direct callback takes priority.
        if (descriptor.Check is not null)
            return await descriptor.Check(agentId, resourceId, caller, ct);

        // Delegate to a named AgentActionService method.
        if (!string.IsNullOrWhiteSpace(descriptor.DelegateTo))
        {
            var result = actions.TryEvaluateByDelegateNameAsync(
                descriptor.DelegateTo, agentId, resourceId, caller, ct);
            if (result is not null) return await result;

            return AgentActionResult.Denied(
                $"Module tool '{actionKey}' delegates to '{descriptor.DelegateTo}' "
                + "which is not a recognised permission check method.");
        }

        return AgentActionResult.Denied($"Module tool '{actionKey}' has no permission check configured.");
    }

    /// <summary>
    /// Attempts to dispatch a job to a module-provided tool. Uses the explicit
    /// <see cref="AgentJobDB.ActionKey"/> when available; otherwise derives a
    /// candidate key from the <see cref="AgentActionType"/> enum name
    /// (PascalCase → snake_case), so legacy API calls using old enum values
    /// transparently route to modules that register the matching alias.
    /// Returns <c>null</c> if no module owns the resolved tool name.
    /// </summary>
    private async Task<string?> TryDispatchByActionKeyAsync(
        AgentJobDB job, CancellationToken ct)
    {
        var actionKey = job.ActionKey;

        // Derive a candidate key from the enum name when no explicit key is
        // set. Excludes ModuleAction itself — that enum name is not a tool.
        if (string.IsNullOrWhiteSpace(actionKey) && job.ActionType != AgentActionType.ModuleAction)
            actionKey = PascalToSnakeCase(job.ActionType.ToString());

        if (string.IsNullOrWhiteSpace(actionKey))
            return null;

        if (!moduleRegistry.TryResolve(actionKey, out var moduleId, out var toolName))
            return null;

        // Parse the raw ScriptJson into a JsonElement for the envelope params.
        var paramsElement = string.IsNullOrWhiteSpace(job.ScriptJson)
            ? JsonDocument.Parse("{}").RootElement.Clone()
            : JsonDocument.Parse(job.ScriptJson).RootElement.Clone();

        // Build a ModuleEnvelope from the job's existing data.
        var envelope = new ModuleEnvelope(moduleId, toolName, paramsElement);
        var syntheticJson = JsonSerializer.Serialize(envelope, SecureJsonOptions.Envelope);

        // Temporarily patch ScriptJson so DispatchModuleExecutionAsync can deserialize it.
        var original = job.ScriptJson;
        job.ScriptJson = syntheticJson;
        try
        {
            return await DispatchModuleExecutionAsync(job, ct);
        }
        finally
        {
            job.ScriptJson = original;
        }
    }

    /// <summary>
    /// Converts a PascalCase name to snake_case
    /// (e.g. <c>ClickDesktop</c> → <c>click_desktop</c>).
    /// Used to derive module tool candidates from legacy
    /// <see cref="AgentActionType"/> enum names.
    /// </summary>
    private static string PascalToSnakeCase(string name)
    {
        Span<char> buffer = stackalloc char[name.Length * 2];
        var pos = 0;
        for (var i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i])
                       && (char.IsLower(name[i - 1]) || char.IsDigit(name[i - 1])))
                buffer[pos++] = '_';
            buffer[pos++] = char.ToLowerInvariant(name[i]);
        }
        return new string(buffer[..pos]);
    }

    /// <summary>
    /// Returns <c>true</c> when the <paramref name="type"/> enum name
    /// resolves to a module tool via PascalCase → snake_case derivation.
    /// </summary>
    private bool TryResolveModuleByActionType(AgentActionType type)
    {
        if (type == AgentActionType.ModuleAction) return false;
        var derived = PascalToSnakeCase(type.ToString());
        return moduleRegistry.TryResolve(derived, out _, out _);
    }

    // ═══════════════════════════════════════════════════════════════
    // CREATE SUB-AGENT
    // ═══════════════════════════════════════════════════════════════

    private sealed class CreateSubAgentPayload
    {
        public string? Name { get; set; }
        public string? ModelId { get; set; }
        public string? SystemPrompt { get; set; }
    }

    private async Task<string?> ExecuteCreateSubAgentAsync(
        AgentJobDB job, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(job.ScriptJson))
            throw new InvalidOperationException(
                "CreateSubAgent requires a JSON payload in ScriptJson.");

        var payload = JsonSerializer.Deserialize<CreateSubAgentPayload>(
            job.ScriptJson, _payloadJsonOptions)
            ?? throw new InvalidOperationException(
                "Failed to deserialise CreateSubAgent payload.");

        if (string.IsNullOrWhiteSpace(payload.Name))
            throw new InvalidOperationException(
                "CreateSubAgent payload requires a 'name' field.");

        if (!Guid.TryParse(payload.ModelId, out var modelId))
            throw new InvalidOperationException(
                "CreateSubAgent payload requires a valid 'modelId' GUID.");

        var model = await db.Models
            .Include(m => m.Provider)
            .FirstOrDefaultAsync(m => m.Id == modelId, ct)
            ?? throw new InvalidOperationException(
                $"Model {modelId} not found.");

        var agent = new AgentDB
        {
            Name = payload.Name,
            SystemPrompt = payload.SystemPrompt,
            ModelId = model.Id,
        };

        db.Agents.Add(agent);
        await db.SaveChangesAsync(ct);

        AddLog(job, $"Sub-agent '{agent.Name}' created (id={agent.Id}).");
        return $"Created sub-agent '{agent.Name}' (id={agent.Id}, model={model.Name}).";
    }

    // ═══════════════════════════════════════════════════════════════
    // MANAGE AGENT
    // ═══════════════════════════════════════════════════════════════

    private sealed class ManageAgentPayload
    {
        public string? TargetId { get; set; }
        public string? Name { get; set; }
        public string? SystemPrompt { get; set; }
        public string? ModelId { get; set; }
    }

    private async Task<string?> ExecuteManageAgentAsync(
        AgentJobDB job, CancellationToken ct)
    {
        if (!job.ResourceId.HasValue)
            throw new InvalidOperationException(
                "ManageAgent requires a ResourceId (target agent).");

        var agent = await db.Agents
            .Include(a => a.Model).ThenInclude(m => m.Provider)
            .FirstOrDefaultAsync(a => a.Id == job.ResourceId.Value, ct)
            ?? throw new InvalidOperationException(
                $"Agent {job.ResourceId} not found.");

        ManageAgentPayload? payload = null;
        if (!string.IsNullOrWhiteSpace(job.ScriptJson))
        {
            payload = JsonSerializer.Deserialize<ManageAgentPayload>(
                job.ScriptJson, _payloadJsonOptions);
        }

        var changes = new List<string>();

        if (payload?.Name is { } newName && !string.IsNullOrWhiteSpace(newName))
        {
            agent.Name = newName;
            changes.Add($"name='{newName}'");
        }

        if (payload?.SystemPrompt is not null)
        {
            agent.SystemPrompt = payload.SystemPrompt;
            changes.Add("systemPrompt updated");
        }

        if (payload?.ModelId is { } modelIdStr && Guid.TryParse(modelIdStr, out var newModelId))
        {
            var model = await db.Models
                .Include(m => m.Provider)
                .FirstOrDefaultAsync(m => m.Id == newModelId, ct)
                ?? throw new InvalidOperationException($"Model {newModelId} not found.");
            agent.ModelId = model.Id;
            agent.Model = model;
            changes.Add($"model='{model.Name}'");
        }

        if (changes.Count == 0)
            return $"Agent '{agent.Name}' (id={agent.Id}) — no changes applied.";

        await db.SaveChangesAsync(ct);

        var summary = string.Join(", ", changes);
        AddLog(job, $"Agent '{agent.Name}' updated: {summary}.");
        return $"Updated agent '{agent.Name}' (id={agent.Id}): {summary}.";
    }

    // ═══════════════════════════════════════════════════════════════
    // EDIT TASK
    // ═══════════════════════════════════════════════════════════════

    private sealed class EditTaskPayload
    {
        public string? TargetId { get; set; }
        public string? Name { get; set; }
        public int? RepeatIntervalMinutes { get; set; }
        public int? MaxRetries { get; set; }
    }

    private async Task<string?> ExecuteEditTaskAsync(
        AgentJobDB job, CancellationToken ct)
    {
        if (!job.ResourceId.HasValue)
            throw new InvalidOperationException(
                "EditTask requires a ResourceId (target task).");

        var task = await db.ScheduledTasks
            .FirstOrDefaultAsync(t => t.Id == job.ResourceId.Value, ct)
            ?? throw new InvalidOperationException(
                $"ScheduledTask {job.ResourceId} not found.");

        EditTaskPayload? payload = null;
        if (!string.IsNullOrWhiteSpace(job.ScriptJson))
        {
            payload = JsonSerializer.Deserialize<EditTaskPayload>(
                job.ScriptJson, _payloadJsonOptions);
        }

        var changes = new List<string>();

        if (payload?.Name is { } newName && !string.IsNullOrWhiteSpace(newName))
        {
            task.Name = newName;
            changes.Add($"name='{newName}'");
        }

        if (payload?.RepeatIntervalMinutes is { } intervalMinutes)
        {
            task.RepeatInterval = intervalMinutes > 0
                ? TimeSpan.FromMinutes(intervalMinutes)
                : null;
            changes.Add($"repeatInterval={task.RepeatInterval?.ToString() ?? "none"}");
        }

        if (payload?.MaxRetries is { } retries)
        {
            task.MaxRetries = retries;
            changes.Add($"maxRetries={retries}");
        }

        if (changes.Count == 0)
            return $"Task '{task.Name}' (id={task.Id}) — no changes applied.";

        await db.SaveChangesAsync(ct);

        var summary = string.Join(", ", changes);
        AddLog(job, $"Task '{task.Name}' updated: {summary}.");
        return $"Updated task '{task.Name}' (id={task.Id}): {summary}.";
    }

    // ═══════════════════════════════════════════════════════════════
    // ACCESS SKILL
    // ═══════════════════════════════════════════════════════════════

    private async Task<string?> ExecuteAccessSkillAsync(
        AgentJobDB job, CancellationToken ct)
    {
        if (!job.ResourceId.HasValue)
            throw new InvalidOperationException(
                "AccessSkill requires a ResourceId (target skill).");

        var skill = await db.Skills
            .FirstOrDefaultAsync(s => s.Id == job.ResourceId.Value, ct)
            ?? throw new InvalidOperationException(
                $"Skill {job.ResourceId} not found.");

        AddLog(job, $"Skill '{skill.Name}' accessed.");
        return $"Skill: {skill.Name}\n\n{skill.SkillText}";
    }

    // ═══════════════════════════════════════════════════════════════
    // ACCESS LOCALHOST IN BROWSER
    // ═══════════════════════════════════════════════════════════════

    private sealed class AccessLocalhostPayload
    {
        public string? Url { get; set; }
        public string? Mode { get; set; }
    }

    /// <summary>
    /// Launches a headless browser (configured via <c>Browser:Executable</c>
    /// and <c>Browser:Arguments</c> in the SharpClaw .env — defaults to
    /// auto-detected Chrome/Edge on Windows) against a <b>localhost</b> URL
    /// and returns either a screenshot path or the page HTML.
    /// </summary>
    private async Task<string?> ExecuteAccessLocalhostInBrowserAsync(
        AgentJobDB job, CancellationToken ct)
    {
        var payload = DeserializePayload<AccessLocalhostPayload>(job,
            "AccessLocalhostInBrowser");

        var url = ValidateLocalhostUrl(payload.Url);
        var mode = (payload.Mode ?? "html").ToLowerInvariant();

        var executable = configuration["Browser:Executable"] ?? ResolveChromiumExecutable();
        var extraArgs = configuration["Browser:Arguments"] ?? "--incognito";

        // Build headless Chrome/Edge arguments.
        // --dump-dom returns the serialised DOM as text.
        // --screenshot returns a PNG screenshot to a temp file.
        // --ignore-certificate-errors allows self-signed localhost HTTPS.
        // --virtual-time-budget gives the page simulated time (ms) for JS
        //   execution and async fetches before the DOM/screenshot is captured.
        //   Without this, SPA pages (e.g. Swagger UI) render empty because
        //   --dump-dom snapshots before JS has finished loading content.
        var tempFile = mode == "screenshot"
            ? Path.Combine(Path.GetTempPath(), $"sc_{job.Id:N}.png")
            : null;

        var headlessArgs = mode switch
        {
            "screenshot" => $"--headless --disable-gpu --no-sandbox --ignore-certificate-errors --virtual-time-budget=10000 {extraArgs} --screenshot=\"{tempFile}\" \"{url}\"",
            _ => $"--headless --disable-gpu --no-sandbox --ignore-certificate-errors --virtual-time-budget=10000 {extraArgs} --dump-dom \"{url}\"",
        };

        AddLog(job, $"Browser ({mode}): {executable} → {url}");
        await db.SaveChangesAsync(ct);

        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = executable,
            Arguments = headlessArgs,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        using var process = new System.Diagnostics.Process { StartInfo = psi };
        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);

        // 30-second timeout to prevent hanging on unresponsive pages.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
            throw new InvalidOperationException(
                $"Browser timed out after 30 seconds for URL: {url}");
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"Browser exited with code {process.ExitCode}.\nstderr: {stderr}");

        if (mode == "screenshot" && tempFile is not null && File.Exists(tempFile))
        {
            var bytes = await File.ReadAllBytesAsync(tempFile, ct);
            File.Delete(tempFile);
            // Structured marker: ChatService splits on [SCREENSHOT_BASE64] to
            // extract the raw base64 data and send it as a vision content block.
            return $"Screenshot captured ({bytes.Length} bytes) of {url}\n[SCREENSHOT_BASE64]{Convert.ToBase64String(bytes)}";
        }

        return string.IsNullOrWhiteSpace(stdout) ? "(empty page)" : stdout;
    }

    /// <summary>
    /// Probes well-known installation paths for Chromium-based browsers
    /// on Windows (Chrome, Edge) and returns the first existing path.
    /// Falls back to <c>"chrome"</c> on non-Windows or if nothing is found.
    /// </summary>
    private static string ResolveChromiumExecutable()
    {
        if (!OperatingSystem.IsWindows())
            return "google-chrome";

        ReadOnlySpan<string> candidates =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Google", "Chrome", "Application", "chrome.exe"),
            // Microsoft Edge is always present on Windows 10/11.
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Microsoft", "Edge", "Application", "msedge.exe"),
        ];

        foreach (var path in candidates)
        {
            if (File.Exists(path))
                return path;
        }

        return "chrome";
    }

    // ═══════════════════════════════════════════════════════════════
    // ACCESS LOCALHOST CLI
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Makes a direct HTTP request to a <b>localhost</b> URL and returns
    /// the status code, headers, and response body. No browser involved.
    /// </summary>
    private async Task<string?> ExecuteAccessLocalhostCliAsync(
        AgentJobDB job, CancellationToken ct)
    {
        var payload = DeserializePayload<AccessLocalhostPayload>(job,
            "AccessLocalhostCli");

        var url = ValidateLocalhostUrl(payload.Url);

        AddLog(job, $"HTTP GET → {url}");
        await db.SaveChangesAsync(ct);

        using var handler = new HttpClientHandler
        {
            // Localhost URLs may use self-signed development certificates.
            ServerCertificateCustomValidationCallback =
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        };
        using var httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        var response = await httpClient.GetAsync(url, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
        foreach (var header in response.Headers)
            sb.AppendLine($"{header.Key}: {string.Join(", ", header.Value)}");
        foreach (var header in response.Content.Headers)
            sb.AppendLine($"{header.Key}: {string.Join(", ", header.Value)}");
        sb.AppendLine();
        sb.Append(body);

        return sb.ToString();
    }

    // ═══════════════════════════════════════════════════════════════
    // Localhost helpers
    // ═══════════════════════════════════════════════════════════════

    private static T DeserializePayload<T>(AgentJobDB job, string actionName) where T : class
    {
        if (string.IsNullOrWhiteSpace(job.ScriptJson))
            throw new InvalidOperationException(
                $"{actionName} requires a JSON payload in ScriptJson.");

        return JsonSerializer.Deserialize<T>(job.ScriptJson, _payloadJsonOptions)
            ?? throw new InvalidOperationException(
                $"Failed to deserialise {actionName} payload.");
    }

    private static string ValidateLocalhostUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            throw new InvalidOperationException(
                "Localhost access requires a 'url' field.");

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            throw new InvalidOperationException(
                $"Invalid URL: '{url}'.");

        if (uri.Host is not ("localhost" or "127.0.0.1" or "[::1]"))
            throw new InvalidOperationException(
                $"URL host must be localhost, 127.0.0.1, or [::1]. Got: '{uri.Host}'.");

        return url;
    }

    private static readonly JsonSerializerOptions _payloadJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    // ═══════════════════════════════════════════════════════════════
    // QUERY SEARCH ENGINE
    // ═══════════════════════════════════════════════════════════════

    private sealed class QuerySearchEnginePayload
    {
        public string? ResourceId { get; set; }
        public string? TargetId { get; set; }
        public string? Query { get; set; }
        public int? Count { get; set; }
        public int? Offset { get; set; }
        public string? Language { get; set; }
        public string? Region { get; set; }
        public string? SafeSearch { get; set; }
        public string? DateRestrict { get; set; }
        public string? SiteRestrict { get; set; }
        public string? FileType { get; set; }
        public string? ExactTerms { get; set; }
        public string? ExcludeTerms { get; set; }
        public string? SearchType { get; set; }
        public string? SortBy { get; set; }
        public string? Topic { get; set; }
        public string? Category { get; set; }
    }

    /// <summary>
    /// Executes a search query against a registered <see cref="SearchEngineDB"/>.
    /// The engine's <see cref="SearchEngineType"/> determines which API-specific
    /// parameters are forwarded by <see cref="SearchEngineService.QueryAsync"/>.
    /// </summary>
    private async Task<string?> ExecuteQuerySearchEngineAsync(
        AgentJobDB job, CancellationToken ct)
    {
        if (!job.ResourceId.HasValue)
            throw new InvalidOperationException(
                "QuerySearchEngine requires a ResourceId (search engine).");

        var engine = await db.SearchEngines
            .Include(e => e.Skill)
            .FirstOrDefaultAsync(e => e.Id == job.ResourceId.Value, ct)
            ?? throw new InvalidOperationException(
                $"Search engine {job.ResourceId} not found.");

        var payload = DeserializePayload<QuerySearchEnginePayload>(job,
            "QuerySearchEngine");

        if (string.IsNullOrWhiteSpace(payload.Query))
            throw new InvalidOperationException(
                "QuerySearchEngine requires a 'query' field.");

        AddLog(job, $"Search engine '{engine.Name}' ({engine.Type}): {payload.Query}");
        await db.SaveChangesAsync(ct);

        var result = await searchEngineService.QueryAsync(
            engine,
            payload.Query,
            count: payload.Count ?? 10,
            offset: payload.Offset ?? 0,
            language: payload.Language,
            region: payload.Region,
            safeSearch: payload.SafeSearch,
            dateRestrict: payload.DateRestrict,
            siteRestrict: payload.SiteRestrict,
            fileType: payload.FileType,
            exactTerms: payload.ExactTerms,
            excludeTerms: payload.ExcludeTerms,
            searchType: payload.SearchType,
            sortBy: payload.SortBy,
            topic: payload.Topic,
            category: payload.Category,
            ct: ct);

        if (engine.Skill is { SkillText.Length: > 0 } skill)
            result = $"[Search Engine Skill: {skill.Name}]\n{skill.SkillText}\n\n---\n\n{result}";

        return result;
    }

    // ═══════════════════════════════════════════════════════════════
    // ACCESS WEBSITE — external sites, hardened
    // ═══════════════════════════════════════════════════════════════

    private sealed class AccessWebsitePayload
    {
        public string? ResourceId { get; set; }
        public string? TargetId { get; set; }
        public string? Mode { get; set; }
        public string? Path { get; set; }
    }

    /// <summary>
    /// Maximum response body size (2 MB) to prevent memory exhaustion from
    /// malicious or very large external pages.
    /// </summary>
    private const int MaxWebsiteResponseBytes = 2 * 1024 * 1024;

    /// <summary>
    /// Content-Type prefixes that are considered safe to return as text.
    /// Binary types (images, executables, archives, etc.) are blocked.
    /// </summary>
    private static readonly string[] SafeContentTypePrefixes =
    [
        "text/",
        "application/json",
        "application/xml",
        "application/xhtml+xml",
        "application/javascript",
        "application/x-javascript",
    ];

    /// <summary>
    /// Fetches an external website registered in <see cref="WebsiteDB"/>
    /// using either a headless browser (<c>mode=html|screenshot</c>) or
    /// a direct HTTP GET (<c>mode=cli</c>, the default).
    /// <para>
    /// Unlike localhost access, external websites are untrusted.
    /// The following precautions are enforced:
    /// <list type="bullet">
    ///   <item>Only the registered base URL (plus optional <c>path</c>
    ///         suffix) is allowed — agents cannot browse arbitrary
    ///         external sites.</item>
    ///   <item>Responses are capped at <see cref="MaxWebsiteResponseBytes"/>
    ///         to prevent memory exhaustion.</item>
    ///   <item>Binary content types are rejected — only text-based
    ///         responses are returned to the agent.</item>
    ///   <item>Browser mode uses <c>--disable-downloads</c> to prevent
    ///         the headless browser from saving files to disk.</item>
    ///   <item>Redirect chains are limited to 10 hops and must stay
    ///         within the registered origin (scheme + host + port).</item>
    ///   <item>Private/loopback IPs are blocked to prevent SSRF.</item>
    ///   <item>30-second hard timeout kills runaway requests.</item>
    /// </list>
    /// </para>
    /// </summary>
    private async Task<string?> ExecuteAccessWebsiteAsync(
        AgentJobDB job, CancellationToken ct)
    {
        if (!job.ResourceId.HasValue)
            throw new InvalidOperationException(
                "AccessWebsite requires a ResourceId (Website).");

        var website = await db.Websites
            .Include(w => w.Skill)
            .FirstOrDefaultAsync(w => w.Id == job.ResourceId.Value, ct)
            ?? throw new InvalidOperationException(
                $"Website {job.ResourceId} not found.");

        var payload = DeserializePayload<AccessWebsitePayload>(job, "AccessWebsite");
        var mode = (payload.Mode ?? "cli").ToLowerInvariant();

        // Build the final URL: registered base + optional path suffix.
        var url = BuildWebsiteUrl(website.Url, payload.Path);

        // Validate the resolved URL is safe for external access.
        ValidateExternalUrl(url, website.Url);

        AddLog(job, $"Website '{website.Name}' ({mode}): {url}");
        await db.SaveChangesAsync(ct);

        string? result = mode switch
        {
            "html" or "screenshot"
                => await ExecuteAccessWebsiteBrowserAsync(job, url, mode, ct),
            _ => await ExecuteAccessWebsiteCliAsync(url, ct),
        };

        // Prepend skill instructions when available so the agent knows
        // how to interpret the website's structure and navigation.
        if (website.Skill is { SkillText.Length: > 0 } skill)
            result = $"[Website Skill: {skill.Name}]\n{skill.SkillText}\n\n---\n\n{result}";

        return result;
    }

    /// <summary>
    /// Browser-based external website access.  Identical to the localhost
    /// browser implementation but with hardened flags for untrusted sites.
    /// </summary>
    private async Task<string?> ExecuteAccessWebsiteBrowserAsync(
        AgentJobDB job, string url, string mode, CancellationToken ct)
    {
        var executable = configuration["Browser:Executable"] ?? ResolveChromiumExecutable();
        var extraArgs = configuration["Browser:Arguments"] ?? "--incognito";

        var tempFile = mode == "screenshot"
            ? System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"sc_web_{job.Id:N}.png")
            : null;

        // Security: external sites get stricter flags than localhost.
        //   --disable-downloads           — block all file downloads
        //   --disable-extensions          — no extension side-effects
        //   --disable-plugins             — no NPAPI/PPAPI plugins
        //   --disable-popup-blocking      — *off* so popups don't spawn
        //   --no-first-run               — skip Chrome first-run experience
        //   --disable-background-networking — reduce background traffic
        //   --disable-default-apps        — no default app installs
        //   --disable-sync               — no account sync
        //
        // NOTE: --ignore-certificate-errors is intentionally OMITTED for
        // external sites — a bad cert is a genuine red flag.
        var securityFlags = "--disable-downloads --disable-extensions --disable-plugins " +
                            "--no-first-run --disable-background-networking " +
                            "--disable-default-apps --disable-sync";

        var headlessArgs = mode switch
        {
            "screenshot" =>
                $"--headless --disable-gpu --no-sandbox --virtual-time-budget=10000 " +
                $"{securityFlags} {extraArgs} --screenshot=\"{tempFile}\" \"{url}\"",
            _ =>
                $"--headless --disable-gpu --no-sandbox --virtual-time-budget=10000 " +
                $"{securityFlags} {extraArgs} --dump-dom \"{url}\"",
        };

        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = executable,
            Arguments = headlessArgs,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        using var process = new System.Diagnostics.Process { StartInfo = psi };
        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
            throw new InvalidOperationException(
                $"Browser timed out after 30 seconds for URL: {url}");
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"Browser exited with code {process.ExitCode}.\nstderr: {stderr}");

        if (mode == "screenshot" && tempFile is not null && File.Exists(tempFile))
        {
            var bytes = await File.ReadAllBytesAsync(tempFile, ct);
            File.Delete(tempFile);
            return $"Screenshot captured ({bytes.Length} bytes) of {url}\n[SCREENSHOT_BASE64]{Convert.ToBase64String(bytes)}";
        }

        return string.IsNullOrWhiteSpace(stdout) ? "(empty page)" : stdout;
    }

    /// <summary>
    /// Direct HTTP GET for an external website. Enforces size limits,
    /// content-type allow-listing, redirect origin pinning, and SSRF
    /// protection.
    /// </summary>
    private async Task<string?> ExecuteAccessWebsiteCliAsync(
        string url, CancellationToken ct)
    {
        // ── Handler: redirect pinning + SSRF protection ──────────
        var allowedOrigin = new Uri(url).GetLeftPart(UriPartial.Authority);

        using var handler = new HttpClientHandler
        {
            MaxAutomaticRedirections = 10,
            AllowAutoRedirect = false, // we follow redirects manually
        };

        using var httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(30),
            MaxResponseContentBufferSize = MaxWebsiteResponseBytes,
        };

        // Set a realistic user-agent so sites don't block us outright.
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (compatible; SharpClaw/1.0; +https://github.com/mkn8rn/SharpClaw)");

        // ── Manual redirect loop with origin pinning ─────────────
        var currentUrl = url;
        HttpResponseMessage response;
        var redirectCount = 0;
        const int maxRedirects = 10;

        do
        {
            var requestUri = new Uri(currentUrl);
            RejectPrivateAddress(requestUri);

            response = await httpClient.GetAsync(
                requestUri, HttpCompletionOption.ResponseHeadersRead, ct);

            if ((int)response.StatusCode is >= 300 and < 400
                && response.Headers.Location is { } location)
            {
                var redirectUri = location.IsAbsoluteUri
                    ? location
                    : new Uri(requestUri, location);

                // Pin redirects to the original origin to prevent open-redirect SSRF.
                if (!string.Equals(
                        redirectUri.GetLeftPart(UriPartial.Authority),
                        allowedOrigin, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        $"Redirect to a different origin is blocked: {redirectUri}");

                currentUrl = redirectUri.AbsoluteUri;
                response.Dispose();
                redirectCount++;
            }
            else
            {
                break;
            }
        } while (redirectCount < maxRedirects);

        if (redirectCount >= maxRedirects)
            throw new InvalidOperationException(
                $"Too many redirects ({maxRedirects}) for URL: {url}");

        // ── Content-type guard: reject binary payloads ────────────
        var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
        var isSafeContent = SafeContentTypePrefixes.Any(
            prefix => contentType.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

        if (!isSafeContent)
        {
            response.Dispose();
            throw new InvalidOperationException(
                $"Blocked: content type '{contentType}' is not text-based. " +
                "Binary downloads are not permitted.");
        }

        // ── Read body with size cap ──────────────────────────────
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);
        var buffer = new char[MaxWebsiteResponseBytes / sizeof(char)];
        var charsRead = await reader.ReadBlockAsync(buffer, 0, buffer.Length);
        var body = new string(buffer, 0, charsRead);

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
        foreach (var header in response.Headers)
            sb.AppendLine($"{header.Key}: {string.Join(", ", header.Value)}");
        foreach (var header in response.Content.Headers)
            sb.AppendLine($"{header.Key}: {string.Join(", ", header.Value)}");
        sb.AppendLine();
        sb.Append(body);

        if (!reader.EndOfStream)
            sb.AppendLine("\n\n[TRUNCATED — response exceeded 2 MB limit]");

        return sb.ToString();
    }

    // ── Website helpers ──────────────────────────────────────────

    /// <summary>
    /// Builds the final URL by combining the website's registered base URL
    /// with an optional agent-specified path suffix.
    /// </summary>
    private static string BuildWebsiteUrl(string baseUrl, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return baseUrl;

        // Prevent path traversal and injection.
        if (path.Contains("..", StringComparison.Ordinal))
            throw new InvalidOperationException(
                "Path traversal ('..') is not permitted.");

        // Strip leading slash to avoid double-slash.
        var trimmedBase = baseUrl.TrimEnd('/');
        var trimmedPath = path.TrimStart('/');

        return $"{trimmedBase}/{trimmedPath}";
    }

    /// <summary>
    /// Validates that the resolved URL is safe for external access:
    /// must be http/https, must share the same origin as the registered
    /// website, and must not target private/loopback addresses.
    /// </summary>
    private static void ValidateExternalUrl(string url, string registeredBaseUrl)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            throw new InvalidOperationException($"Invalid URL: '{url}'.");

        if (uri.Scheme is not ("http" or "https"))
            throw new InvalidOperationException(
                $"Only http/https schemes are allowed. Got: '{uri.Scheme}'.");

        // The resolved URL must stay within the registered website's origin.
        if (!Uri.TryCreate(registeredBaseUrl, UriKind.Absolute, out var baseUri))
            throw new InvalidOperationException(
                $"Registered website has an invalid base URL: '{registeredBaseUrl}'.");

        if (!string.Equals(
                uri.GetLeftPart(UriPartial.Authority),
                baseUri.GetLeftPart(UriPartial.Authority),
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"URL origin does not match the registered website. " +
                $"Expected: {baseUri.GetLeftPart(UriPartial.Authority)}, " +
                $"got: {uri.GetLeftPart(UriPartial.Authority)}.");

        RejectPrivateAddress(uri);
    }

    /// <summary>
    /// Blocks requests to loopback and private IP ranges to prevent SSRF.
    /// External website access must never target internal infrastructure.
    /// </summary>
    private static void RejectPrivateAddress(Uri uri)
    {
        if (uri.Host is "localhost" or "127.0.0.1" or "[::1]")
            throw new InvalidOperationException(
                "External website access cannot target localhost. " +
                "Use the localhost tools instead.");

        if (System.Net.IPAddress.TryParse(uri.Host, out var ip))
        {
            if (System.Net.IPAddress.IsLoopback(ip))
                throw new InvalidOperationException(
                    $"Blocked: loopback address '{uri.Host}'.");

            // RFC 1918 / RFC 4193 private ranges
            var bytes = ip.GetAddressBytes();
            var isPrivate = bytes switch
            {
                [10, ..] => true,                                      // 10.0.0.0/8
                [172, >= 16 and <= 31, ..] => true,                    // 172.16.0.0/12
                [192, 168, ..] => true,                                // 192.168.0.0/16
                [169, 254, ..] => true,                                // 169.254.0.0/16 (link-local)
                [0, ..] => true,                                       // 0.0.0.0/8
                _ => false,
            };

            if (isPrivate)
                throw new InvalidOperationException(
                    $"Blocked: private/reserved IP address '{uri.Host}'.");
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // BOT MESSAGING
    // ═══════════════════════════════════════════════════════════════

    private sealed class SendBotMessagePayload
    {
        public string? ResourceId { get; set; }
        public string? RecipientId { get; set; }
        public string? Message { get; set; }
        public string? Subject { get; set; }
    }

    private async Task<string?> ExecuteSendBotMessageAsync(
        AgentJobDB job, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(job.ScriptJson))
            throw new InvalidOperationException(
                "SendBotMessage requires a JSON payload in ScriptJson.");

        var payload = JsonSerializer.Deserialize<SendBotMessagePayload>(
            job.ScriptJson, _payloadJsonOptions)
            ?? throw new InvalidOperationException(
                "Failed to deserialise SendBotMessage payload.");

        if (string.IsNullOrWhiteSpace(payload.RecipientId))
            throw new InvalidOperationException(
                "SendBotMessage payload requires a 'recipientId' field.");

        if (string.IsNullOrWhiteSpace(payload.Message))
            throw new InvalidOperationException(
                "SendBotMessage payload requires a 'message' field.");

        if (!job.ResourceId.HasValue)
            throw new InvalidOperationException(
                "SendBotMessage requires a ResourceId (bot integration ID).");

        AddLog(job, $"Sending bot message via integration {job.ResourceId}");
        await db.SaveChangesAsync(ct);

        await botMessageSender.SendMessageAsync(
            job.ResourceId.Value, payload.RecipientId, payload.Message,
            payload.Subject, ct);

        return $"Message sent successfully via bot integration {job.ResourceId} to recipient '{payload.RecipientId}'.";
    }

    private Dictionary<string, object?>? ParsePayload(AgentJobDB job)
    {
        if (string.IsNullOrWhiteSpace(job.ScriptJson))
            return null;
        return JsonSerializer.Deserialize<Dictionary<string, object?>>(
            job.ScriptJson, _payloadJsonOptions);
    }

    private static string? GetString(Dictionary<string, object?>? p, string key) =>
        p?.GetValueOrDefault(key)?.ToString();

    private static int? GetInt(Dictionary<string, object?>? p, string key)
    {
        var val = p?.GetValueOrDefault(key);
        return val switch
        {
            JsonElement je when je.ValueKind == JsonValueKind.Number => je.GetInt32(),
            _ when val is not null && int.TryParse(val.ToString(), out var i) => i,
            _ => null,
        };
    }

    private static bool? GetBool(Dictionary<string, object?>? p, string key)
    {
        var val = p?.GetValueOrDefault(key);
        return val switch
        {
            JsonElement je when je.ValueKind == JsonValueKind.True => true,
            JsonElement je when je.ValueKind == JsonValueKind.False => false,
            _ when val is not null && bool.TryParse(val.ToString(), out var b) => b,
            _ => null,
        };
    }

    // ═══════════════════════════════════════════════════════════════
    // Permission dispatch
    // ═══════════════════════════════════════════════════════════════

    private Task<AgentActionResult> DispatchPermissionCheckAsync(
        Guid agentId, AgentActionType actionType, Guid? resourceId,
        ActionCaller caller, CancellationToken ct,
        string? actionKey = null)
    {
        return actionType switch
        {
            AgentActionType.CreateSubAgent
                => actions.CreateSubAgentAsync(agentId, caller, ct: ct),
            AgentActionType.RegisterDatabase
                => actions.RegisterDatabaseAsync(agentId, caller, ct: ct),
            AgentActionType.AccessLocalhostInBrowser
                => actions.AccessLocalhostInBrowserAsync(agentId, caller, ct: ct),
            AgentActionType.AccessLocalhostCli
                => actions.AccessLocalhostCliAsync(agentId, caller, ct: ct),
            AgentActionType.AccessInternalDatabases when resourceId.HasValue
                => actions.AccessInternalDatabaseAsync(agentId, resourceId.Value, caller, ct: ct),
            AgentActionType.AccessExternalDatabase when resourceId.HasValue
                => actions.AccessExternalDatabaseAsync(agentId, resourceId.Value, caller, ct: ct),
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
            AgentActionType.SendBotMessage when resourceId.HasValue
                => actions.AccessBotIntegrationAsync(agentId, resourceId.Value, caller, ct: ct),
            // Module-provided tool calls: delegate to the module's permission descriptor.
            AgentActionType.ModuleAction
                => DispatchModulePermissionCheckAsync(agentId, resourceId, caller, actionKey, ct),

            // Module fallback: legacy enum values whose core handler was removed
            // route to the owning module's permission check via enum-name derivation.
            _ when TryResolveModuleByActionType(actionType)
                => DispatchModulePermissionCheckAsync(agentId, resourceId, caller,
                    actionKey ?? PascalToSnakeCase(actionType.ToString()), ct),

            _ when IsPerResourceAction(actionType) && !resourceId.HasValue
                => Task.FromResult(AgentActionResult.Denied($"ResourceId is required for {actionType}.")),
            _ => Task.FromResult(AgentActionResult.Denied($"Unknown action type: {actionType}."))
        };
    }

    /// <summary>
    /// Determines whether the given action type requires a per-resource grant.
    /// Checks the hardcoded core list first, then falls back to module registry
    /// resolution via explicit <paramref name="actionKey"/> or enum-name derivation.
    /// </summary>
    private bool IsPerResourceAction(AgentActionType type, string? actionKey = null)
    {
        // Core per-resource actions — checked first so module resolution
        // cannot override behaviour for actions that still have core handlers.
        if (type is AgentActionType.AccessInternalDatabases
            or AgentActionType.AccessExternalDatabase
            or AgentActionType.AccessWebsite
            or AgentActionType.QuerySearchEngine
            or AgentActionType.AccessContainer
            or AgentActionType.ManageAgent
            or AgentActionType.EditTask
            or AgentActionType.AccessSkill
            or AgentActionType.TranscribeFromAudioDevice
            or AgentActionType.TranscribeFromAudioStream
            or AgentActionType.TranscribeFromAudioFile
            or AgentActionType.SendBotMessage)
            return true;

        // Module fallback: explicit ActionKey or enum-name derivation.
        var key = actionKey;
        if (string.IsNullOrWhiteSpace(key) && type != AgentActionType.ModuleAction)
            key = PascalToSnakeCase(type.ToString());

        if (!string.IsNullOrWhiteSpace(key)
            && moduleRegistry.TryResolve(key, out var moduleId, out var toolName))
        {
            var descriptor = moduleRegistry.GetPermissionDescriptor(moduleId, toolName);
            return descriptor?.IsPerResource ?? false;
        }

        return false;
    }

    // ═══════════════════════════════════════════════════════════════
    // Default resource resolution
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Resolves the default resource ID for a per-resource action.
    /// Priority: channel DefaultResourceSet → context DefaultResourceSet
    /// → channel PermissionSet → context PermissionSet → agent role
    /// PermissionSet.
    /// </summary>
    private async Task<Guid?> ResolveDefaultResourceIdAsync(
        AgentActionType actionType, Guid channelId, Guid agentId,
        CancellationToken ct)
    {
        var ch = await db.Channels
            .Include(c => c.DefaultResourceSet)
            .Include(c => c.AgentContext).ThenInclude(ctx => ctx!.DefaultResourceSet)
            .Include(c => c.AgentContext)
            .FirstOrDefaultAsync(c => c.Id == channelId, ct);

        // 1. Channel's DefaultResourceSet
        if (ch?.DefaultResourceSet is { } chDrs)
        {
            var id = ExtractFromDefaultResourceSet(chDrs, actionType);
            if (id.HasValue) return id;
        }

        // 2. Context's DefaultResourceSet
        if (ch?.AgentContext?.DefaultResourceSet is { } ctxDrs)
        {
            var id = ExtractFromDefaultResourceSet(ctxDrs, actionType);
            if (id.HasValue) return id;
        }

        // 3. Fall back to permission set defaults (channel → context → role).
        var permissionSetIds = new List<Guid>(3);

        if (ch?.PermissionSetId is { } chPsId)
            permissionSetIds.Add(chPsId);

        if (ch?.AgentContext?.PermissionSetId is { } ctxPsId)
            permissionSetIds.Add(ctxPsId);

        var agent = await db.Agents
            .Include(a => a.Role)
            .FirstOrDefaultAsync(a => a.Id == agentId, ct);

        if (agent?.Role?.PermissionSetId is { } rolePsId)
            permissionSetIds.Add(rolePsId);

        if (permissionSetIds.Count == 0)
            return null;

        var permissionSets = await db.PermissionSets
            .Where(p => permissionSetIds.Contains(p.Id))
            .Include(p => p.DefaultInternalDatabaseAccess)
            .Include(p => p.DefaultExternalDatabaseAccess)
            .Include(p => p.DefaultWebsiteAccess)
            .Include(p => p.DefaultSearchEngineAccess)
            .Include(p => p.DefaultContainerAccess)
            .Include(p => p.DefaultAudioDeviceAccess)
            .Include(p => p.DefaultDisplayDeviceAccess)
            .Include(p => p.DefaultEditorSessionAccess)
            .Include(p => p.DefaultAgentPermission)
            .Include(p => p.DefaultTaskPermission)
            .Include(p => p.DefaultSkillPermission)
            .Include(p => p.DefaultBotIntegrationAccess)
            .Include(p => p.DefaultDocumentSessionAccess)
            .Include(p => p.DefaultNativeApplicationAccess)
            .ToListAsync(ct);

        foreach (var psId in permissionSetIds)
        {
            var ps = permissionSets.FirstOrDefault(p => p.Id == psId);
            if (ps is null) continue;

            var resourceId = ExtractDefaultResourceId(ps, actionType);
            if (resourceId.HasValue)
                return resourceId;
        }

        return null;
    }

    /// <summary>
    /// Resolves the default transcription model from the channel/context
    /// <see cref="DefaultResourceSetDB"/>.
    /// </summary>
    private async Task<Guid?> ResolveDefaultTranscriptionModelAsync(
        Guid channelId, CancellationToken ct)
    {
        var ch = await db.Channels
            .Include(c => c.DefaultResourceSet)
            .Include(c => c.AgentContext).ThenInclude(ctx => ctx!.DefaultResourceSet)
            .FirstOrDefaultAsync(c => c.Id == channelId, ct);

        if (ch?.DefaultResourceSet?.TranscriptionModelId is { } chModel)
            return chModel;

        if (ch?.AgentContext?.DefaultResourceSet?.TranscriptionModelId is { } ctxModel)
            return ctxModel;

        return null;
    }

    private static Guid? ExtractFromDefaultResourceSet(
        DefaultResourceSetDB drs, AgentActionType actionType) => actionType switch
    {
        AgentActionType.AccessInternalDatabases => drs.InternalDatabaseResourceId,
        AgentActionType.AccessExternalDatabase => drs.ExternalDatabaseResourceId,
        AgentActionType.AccessWebsite => drs.WebsiteResourceId,
        AgentActionType.QuerySearchEngine => drs.SearchEngineResourceId,
        AgentActionType.AccessContainer => drs.ContainerResourceId,
        AgentActionType.ManageAgent => drs.AgentResourceId,
        AgentActionType.EditTask => drs.TaskResourceId,
        AgentActionType.AccessSkill => drs.SkillResourceId,
        AgentActionType.TranscribeFromAudioDevice or
        AgentActionType.TranscribeFromAudioStream or
        AgentActionType.TranscribeFromAudioFile => drs.AudioDeviceResourceId,
        AgentActionType.CaptureDisplay or
        AgentActionType.ClickDesktop or
        AgentActionType.TypeOnDesktop => drs.DisplayDeviceResourceId,
        AgentActionType.SendBotMessage => drs.BotIntegrationResourceId,
        AgentActionType.LaunchNativeApplication => drs.NativeApplicationResourceId,
        _ => null,
    };

    /// <summary>
    /// Returns the resource ID from the matching default access entry on
    /// a permission set, or <c>null</c> if no default is configured.
    /// </summary>
    private static Guid? ExtractDefaultResourceId(
        PermissionSetDB permissionSet, AgentActionType actionType) => actionType switch
    {
        AgentActionType.AccessInternalDatabases
            => permissionSet.DefaultInternalDatabaseAccess?.InternalDatabaseId,
        AgentActionType.AccessExternalDatabase
            => permissionSet.DefaultExternalDatabaseAccess?.ExternalDatabaseId,
        AgentActionType.AccessWebsite
            => permissionSet.DefaultWebsiteAccess?.WebsiteId,
        AgentActionType.QuerySearchEngine
            => permissionSet.DefaultSearchEngineAccess?.SearchEngineId,
        AgentActionType.AccessContainer
            => permissionSet.DefaultContainerAccess?.ContainerId,
        AgentActionType.ManageAgent
            => permissionSet.DefaultAgentPermission?.AgentId,
        AgentActionType.EditTask
            => permissionSet.DefaultTaskPermission?.ScheduledTaskId,
        AgentActionType.AccessSkill
            => permissionSet.DefaultSkillPermission?.SkillId,
        AgentActionType.TranscribeFromAudioDevice or
        AgentActionType.TranscribeFromAudioStream or
        AgentActionType.TranscribeFromAudioFile
            => permissionSet.DefaultAudioDeviceAccess?.AudioDeviceId,
        AgentActionType.CaptureDisplay or
        AgentActionType.ClickDesktop or
        AgentActionType.TypeOnDesktop
            => permissionSet.DefaultDisplayDeviceAccess?.DisplayDeviceId,
        AgentActionType.SendBotMessage
            => permissionSet.DefaultBotIntegrationAccess?.BotIntegrationId,
        AgentActionType.LaunchNativeApplication
            => permissionSet.DefaultNativeApplicationAccess?.NativeApplicationId,
        _ => null,
    };

    // ═══════════════════════════════════════════════════════════════
    // Channel / context pre-authorisation
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Checks whether the channel (or its parent context) has a
    /// user-defined permission set that pre-authorises the requested
    /// action.
    /// <para>
    /// Channel pre-auth provides
    /// <see cref="PermissionClearance.ApprovedByWhitelistedUser"/>-level
    /// authority — the user who configured the channel/context PS is
    /// treated as a whitelisted user granting approval in advance.
    /// </para>
    /// <para>
    /// <b>Level 1 (<see cref="PermissionClearance.ApprovedBySameLevelUser"/>):</b>
    /// the channel PS must contain the grant <b>and</b> the session
    /// user (<paramref name="callerUserId"/>) must personally hold the
    /// same permission (with any non-<see cref="PermissionClearance.Unset"/>
    /// clearance) via their own role.
    /// </para>
    /// <para>
    /// <b>Levels 2 and 4:</b> the channel/context PS grant alone
    /// is sufficient — no additional user check is needed.
    /// </para>
    /// <para>
    /// <b>Level 3 (<see cref="PermissionClearance.ApprovedByPermittedAgent"/>):</b>
    /// agent-only — channel pre-auth is never accepted.
    /// </para>
    /// </summary>
    private async Task<bool> HasChannelAuthorizationAsync(
        Guid channelId,
        AgentActionType actionType,
        Guid? resourceId,
        PermissionClearance agentClearance,
        Guid? callerUserId,
        CancellationToken ct,
        string? actionKey = null)
    {
        // Level 3 is agent-only — no user/channel pre-auth applies.
        if (agentClearance is not (PermissionClearance.ApprovedBySameLevelUser
                                or PermissionClearance.ApprovedByWhitelistedUser
                                or PermissionClearance.ApprovedByWhitelistedAgent))
            return false;

        // Level 1: the session user must personally hold the permission.
        // Verify via the user's own role PS before checking the channel PS.
        if (agentClearance == PermissionClearance.ApprovedBySameLevelUser)
        {
            if (callerUserId is null)
                return false;

            var user = await db.Users
                .Include(u => u.Role)
                .FirstOrDefaultAsync(u => u.Id == callerUserId, ct);

            if (user?.Role?.PermissionSetId is not { } userPsId)
                return false;

            var userPs = await actions.LoadPermissionSetAsync(userPsId, ct);
            if (userPs is null || !HasMatchingGrant(userPs, actionType, resourceId, actionKey))
                return false;
        }

        var ch = await db.Channels
            .Include(c => c.AgentContext)
            .FirstOrDefaultAsync(c => c.Id == channelId, ct);
        if (ch is null) return false;

        // Channel PS first.
        if (ch.PermissionSetId is { } chPsId)
        {
            var chPs = await actions.LoadPermissionSetAsync(chPsId, ct);
            if (chPs is not null && HasMatchingGrant(chPs, actionType, resourceId, actionKey))
                return true;
        }

        // Channel didn't have it — fall through to context.
        if (ch.AgentContext?.PermissionSetId is { } ctxPsId)
        {
            var ctxPs = await actions.LoadPermissionSetAsync(ctxPsId, ct);
            if (ctxPs is not null && HasMatchingGrant(ctxPs, actionType, resourceId, actionKey))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Returns <c>true</c> when the permission set contains a grant
    /// that covers the given action type (and resource, for per-resource
    /// actions).  Wildcard grants (<see cref="WellKnownIds.AllResources"/>)
    /// match any resource.  The clearance value on the grant is
    /// irrelevant — only existence matters.
    /// For module actions, resolves the tool's <see cref="ModuleToolPermission.DelegateTo"/>
    /// and checks the corresponding grant collection.
    /// </summary>
    private bool HasMatchingGrant(
        PermissionSetDB ps, AgentActionType actionType, Guid? resourceId,
        string? actionKey = null) => actionType switch
    {
        // ── Global flags ──────────────────────────────────────────
        AgentActionType.CreateSubAgent    => ps.CanCreateSubAgents,
        AgentActionType.RegisterDatabase => ps.CanRegisterDatabases,
        AgentActionType.AccessLocalhostInBrowser => ps.CanAccessLocalhostInBrowser,
        AgentActionType.AccessLocalhostCli       => ps.CanAccessLocalhostCli,

        // ── Per-resource grants ───────────────────────────────────
        AgentActionType.AccessInternalDatabases when resourceId.HasValue
            => ps.InternalDatabaseAccesses.Any(a =>
                a.InternalDatabaseId == resourceId || a.InternalDatabaseId == WellKnownIds.AllResources),

        AgentActionType.AccessExternalDatabase when resourceId.HasValue
            => ps.ExternalDatabaseAccesses.Any(a =>
                a.ExternalDatabaseId == resourceId || a.ExternalDatabaseId == WellKnownIds.AllResources),

        AgentActionType.AccessWebsite when resourceId.HasValue
            => ps.WebsiteAccesses.Any(a =>
                a.WebsiteId == resourceId || a.WebsiteId == WellKnownIds.AllResources),

        AgentActionType.QuerySearchEngine when resourceId.HasValue
            => ps.SearchEngineAccesses.Any(a =>
                a.SearchEngineId == resourceId || a.SearchEngineId == WellKnownIds.AllResources),

        AgentActionType.AccessContainer when resourceId.HasValue
            => ps.ContainerAccesses.Any(a =>
                a.ContainerId == resourceId || a.ContainerId == WellKnownIds.AllResources),

        AgentActionType.ManageAgent when resourceId.HasValue
            => ps.AgentPermissions.Any(a =>
                a.AgentId == resourceId || a.AgentId == WellKnownIds.AllResources),

        AgentActionType.EditTask when resourceId.HasValue
            => ps.TaskPermissions.Any(a =>
                a.ScheduledTaskId == resourceId || a.ScheduledTaskId == WellKnownIds.AllResources),

        AgentActionType.AccessSkill when resourceId.HasValue
            => ps.SkillPermissions.Any(a =>
                a.SkillId == resourceId || a.SkillId == WellKnownIds.AllResources),

        AgentActionType.TranscribeFromAudioDevice or
        AgentActionType.TranscribeFromAudioStream or
        AgentActionType.TranscribeFromAudioFile when resourceId.HasValue
            => ps.AudioDeviceAccesses.Any(a =>
                a.AudioDeviceId == resourceId || a.AudioDeviceId == WellKnownIds.AllResources),

        AgentActionType.CaptureDisplay or
        AgentActionType.ClickDesktop or
        AgentActionType.TypeOnDesktop when resourceId.HasValue
            => ps.DisplayDeviceAccesses.Any(a =>
                a.DisplayDeviceId == resourceId || a.DisplayDeviceId == WellKnownIds.AllResources),

        AgentActionType.SendBotMessage when resourceId.HasValue
            => ps.BotIntegrationAccesses.Any(a =>
                a.BotIntegrationId == resourceId || a.BotIntegrationId == WellKnownIds.AllResources),

        // Module-provided tool calls: resolve the tool's DelegateTo and
        // check the corresponding grant collection.
        AgentActionType.ModuleAction when !string.IsNullOrWhiteSpace(actionKey)
            => ResolveModuleGrantCheck(ps, actionKey, resourceId),
        _ when !string.IsNullOrWhiteSpace(actionKey) && TryResolveModuleByActionType(actionType)
            => ResolveModuleGrantCheck(ps, PascalToSnakeCase(actionType.ToString()), resourceId),

        _ => false,
    };

    /// <summary>
    /// Resolves a module tool's <see cref="ModuleToolPermission.DelegateTo"/>
    /// and checks whether the permission set contains the corresponding grant.
    /// </summary>
    private bool ResolveModuleGrantCheck(PermissionSetDB ps, string actionKey, Guid? resourceId)
    {
        if (!moduleRegistry.TryResolve(actionKey, out var moduleId, out var toolName))
            return false;

        var descriptor = moduleRegistry.GetPermissionDescriptor(moduleId, toolName);
        if (descriptor is null || string.IsNullOrWhiteSpace(descriptor.DelegateTo))
            return false;

        return AgentActionService.HasGrantByDelegateName(ps, descriptor.DelegateTo, resourceId);
    }

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
            ChannelId: job.ChannelId,
            AgentId: job.AgentId,
            ActionType: job.ActionType,
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
            DangerousShellType: job.DangerousShellType,
            SafeShellType: job.SafeShellType,
            ScriptJson: job.ScriptJson,
            WorkingDirectory: job.WorkingDirectory,
            TranscriptionModelId: job.TranscriptionModelId,
            Language: job.Language,
            TranscriptionMode: job.TranscriptionMode,
            WindowSeconds: job.WindowSeconds,
            StepSeconds: job.StepSeconds,
            Segments: IsTranscriptionAction(job.ActionType)
                ? job.TranscriptionSegments
                    .OrderBy(s => s.StartTime)
                    .Select(s => new TranscriptionSegmentResponse(
                        s.Id, s.Text, s.StartTime, s.EndTime, s.Confidence, s.Timestamp,
                        s.IsProvisional))
                    .ToList()
                : null);
}
