using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Application.Core.Modules;
using SharpClaw.Contracts.Entities.Core.Clearance;
using SharpClaw.Contracts.Entities.Core.Context;
using SharpClaw.Contracts.Entities.Core.Jobs;
using SharpClaw.Contracts.Entities.Core.Messages;
using SharpClaw.Contracts;
using SharpClaw.Contracts.DTOs.AgentActions;
using SharpClaw.Contracts.DTOs.Chat;
using SharpClaw.Contracts.Enums;
using SharpClaw.Contracts.Modules;
using SharpClaw.Contracts.Entities.Core;
using SharpClaw.Infrastructure.Persistence;

namespace SharpClaw.Application.Services;

/// <summary>
/// Manages the lifecycle of agent action jobs: submission, permission
/// evaluation, optional approval, execution, and outcome tracking.
/// </summary>
public sealed class AgentJobService(
    SharpClawDbContext db,
    IPersistenceEntityResolver entities,
    AgentActionService actions,
    SessionService session,
    ModuleRegistry moduleRegistry,
    ModuleMetricsCollector metricsCollector,
    ModuleEventDispatcher eventDispatcher,
    IServiceScopeFactory serviceScopeFactory,
    IConfiguration configuration)
{
    private readonly ModuleEventDispatcher _eventDispatcher = eventDispatcher;
    private readonly IConfiguration _configuration = configuration;

    // ═══════════════════════════════════════════════════════════════
    // Public API
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Submit a new job.  The channel is the primary anchor — the agent
    /// is inferred from the channel unless explicitly overridden in the
    /// request.  Permission is evaluated immediately:
    /// <list type="bullet">
    ///   <item>Approved → executes inline, returns <see cref="AgentJobStatus.Completed/>
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

        var job = new AgentJobDB
        {
            AgentId = agentId,
            ChannelId = channelId,
            CallerUserId = session.UserId,
            CallerAgentId = request.CallerAgentId,
            ActionKey = request.ActionKey,
            ResourceId = effectiveResourceId,
            Status = AgentJobStatus.Queued,
            ScriptJson = request.ScriptJson,
            WorkingDirectory = request.WorkingDirectory,
        };

        db.AgentJobs.Add(job);
        AddLog(job, $"Job queued: {request.ActionKey ?? "unknown"}.");
        await db.SaveChangesAsync(ct);

        var caller = new ActionCaller(session.UserId, request.CallerAgentId);
        var result = await DispatchPermissionCheckAsync(
            agentId, job.ResourceId, caller, ct, job.ActionKey,
            channelPsId: ch.PermissionSetId, contextPsId: ch.AgentContext?.PermissionSetId);

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

        var approvalCh = await db.Channels
            .Include(c => c.AgentContext)
            .FirstOrDefaultAsync(c => c.Id == job.ChannelId, ct);

        var result = await DispatchPermissionCheckAsync(
            job.AgentId, job.ResourceId, approver, ct, job.ActionKey,
            channelPsId: approvalCh?.PermissionSetId,
            contextPsId: approvalCh?.AgentContext?.PermissionSetId);

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

        job.Status = AgentJobStatus.Cancelled;
        job.CompletedAt = DateTimeOffset.UtcNow;
        AddLog(job, "Job cancelled.");
        await db.SaveChangesAsync(ct);

        return ToResponse(job);
    }

    /// <summary>Stop a long-running job and complete it normally.</summary>
    public async Task<AgentJobResponse?> StopAsync(
        Guid jobId, string? requiredActionPrefix = null, CancellationToken ct = default)
    {
        var job = await LoadJobAsync(jobId, ct);
        if (job is null) return null;

        if (!string.IsNullOrWhiteSpace(requiredActionPrefix)
            && job.ActionKey?.StartsWith(requiredActionPrefix, StringComparison.OrdinalIgnoreCase) != true)
        {
            AddLog(job, "Stop rejected: job action does not match the requested action prefix.", "Warning");
            await db.SaveChangesAsync(ct);
            return ToResponse(job);
        }

        if (job.Status is not AgentJobStatus.Executing and not AgentJobStatus.Paused)
        {
            AddLog(job, $"Stop rejected: job is {job.Status}, not Executing or Paused.", "Warning");
            await db.SaveChangesAsync(ct);
            return ToResponse(job);
        }

        job.Status = AgentJobStatus.Completed;
        job.CompletedAt = DateTimeOffset.UtcNow;
        AddLog(job, "Job stopped.");
        await db.SaveChangesAsync(ct);

        return ToResponse(job);
    }

    /// <summary>
    /// Pause a long-running job. The job can be resumed later with
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

        job.Status = AgentJobStatus.Paused;
        AddLog(job, "Job paused.");
        await db.SaveChangesAsync(ct);

        return ToResponse(job);
    }

    /// <summary>
    /// Resume a previously paused job. Module executors own their resume
    /// semantics and restore any module-specific state using the original
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
        var jobs = await entities.QueryAsync<AgentJobDB>(
            db,
            j => j.ChannelId == channelId,
            hint: new PersistenceQueryHint("ChannelId", channelId),
            ct: ct);

        foreach (var job in jobs)
        {
            if (job.LogEntries.Count == 0)
            {
                job.LogEntries = (await entities.QueryAsync<AgentJobLogEntryDB>(
                    db,
                    l => l.AgentJobId == job.Id,
                    hint: new PersistenceQueryHint("AgentJobId", job.Id),
                    ct: ct)).ToList();
            }
        }

        return jobs.OrderByDescending(j => j.CreatedAt).Select(ToResponse).ToList();
    }

    /// <summary>
    /// List lightweight summaries for all jobs in a channel, most recent first.
    /// Does not load <c>ResultData</c>, <c>ErrorLog</c>, logs, or segments —
    /// suitable for populating dropdowns or list views without memory pressure.
    /// </summary>
    public async Task<IReadOnlyList<AgentJobSummaryResponse>> ListSummariesAsync(
        Guid channelId, CancellationToken ct = default)
    {
        var jobs = await entities.QueryAsync<AgentJobDB>(
            db,
            j => j.ChannelId == channelId,
            hint: new PersistenceQueryHint("ChannelId", channelId),
            ct: ct);

        return jobs
            .OrderByDescending(j => j.CreatedAt)
            .Select(j => new AgentJobSummaryResponse(
                j.Id, j.ChannelId, j.AgentId,
                j.ActionKey, j.ResourceId, j.Status,
                j.CreatedAt, j.StartedAt, j.CompletedAt))
            .ToList();
    }

    public async Task<IReadOnlyList<AgentJobResponse>> ListJobsByActionPrefixAsync(
        string actionKeyPrefix,
        Guid? resourceId = null,
        CancellationToken ct = default)
    {
        var jobs = await entities.QueryAsync<AgentJobDB>(
            db,
            j => j.ActionKey != null
                 && j.ActionKey.StartsWith(actionKeyPrefix, StringComparison.OrdinalIgnoreCase)
                 && (resourceId == null || j.ResourceId == resourceId),
            ct: ct);

        foreach (var job in jobs)
        {
            if (job.LogEntries.Count == 0)
            {
                job.LogEntries = (await entities.QueryAsync<AgentJobLogEntryDB>(
                    db,
                    l => l.AgentJobId == job.Id,
                    hint: new PersistenceQueryHint("AgentJobId", job.Id),
                    ct: ct)).ToList();
            }
        }

        return jobs.OrderByDescending(j => j.CreatedAt).Select(ToResponse).ToList();
    }

    public async Task<IReadOnlyList<AgentJobSummaryResponse>> ListJobSummariesByActionPrefixAsync(
        string actionKeyPrefix,
        Guid? resourceId = null,
        CancellationToken ct = default)
    {
        var jobs = await entities.QueryAsync<AgentJobDB>(
            db,
            j => j.ActionKey != null
                 && j.ActionKey.StartsWith(actionKeyPrefix, StringComparison.OrdinalIgnoreCase)
                 && (resourceId == null || j.ResourceId == resourceId),
            ct: ct);

        return jobs
            .OrderByDescending(j => j.CreatedAt)
            .Select(j => new AgentJobSummaryResponse(
                j.Id, j.ChannelId, j.AgentId,
                j.ActionKey, j.ResourceId, j.Status,
                j.CreatedAt, j.StartedAt, j.CompletedAt))
            .ToList();
    }

    public async Task<bool> JobExistsWithActionPrefixAsync(
        Guid jobId, string actionKeyPrefix, CancellationToken ct = default)
    {
        var job = await LoadJobAsync(jobId, ct);
        return job?.ActionKey?.StartsWith(actionKeyPrefix, StringComparison.OrdinalIgnoreCase) == true;
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

        var prefixedToolName = $"{module.ToolPrefix}_{envelope.Tool}";

        var jobContext = new AgentJobContext(
            JobId: job.Id,
            AgentId: job.AgentId,
            ChannelId: job.ChannelId,
            ResourceId: job.ResourceId,
            ActionKey: job.ActionKey);

        // External modules use their own per-module DI container;
        // bundled modules use the host's scope.
        var externalHost = moduleRegistry.GetExternalHost(envelope.Module);
        if (externalHost is not null && !externalHost.TryAcquireExecution())
            throw new InvalidOperationException(
                $"Module '{envelope.Module}' is unloading — cannot execute tools.");

        var sw = Stopwatch.StartNew();
        try
        {
            using var scope = externalHost is not null
                ? externalHost.CreateScope()
                : serviceScopeFactory.CreateScope();

            // Set ModuleExecutionContext so IModuleConfigStore resolves correctly.
            var execCtx = scope.ServiceProvider.GetService<ModuleExecutionContext>();
            if (execCtx is not null) execCtx.ModuleId = module.Id;

            var restrictedScope = new ModuleServiceScope(scope.ServiceProvider, module.Id);

            // Timeout: per-tool override → manifest default → 30s.
            var manifest = moduleRegistry.GetManifest(envelope.Module);
            var toolTimeout = moduleRegistry.GetToolTimeout(envelope.Module, envelope.Tool);
            var timeoutSeconds = toolTimeout ?? manifest?.ExecutionTimeoutSeconds ?? 30;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

            try
            {
                // Try streaming variant first; fall back to non-streaming.
                var stream = module.ExecuteToolStreamingAsync(
                    envelope.Tool, envelope.Params, jobContext, restrictedScope, cts.Token);

                string? result;
                if (stream is not null)
                {
                    var sb = new StringBuilder();
                    await foreach (var chunk in stream.WithCancellation(cts.Token))
                        sb.Append(chunk);
                    result = sb.ToString();
                }
                else
                {
                    result = await module.ExecuteToolAsync(
                        envelope.Tool, envelope.Params, jobContext, restrictedScope, cts.Token);
                }

                sw.Stop();
                metricsCollector.RecordSuccess(prefixedToolName, sw.Elapsed);
                return result;
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                sw.Stop();
                metricsCollector.RecordTimeout(prefixedToolName);
                throw new InvalidOperationException(
                    $"Module tool '{envelope.Module}.{envelope.Tool}' " +
                    $"exceeded timeout ({timeoutSeconds}s).");
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not InvalidOperationException)
            {
                sw.Stop();
                metricsCollector.RecordFailure(prefixedToolName);
                throw new InvalidOperationException(
                    $"[{ex.GetType().Name}] " +
                    ExceptionSanitizer.Sanitize(envelope.Module, envelope.Tool, ex.Message),
                    ex);
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
        string? actionKey, CancellationToken ct,
        Guid? channelPsId = null, Guid? contextPsId = null)
    {
        if (string.IsNullOrWhiteSpace(actionKey))
            return AgentActionResult.Denied("Module action requires an ActionKey to resolve permissions.");

        if (!moduleRegistry.TryResolve(actionKey, out var moduleId, out var toolName))
            return AgentActionResult.Denied($"No module registered for tool '{actionKey}'.");

        var descriptor = moduleRegistry.GetPermissionDescriptor(moduleId, toolName);
        if (descriptor is null)
            return AgentActionResult.Denied($"Module tool '{actionKey}' has no permission descriptor.");

        if (descriptor.IsPerResource && !resourceId.HasValue)
        {
            Debug.WriteLine(
                $"[PermissionCheck] DENIED: ResourceId is null for per-resource tool '{actionKey}'",
                "SharpClaw.CLI");
            return AgentActionResult.Denied($"ResourceId is required for module tool '{actionKey}'.");
        }

        Debug.WriteLine(
            $"[PermissionCheck] Tool='{actionKey}' AgentId={agentId} ResourceId={resourceId} DelegateTo='{descriptor.DelegateTo}'",
            "SharpClaw.CLI");

        // Direct callback takes priority.
        if (descriptor.Check is not null)
            return await descriptor.Check(agentId, resourceId, caller, ct);

        // Delegate to a named AgentActionService method.
        if (!string.IsNullOrWhiteSpace(descriptor.DelegateTo))
        {
            var result = actions.TryEvaluateByDelegateNameAsync(
                descriptor.DelegateTo, agentId, resourceId, caller, ct,
                channelPsId: channelPsId, contextPsId: contextPsId);
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

        // Parse the raw ScriptJson into tool parameters.  When ScriptJson
        // is already a full ModuleEnvelope (created by ParseNativeToolCall),
        // extract only the nested "params" element to avoid double-wrapping.
        JsonElement paramsElement;
        if (!string.IsNullOrWhiteSpace(job.ScriptJson))
        {
            using var doc = JsonDocument.Parse(job.ScriptJson);
            var root = doc.RootElement;
            if ((root.TryGetProperty("module", out _) || root.TryGetProperty("Module", out _)) &&
                (root.TryGetProperty("tool", out _) || root.TryGetProperty("Tool", out _)) &&
                (root.TryGetProperty("params", out var nested) || root.TryGetProperty("Params", out nested)))
                paramsElement = nested.Clone();
            else
                paramsElement = root.Clone();
        }
        else
        {
            paramsElement = JsonDocument.Parse("{}").RootElement.Clone();
        }

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
        string? actionKey = null,
        Guid? channelPsId = null, Guid? contextPsId = null)
    {
        return DispatchModulePermissionCheckAsync(agentId, resourceId, caller, actionKey, ct,
            channelPsId, contextPsId);
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
            .Include(c => c.DefaultResourceSet!).ThenInclude(drs => drs.Entries)
            .Include(c => c.AgentContext!).ThenInclude(ctx => ctx.DefaultResourceSet!).ThenInclude(drs => drs.Entries)
            .FirstOrDefaultAsync(c => c.Id == channelId, ct);

        // 1. Channel's DefaultResourceSet
        if (ch?.DefaultResourceSet is { } chDrs)
        {
            var id = ExtractFromDefaultResourceSet(chDrs, delegateTo, moduleRegistry);
            if (id.HasValue) return id;
        }

        // 2. Context's DefaultResourceSet
        if (ch?.AgentContext?.DefaultResourceSet is { } ctxDrs)
        {
            var id = ExtractFromDefaultResourceSet(ctxDrs, delegateTo, moduleRegistry);
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
            .Include(p => p.ResourceAccesses)
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

    private static Guid? ExtractFromDefaultResourceSet(
        DefaultResourceSetDB drs, string? delegateTo,
        ModuleRegistry registry)
    {
        if (delegateTo is null) return null;
        var key = registry.GetDefaultResourceKeyForDelegate(delegateTo);
        if (key is null) return null;
        var entry = drs.Entries.FirstOrDefault(
            e => string.Equals(e.ResourceKey, key, StringComparison.OrdinalIgnoreCase));
        return entry?.ResourceId;
    }

    /// <summary>
    /// Returns the resource ID from the matching default access entry on
    /// a permission set, or <c>null</c> if no default is configured.
    /// </summary>
    private Guid? ExtractDefaultResourceId(
        PermissionSetDB permissionSet, string? delegateTo)
    {
        if (delegateTo is null)
            return null;

        var resourceType = moduleRegistry.ResolveResourceType(delegateTo);
        if (resourceType is null)
            return null;

        return permissionSet.ResourceAccesses
            .FirstOrDefault(a => a.ResourceType == resourceType && a.IsDefault)
            ?.ResourceId;
    }

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

        return actions.HasGrantByDelegateName(ps, descriptor.DelegateTo, resourceId);
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

    private async Task<AgentJobDB?> LoadJobAsync(Guid jobId, CancellationToken ct)
    {
        var job = await entities.FindAsync<AgentJobDB>(db, jobId, ct);
        if (job is not null)
        {
            if (job.LogEntries.Count == 0)
            {
                job.LogEntries = (await entities.QueryAsync<AgentJobLogEntryDB>(
                    db,
                    l => l.AgentJobId == jobId,
                    hint: new PersistenceQueryHint("AgentJobId", jobId),
                    ct: ct)).ToList();
            }
        }

        return job;
    }

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

    private static AgentJobResponse ToResponse(AgentJobDB job)
    {
        var jobCost = job.PromptTokens is not null || job.CompletionTokens is not null
            ? new TokenUsageResponse(
                job.PromptTokens ?? 0,
                job.CompletionTokens ?? 0,
                (job.PromptTokens ?? 0) + (job.CompletionTokens ?? 0))
            : null;

        return new(
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
            ScriptJson: job.ScriptJson,
            WorkingDirectory: job.WorkingDirectory,
            JobCost: jobCost);
    }

    /// <summary>
    /// Records prompt/completion tokens on a set of jobs that were
    /// submitted during a single LLM round.  Tokens are split evenly
    /// across the jobs; any remainder is assigned to the first job.
    /// </summary>
    public async Task RecordTokensAsync(
        IReadOnlyList<Guid> jobIds, int promptTokens, int completionTokens,
        CancellationToken ct = default)
    {
        if (jobIds.Count == 0) return;

        // Jobs being recorded are from the current session (just executed),
        // so they should be in EF. Fall back to cold store + re-attach if
        // a restart happened mid-flight.
        var jobs = await db.AgentJobs
            .Where(j => jobIds.Contains(j.Id))
            .ToListAsync(ct);

        if (jobs.Count == 0)
        {
            foreach (var id in jobIds)
            {
                var job = await entities.FindAsync<AgentJobDB>(db, id, ct);
                if (job is not null)
                    jobs.Add(job);
            }
        }

        if (jobs.Count == 0) return;

        var promptPer = promptTokens / jobs.Count;
        var completionPer = completionTokens / jobs.Count;
        var promptRemainder = promptTokens % jobs.Count;
        var completionRemainder = completionTokens % jobs.Count;

        for (var i = 0; i < jobs.Count; i++)
        {
            jobs[i].PromptTokens = (jobs[i].PromptTokens ?? 0) + promptPer + (i == 0 ? promptRemainder : 0);
            jobs[i].CompletionTokens = (jobs[i].CompletionTokens ?? 0) + completionPer + (i == 0 ? completionRemainder : 0);
        }

        await db.SaveChangesAsync(ct);
    }
}
