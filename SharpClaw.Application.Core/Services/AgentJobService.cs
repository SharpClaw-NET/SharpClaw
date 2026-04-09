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
ILiveTranscriptionOrchestrator orchestrator,
SessionService session,
DocumentSessionService documentSessionService,
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
        if (!effectiveResourceId.HasValue && IsPerResourceAction(request.ActionKey))
        {
            effectiveResourceId = await ResolveDefaultResourceIdAsync(
                request.ActionKey, channelId, agentId, ct);
        }

        // Resolve default transcription model when not specified.
        var effectiveTranscriptionModelId = request.TranscriptionModelId;
        if (!effectiveTranscriptionModelId.HasValue && IsTranscriptionActionKey(request.ActionKey))
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
        AddLog(job, $"Job queued: {request.ActionKey ?? "unknown"}.");
        await db.SaveChangesAsync(ct);

        var caller = new ActionCaller(session.UserId, request.CallerAgentId);
        var result = await DispatchPermissionCheckAsync(
            agentId, job.ResourceId, caller, ct, job.ActionKey);

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
                        channelId,
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
            job.AgentId, job.ResourceId, approver, ct, job.ActionKey);

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

        if (IsTranscriptionActionKey(job.ActionKey)
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

        if (!IsTranscriptionActionKey(job.ActionKey))
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

        if (IsTranscriptionActionKey(job.ActionKey))
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

        if (IsTranscriptionActionKey(job.ActionKey))
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
                j.ActionKey, j.ResourceId, j.Status,
                j.CreatedAt, j.StartedAt, j.CompletedAt))
            .ToListAsync(ct);
    }

    /// <summary>List transcription jobs, optionally filtered by input audio.</summary>
    public async Task<IReadOnlyList<AgentJobResponse>> ListTranscriptionJobsAsync(
        Guid? inputAudioId = null, CancellationToken ct = default)
    {
        var query = db.AgentJobs
            .Include(j => j.LogEntries)
            .Include(j => j.TranscriptionSegments.OrderBy(s => s.StartTime))
            .Where(j => j.ActionKey != null && j.ActionKey.StartsWith("transcribe_from_audio"));

        if (inputAudioId is not null)
            query = query.Where(j => j.ResourceId == inputAudioId);

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
        Guid agentId, Guid? resourceId,
        ActionCaller caller, CancellationToken ct = default,
        string? actionKey = null)
        => DispatchPermissionCheckAsync(agentId, resourceId, caller, ct, actionKey);

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
            if (IsTranscriptionActionKey(job.ActionKey))
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

        var device = await db.InputAudios.FirstOrDefaultAsync(d => d.Id == job.ResourceId, ct)
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

        var device = await db.InputAudios.FirstOrDefaultAsync(d => d.Id == job.ResourceId, ct)
            ?? throw new InvalidOperationException("Audio device not found.");

        _channels.TryAdd(job.Id, Channel.CreateUnbounded<TranscriptionSegmentResponse>());

        orchestrator.Start(
            job.Id, modelId, device.DeviceIdentifier, job.Language,
            job.TranscriptionMode, job.WindowSeconds, job.StepSeconds);
    }

    private async Task<string?> DispatchExecutionAsync(AgentJobDB job, CancellationToken ct)
    {
        // Try ActionKey-based dispatch first (synthesizes envelope from raw params).
        // Falls back to full envelope deserialization.
        return await TryDispatchByActionKeyAsync(job, ct)
               ?? await DispatchModuleExecutionAsync(job, ct);
    }

    // ═══════════════════════════════════════════════════════════════
    // MODULE DISPATCH — executes module tool calls via ModuleRegistry
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Executes a module job by resolving the <c>ActionKey</c> through
    /// <see cref="ModuleRegistry"/>, deserializing the <see cref="ModuleEnvelope"/>
    /// from <c>ScriptJson</c>, and calling
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
            ActionKey: job.ActionKey,
            Language: job.Language);

        // External modules use their own per-module DI container;
        // bundled modules use the host's scope.
        var externalHost = moduleRegistry.GetExternalHost(envelope.Module);
        if (externalHost is not null && !externalHost.TryAcquireExecution())
            throw new InvalidOperationException(
                $"Module '{envelope.Module}' is unloading — cannot execute tools.");

        try
        {
            using var scope = externalHost is not null
                ? externalHost.CreateScope()
                : serviceScopeFactory.CreateScope();
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
        finally
        {
            externalHost?.ReleaseExecution();
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
    /// Attempts to dispatch a job to a module-provided tool using the
    /// explicit <see cref="AgentJobDB.ActionKey"/>.
    /// Returns <c>null</c> if no module owns the resolved tool name.
    /// </summary>
    private async Task<string?> TryDispatchByActionKeyAsync(
        AgentJobDB job, CancellationToken ct)
    {
        var actionKey = job.ActionKey;

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

    // ═══════════════════════════════════════════════════════════════
    // Permission dispatch
    // ═══════════════════════════════════════════════════════════════

    private Task<AgentActionResult> DispatchPermissionCheckAsync(
        Guid agentId, Guid? resourceId,
        ActionCaller caller, CancellationToken ct,
        string? actionKey = null)
    {
        return DispatchModulePermissionCheckAsync(agentId, resourceId, caller, actionKey, ct);
    }

    /// <summary>
    /// Determines whether the given action key requires a per-resource grant.
    /// Resolves via the module registry's permission descriptor.
    /// </summary>
    private bool IsPerResourceAction(string? actionKey)
    {
        if (string.IsNullOrWhiteSpace(actionKey)) return false;

        if (!moduleRegistry.TryResolve(actionKey, out var moduleId, out var toolName))
            return false;

        var descriptor = moduleRegistry.GetPermissionDescriptor(moduleId, toolName);
        return descriptor?.IsPerResource ?? false;
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
        string? actionKey, Guid channelId, Guid agentId,
        CancellationToken ct)
    {
        var delegateTo = ResolveDelegateTo(actionKey);

        var ch = await db.Channels
            .Include(c => c.DefaultResourceSet)
            .Include(c => c.AgentContext).ThenInclude(ctx => ctx!.DefaultResourceSet)
            .Include(c => c.AgentContext)
            .FirstOrDefaultAsync(c => c.Id == channelId, ct);

        // 1. Channel's DefaultResourceSet
        if (ch?.DefaultResourceSet is { } chDrs)
        {
            var id = ExtractFromDefaultResourceSet(chDrs, delegateTo);
            if (id.HasValue) return id;
        }

        // 2. Context's DefaultResourceSet
        if (ch?.AgentContext?.DefaultResourceSet is { } ctxDrs)
        {
            var id = ExtractFromDefaultResourceSet(ctxDrs, delegateTo);
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
            .Include(p => p.DefaultInputAudioAccess)
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

            var resourceId = ExtractDefaultResourceId(ps, delegateTo);
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
        DefaultResourceSetDB drs, string? delegateTo) => delegateTo switch
    {
        "UnsafeExecuteAsDangerousShellAsync" => drs.DangerousShellResourceId,
        "ExecuteAsSafeShellAsync" => drs.SafeShellResourceId,
        "AccessContainerAsync" => drs.ContainerResourceId,
        "AccessWebsiteAsync" => drs.WebsiteResourceId,
        "QuerySearchEngineAsync" => drs.SearchEngineResourceId,
        "AccessInternalDatabaseAsync" => drs.InternalDatabaseResourceId,
        "AccessExternalDatabaseAsync" => drs.ExternalDatabaseResourceId,
        "AccessInputAudioAsync" => drs.InputAudioResourceId,
        "AccessDisplayDeviceAsync" => drs.DisplayDeviceResourceId,
        "AccessEditorSessionAsync" => drs.EditorSessionResourceId,
        "ManageAgentAsync" => drs.AgentResourceId,
        "EditTaskAsync" => drs.TaskResourceId,
        "AccessSkillAsync" => drs.SkillResourceId,
        "AccessBotIntegrationAsync" => drs.BotIntegrationResourceId,
        "AccessDocumentSessionAsync" => drs.DocumentSessionResourceId,
        "LaunchNativeApplicationAsync" => drs.NativeApplicationResourceId,
        _ => null,
    };

    /// <summary>
    /// Returns the resource ID from the matching default access entry on
    /// a permission set, or <c>null</c> if no default is configured.
    /// </summary>
    private static Guid? ExtractDefaultResourceId(
        PermissionSetDB permissionSet, string? delegateTo) => delegateTo switch
    {
        "AccessInternalDatabaseAsync" => permissionSet.DefaultInternalDatabaseAccess?.InternalDatabaseId,
        "AccessExternalDatabaseAsync" => permissionSet.DefaultExternalDatabaseAccess?.ExternalDatabaseId,
        "AccessWebsiteAsync" => permissionSet.DefaultWebsiteAccess?.WebsiteId,
        "QuerySearchEngineAsync" => permissionSet.DefaultSearchEngineAccess?.SearchEngineId,
        "AccessContainerAsync" => permissionSet.DefaultContainerAccess?.ContainerId,
        "AccessInputAudioAsync" => permissionSet.DefaultInputAudioAccess?.InputAudioId,
        "AccessDisplayDeviceAsync" => permissionSet.DefaultDisplayDeviceAccess?.DisplayDeviceId,
        "AccessEditorSessionAsync" => permissionSet.DefaultEditorSessionAccess?.EditorSessionId,
        "ManageAgentAsync" => permissionSet.DefaultAgentPermission?.AgentId,
        "EditTaskAsync" => permissionSet.DefaultTaskPermission?.ScheduledTaskId,
        "AccessSkillAsync" => permissionSet.DefaultSkillPermission?.SkillId,
        "AccessBotIntegrationAsync" => permissionSet.DefaultBotIntegrationAccess?.BotIntegrationId,
        "AccessDocumentSessionAsync" => permissionSet.DefaultDocumentSessionAccess?.DocumentSessionId,
        "LaunchNativeApplicationAsync" => permissionSet.DefaultNativeApplicationAccess?.NativeApplicationId,
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
            if (userPs is null || !HasMatchingGrant(userPs, resourceId, actionKey))
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
            if (chPs is not null && HasMatchingGrant(chPs, resourceId, actionKey))
                return true;
        }

        // Channel didn't have it — fall through to context.
        if (ch.AgentContext?.PermissionSetId is { } ctxPsId)
        {
            var ctxPs = await actions.LoadPermissionSetAsync(ctxPsId, ct);
            if (ctxPs is not null && HasMatchingGrant(ctxPs, resourceId, actionKey))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Returns <c>true</c> when the permission set contains a grant
    /// that covers the given action key (and resource, for per-resource
    /// actions).  Resolves the tool's <see cref="ModuleToolPermission.DelegateTo"/>
    /// and checks the corresponding grant collection via
    /// <see cref="AgentActionService.HasGrantByDelegateName"/>.
    /// </summary>
    private bool HasMatchingGrant(
        PermissionSetDB ps, Guid? resourceId,
        string? actionKey = null)
    {
        if (string.IsNullOrWhiteSpace(actionKey))
            return false;
        return ResolveModuleGrantCheck(ps, actionKey, resourceId);
    }

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

    /// <summary>
    /// Resolves the <see cref="ModuleToolPermission.DelegateTo"/> string
    /// for the given action key via the module registry.
    /// </summary>
    private string? ResolveDelegateTo(string? actionKey)
    {
        if (string.IsNullOrWhiteSpace(actionKey)) return null;
        if (!moduleRegistry.TryResolve(actionKey, out var moduleId, out var toolName)) return null;
        var descriptor = moduleRegistry.GetPermissionDescriptor(moduleId, toolName);
        return descriptor?.DelegateTo;
    }

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

    /// <summary>
    /// Returns <c>true</c> when the <paramref name="actionKey"/> corresponds
    /// to a transcription tool (e.g. "transcribe_from_audio_device").
    /// </summary>
    private static bool IsTranscriptionActionKey(string? actionKey) =>
        actionKey is not null && actionKey.StartsWith("transcribe_from_audio", StringComparison.OrdinalIgnoreCase);

    private static AgentJobResponse ToResponse(AgentJobDB job) =>
        new(
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
            DangerousShellType: job.DangerousShellType,
            SafeShellType: job.SafeShellType,
            ScriptJson: job.ScriptJson,
            WorkingDirectory: job.WorkingDirectory,
            TranscriptionModelId: job.TranscriptionModelId,
            Language: job.Language,
            TranscriptionMode: job.TranscriptionMode,
            WindowSeconds: job.WindowSeconds,
            StepSeconds: job.StepSeconds,
            Segments: IsTranscriptionActionKey(job.ActionKey)
                ? job.TranscriptionSegments
                    .OrderBy(s => s.StartTime)
                    .Select(s => new TranscriptionSegmentResponse(
                        s.Id, s.Text, s.StartTime, s.EndTime, s.Confidence, s.Timestamp,
                        s.IsProvisional))
                    .ToList()
                : null);
}
