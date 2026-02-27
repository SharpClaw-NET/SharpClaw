using System.Collections.Concurrent;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Mk8.Shell;
using Mk8.Shell.Engine;
using Mk8.Shell.Models;
using Mk8.Shell.Safety;
using Mk8.Shell.Startup;
using SharpClaw.Application.Infrastructure.Models.Clearance;
using SharpClaw.Application.Infrastructure.Models.Context;
using SharpClaw.Application.Infrastructure.Models.Jobs;
using SharpClaw.Application.Infrastructure.Models.Messages;
using SharpClaw.Application.Infrastructure.Models.Resources;
using SharpClaw.Contracts;
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
LiveTranscriptionOrchestrator orchestrator,
SessionService session,
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
        if (!effectiveResourceId.HasValue && IsPerResourceAction(request.ActionType))
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
            ResourceId = effectiveResourceId,
            Status = AgentJobStatus.Queued,
            DangerousShellType = request.DangerousShellType,
            SafeShellType = request.SafeShellType,
            ScriptJson = request.ScriptJson,
            WorkingDirectory = request.WorkingDirectory,
            TranscriptionModelId = effectiveTranscriptionModelId,
            Language = request.Language,
        };

        db.AgentJobs.Add(job);
        AddLog(job, $"Job queued: {request.ActionType}.");
        await db.SaveChangesAsync(ct);

        var caller = new ActionCaller(session.UserId, request.CallerAgentId);
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
                // Channel pre-auth counts as ApprovedByWhitelistedUser-
                // level authority for levels 2 and 4.  For level 1
                // (ApprovedBySameLevelUser), the session user must also
                // personally hold the same permission via their own role.
                // Level 3 (agent-only) is never pre-authorised.
                if (await HasChannelAuthorizationAsync(
                        channelId, job.ActionType,
                        job.ResourceId, result.EffectiveClearance,
                        session.UserId, ct))
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
        ActionCaller caller, CancellationToken ct = default)
        => DispatchPermissionCheckAsync(agentId, actionType, resourceId, caller, ct);

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

    // ── Shell execution routing ─────────────────────────────────
    //
    //  SAFE  (ExecuteAsSafeShell)             → mk8.shell pipeline
    //        mk8.shell is sandboxed HARD: closed verb set,
    //        binary allowlist, path jailing, no real shell
    //        interpreter is ever invoked.  ALL mk8.shell
    //        execution is safe by definition.
    //
    //  DANGEROUS  (UnsafeExecuteAsDangerousShell) → real shell process
    //        Bash, PowerShell, CommandPrompt, Git are ALWAYS
    //        dangerous.  The raw command text is handed to the
    //        interpreter with no sandboxing.  There is no such
    //        thing as a "safe" Bash/PowerShell/Cmd/Git execution.
    //
    // ──────────────────────────────────────────────────────────────

    private async Task<string?> DispatchExecutionAsync(AgentJobDB job, CancellationToken ct)
    {
        return job.ActionType switch
        {
            // Safe shell — always mk8.shell, always sandboxed.
            AgentActionType.ExecuteAsSafeShell
                => await ExecuteSafeShellAsync(job, ct),

            // Dangerous shell — real interpreter, unrestricted.
            AgentActionType.UnsafeExecuteAsDangerousShell
                => await ExecuteDangerousShellAsync(job, ct),

            // Agent lifecycle
            AgentActionType.CreateSubAgent
                => await ExecuteCreateSubAgentAsync(job, ct),
            AgentActionType.ManageAgent
                => await ExecuteManageAgentAsync(job, ct),

            // Container lifecycle
            AgentActionType.CreateContainer
                => await ExecuteCreateContainerAsync(job, ct),

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

            _ => $"Action '{job.ActionType}' executed successfully " +
                 $"(resource: {job.ResourceId?.ToString() ?? "n/a"})."
        };
    }

    // ═══════════════════════════════════════════════════════════════
    // SAFE SHELL — mk8.shell only (sandboxed, verb-restricted DSL)
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Executes an <see cref="AgentActionType.ExecuteAsSafeShell"/> job
    /// through the mk8.shell pipeline.
    /// <para>
    /// The job's <see cref="AgentJobDB.ResourceId"/> identifies the
    /// <see cref="ContainerDB"/> (which must be an mk8shell container).
    /// mk8.shell is self-contained: the sandbox name is passed to
    /// <see cref="Mk8TaskContainer.Create"/> which resolves the sandbox
    /// from its own <c>%APPDATA%/mk8.shell</c> registry, verifies the
    /// signed environment, and builds the workspace context.
    /// </para>
    /// <para>
    /// Permission check (<see cref="AgentActionService.AccessContainerAsync"/>)
    /// must have already been evaluated before reaching this method.
    /// </para>
    /// </summary>
    private async Task<string?> ExecuteSafeShellAsync(
        AgentJobDB job, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(job.ScriptJson))
            throw new InvalidOperationException(
                "Safe shell job requires a script payload (ScriptJson) " +
                "containing an mk8.shell script.");

        if (!job.ResourceId.HasValue)
            throw new InvalidOperationException(
                "Safe shell job requires a ResourceId (Container).");

        var container = await db.Containers
            .FirstOrDefaultAsync(c => c.Id == job.ResourceId.Value, ct)
            ?? throw new InvalidOperationException(
                $"Container {job.ResourceId} not found.");

        if (container.Type != ContainerType.Mk8Shell)
            throw new InvalidOperationException(
                $"Container '{container.Name}' is not an mk8shell container.");

        if (string.IsNullOrWhiteSpace(container.SandboxName))
            throw new InvalidOperationException(
                $"Container '{container.Name}' has no sandbox name configured.");

        var script = JsonSerializer.Deserialize<Mk8ShellScript>(
            job.ScriptJson,
            new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                Converters = { new JsonStringEnumConverter() }
            })
            ?? throw new InvalidOperationException(
                "Failed to deserialise mk8.shell script from ScriptJson.");

        // mk8.shell is self-contained: pass the sandbox name and it
        // resolves everything from its own local registry.
        using var taskContainer = Mk8TaskContainer.Create(container.SandboxName);

        var effectiveOptions = script.Options ?? Mk8ExecutionOptions.Default;

        // Compile through the full mk8.shell pipeline
        // (expand → resolve → sanitize → compile).
        var compiler = new Mk8ShellCompiler(
            Mk8CommandWhitelist.CreateDefault(
                taskContainer.RuntimeConfig,
                taskContainer.FreeTextConfig,
                taskContainer.EnvVocabularies,
                taskContainer.GigaBlacklist));
        var compiled = compiler.Compile(
            script, taskContainer.Workspace, effectiveOptions);

        AddLog(job,
            $"mk8.shell script compiled: {compiled.Commands.Count} command(s), " +
            $"sandbox '{container.SandboxName}'.");
        await db.SaveChangesAsync(ct);

        // Execute all compiled commands (safe — never spawns a real shell).
        var executor = new Mk8ShellExecutor();
        var result = await executor.ExecuteAsync(compiled, ct);

        // Build a human-readable summary for ResultData.
        var summary = new System.Text.StringBuilder();
        summary.AppendLine($"AllSucceeded: {result.AllSucceeded}");
        summary.AppendLine($"Duration: {result.TotalDuration}");
        summary.AppendLine($"Steps: {result.Steps.Count}");

        foreach (var step in result.Steps)
        {
            var status = step.Success ? "OK" : "FAIL";
            summary.AppendLine(
                $"  [{step.StepIndex}] {step.Verb} {status} " +
                $"({step.Attempts} attempt(s), {step.Duration.TotalMilliseconds:F0}ms)");

            if (!string.IsNullOrWhiteSpace(step.Error))
                summary.AppendLine($"    Error: {step.Error}");
        }

        if (!result.AllSucceeded)
            throw new InvalidOperationException(
                $"mk8.shell script execution failed.{Environment.NewLine}{summary}");

        return summary.ToString();
    }

    // ═══════════════════════════════════════════════════════════════
    // DANGEROUS SHELL — real interpreter (Bash/PowerShell/Cmd/Git)
    // ═══════════════════════════════════════════════════════════════
    //
    // Bash, PowerShell, CommandPrompt, and Git are ALWAYS dangerous.
    // They are never sandboxed through mk8.shell.  The raw command
    // text is handed directly to the interpreter.  The only protection
    // is the permission system's clearance requirement.
    //

    /// <summary>
    /// Executes an <see cref="AgentActionType.UnsafeExecuteAsDangerousShell"/>
    /// job by spawning a real shell interpreter process.
    /// <para>
    /// <b>This is inherently dangerous.</b>  The raw command from
    /// <see cref="AgentJobDB.ScriptJson"/> is passed to the shell with
    /// no sandboxing, no allowlist, and no path validation.  Safety
    /// relies entirely on the two-level permission clearance check that
    /// was already evaluated before reaching this point.
    /// </para>
    /// <para>
    /// Cross-platform: Bash on Linux/macOS, PowerShell (pwsh) everywhere,
    /// CommandPrompt on Windows only, Git everywhere.
    /// </para>
    /// </summary>
    private async Task<string?> ExecuteDangerousShellAsync(
        AgentJobDB job, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(job.ScriptJson))
            throw new InvalidOperationException(
                "Dangerous shell job requires a command payload (ScriptJson).");

        if (!job.ResourceId.HasValue)
            throw new InvalidOperationException(
                "Dangerous shell job requires a ResourceId (SystemUser).");

        if (!job.DangerousShellType.HasValue)
            throw new InvalidOperationException(
                "Dangerous shell job requires a DangerousShellType.");

        var systemUser = await db.SystemUsers
            .FirstOrDefaultAsync(u => u.Id == job.ResourceId.Value, ct)
            ?? throw new InvalidOperationException(
                $"SystemUser {job.ResourceId} not found.");

        var workingDir = job.WorkingDirectory
            ?? systemUser.WorkingDirectory
            ?? systemUser.SandboxRoot
            ?? Directory.GetCurrentDirectory();

        // Resolve the shell executable and argument list.
        var (executable, arguments) = ResolveDangerousShell(
            job.DangerousShellType.Value, job.ScriptJson);

        AddLog(job,
            $"Dangerous shell ({job.DangerousShellType}) executing as " +
            $"'{systemUser.Username}' in '{workingDir}'.");
        await db.SaveChangesAsync(ct);

        // Spawn the real shell — NO sandboxing, NO allowlist.
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = workingDir,
        };

        foreach (var arg in arguments)
            psi.ArgumentList.Add(arg);

        using var process = new System.Diagnostics.Process { StartInfo = psi };
        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);

        await process.WaitForExitAsync(ct);

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"Dangerous shell '{executable}' exited with code {process.ExitCode}.\n" +
                $"stderr: {stderr}");

        return string.IsNullOrWhiteSpace(stdout) ? "(no output)" : stdout;
    }

    /// <summary>
    /// Maps a <see cref="DangerousShellType"/> to the OS executable and
    /// the argument list that passes the raw command to the interpreter.
    /// <para>
    /// Every value in this enum spawns a real, unrestricted process.
    /// None of them ever go through mk8.shell.
    /// </para>
    /// </summary>
    private static (string Executable, string[] Arguments) ResolveDangerousShell(
        DangerousShellType shellType, string command) => shellType switch
    {
        // Bash — Linux/macOS (or WSL on Windows).
        DangerousShellType.Bash => ("bash", ["-c", command]),

        // PowerShell — cross-platform via pwsh.
        DangerousShellType.PowerShell => (
            OperatingSystem.IsWindows() ? "powershell" : "pwsh",
            ["-NoProfile", "-NonInteractive", "-Command", command]),

        // Command Prompt — Windows only.
        DangerousShellType.CommandPrompt => ("cmd", ["/C", command]),

        // Git — cross-platform, command is the git sub-command + args.
        DangerousShellType.Git => ("git", command.Split(' ', StringSplitOptions.RemoveEmptyEntries)),

        _ => throw new InvalidOperationException(
            $"Unsupported dangerous shell type: {shellType}.")
    };

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
    // CREATE CONTAINER
    // ═══════════════════════════════════════════════════════════════

    private sealed class CreateContainerPayload
    {
        public string? Name { get; set; }
        public string? Path { get; set; }
        public string? Description { get; set; }
    }

    private async Task<string?> ExecuteCreateContainerAsync(
        AgentJobDB job, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(job.ScriptJson))
            throw new InvalidOperationException(
                "CreateContainer requires a JSON payload in ScriptJson.");

        var payload = JsonSerializer.Deserialize<CreateContainerPayload>(
            job.ScriptJson, _payloadJsonOptions)
            ?? throw new InvalidOperationException(
                "Failed to deserialise CreateContainer payload.");

        if (string.IsNullOrWhiteSpace(payload.Name))
            throw new InvalidOperationException(
                "CreateContainer payload requires a 'name' field.");

        if (string.IsNullOrWhiteSpace(payload.Path))
            throw new InvalidOperationException(
                "CreateContainer payload requires a 'path' field.");

        var sandboxName = payload.Name;
        var sandboxDir = Path.Combine(
            Path.GetFullPath(payload.Path), sandboxName);

        var exists = await db.Containers.AnyAsync(
            c => c.Type == ContainerType.Mk8Shell
                 && c.SandboxName == sandboxName, ct);

        if (exists)
            throw new InvalidOperationException(
                $"An mk8shell container with sandbox name '{sandboxName}' already exists.");

        Mk8SandboxRegistrar.Register(sandboxName, sandboxDir);

        var container = new ContainerDB
        {
            Name = $"mk8shell:{sandboxName}",
            Type = ContainerType.Mk8Shell,
            SandboxName = sandboxName,
            Description = payload.Description,
        };

        db.Containers.Add(container);
        await db.SaveChangesAsync(ct);

        AddLog(job, $"Container '{container.Name}' created (id={container.Id}).");
        return $"Created mk8shell container '{sandboxName}' at '{sandboxDir}' (id={container.Id}).";
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
    /// Google Chrome <c>--incognito</c>) against a <b>localhost</b> URL
    /// and returns either a screenshot path or the page HTML.
    /// </summary>
    private async Task<string?> ExecuteAccessLocalhostInBrowserAsync(
        AgentJobDB job, CancellationToken ct)
    {
        var payload = DeserializePayload<AccessLocalhostPayload>(job,
            "AccessLocalhostInBrowser");

        var url = ValidateLocalhostUrl(payload.Url);
        var mode = (payload.Mode ?? "html").ToLowerInvariant();

        var executable = configuration["Browser:Executable"] ?? "chrome";
        var extraArgs = configuration["Browser:Arguments"] ?? "--incognito";

        // Build headless Chrome arguments.
        // --dump-dom returns the serialised DOM as text.
        // --screenshot returns a PNG screenshot to a temp file.
        var tempFile = mode == "screenshot"
            ? Path.Combine(Path.GetTempPath(), $"sc_{job.Id:N}.png")
            : null;

        var headlessArgs = mode switch
        {
            "screenshot" => $"--headless --disable-gpu --no-sandbox {extraArgs} --screenshot=\"{tempFile}\" \"{url}\"",
            _ => $"--headless --disable-gpu --no-sandbox {extraArgs} --dump-dom \"{url}\"",
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

        await process.WaitForExitAsync(ct);

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"Browser exited with code {process.ExitCode}.\nstderr: {stderr}");

        if (mode == "screenshot" && tempFile is not null && File.Exists(tempFile))
        {
            var bytes = await File.ReadAllBytesAsync(tempFile, ct);
            File.Delete(tempFile);
            return $"Screenshot captured ({bytes.Length} bytes, base64):\n{Convert.ToBase64String(bytes)}";
        }

        return string.IsNullOrWhiteSpace(stdout) ? "(empty page)" : stdout;
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

        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
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
    // Permission dispatch
    // ═══════════════════════════════════════════════════════════════

    private Task<AgentActionResult> DispatchPermissionCheckAsync(
        Guid agentId, AgentActionType actionType, Guid? resourceId,
        ActionCaller caller, CancellationToken ct)
    {
        return actionType switch
        {
            AgentActionType.CreateSubAgent
                => actions.CreateSubAgentAsync(agentId, caller, ct: ct),
            AgentActionType.CreateContainer
                => actions.CreateContainerAsync(agentId, caller, ct: ct),
            AgentActionType.RegisterInfoStore
                => actions.RegisterInfoStoreAsync(agentId, caller, ct: ct),
            AgentActionType.AccessLocalhostInBrowser
                => actions.AccessLocalhostInBrowserAsync(agentId, caller, ct: ct),
            AgentActionType.AccessLocalhostCli
                => actions.AccessLocalhostCliAsync(agentId, caller, ct: ct),
            AgentActionType.UnsafeExecuteAsDangerousShell when resourceId.HasValue
                => actions.UnsafeExecuteAsDangerousShellAsync(agentId, resourceId.Value, caller, ct: ct),
            AgentActionType.ExecuteAsSafeShell when resourceId.HasValue
                => actions.ExecuteAsSafeShellAsync(agentId, resourceId.Value, caller, ct: ct),
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
        type is AgentActionType.UnsafeExecuteAsDangerousShell
            or AgentActionType.ExecuteAsSafeShell
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
            .Include(p => p.DefaultDangerousShellAccess)
            .Include(p => p.DefaultSafeShellAccess)
            .Include(p => p.DefaultLocalInfoStorePermission)
            .Include(p => p.DefaultExternalInfoStorePermission)
            .Include(p => p.DefaultWebsiteAccess)
            .Include(p => p.DefaultSearchEngineAccess)
            .Include(p => p.DefaultContainerAccess)
            .Include(p => p.DefaultAudioDeviceAccess)
            .Include(p => p.DefaultAgentPermission)
            .Include(p => p.DefaultTaskPermission)
            .Include(p => p.DefaultSkillPermission)
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
        AgentActionType.UnsafeExecuteAsDangerousShell => drs.DangerousShellResourceId,
        AgentActionType.ExecuteAsSafeShell => drs.SafeShellResourceId,
        AgentActionType.AccessLocalInfoStore => drs.LocalInfoStoreResourceId,
        AgentActionType.AccessExternalInfoStore => drs.ExternalInfoStoreResourceId,
        AgentActionType.AccessWebsite => drs.WebsiteResourceId,
        AgentActionType.QuerySearchEngine => drs.SearchEngineResourceId,
        AgentActionType.AccessContainer => drs.ContainerResourceId,
        AgentActionType.ManageAgent => drs.AgentResourceId,
        AgentActionType.EditTask => drs.TaskResourceId,
        AgentActionType.AccessSkill => drs.SkillResourceId,
        AgentActionType.TranscribeFromAudioDevice or
        AgentActionType.TranscribeFromAudioStream or
        AgentActionType.TranscribeFromAudioFile => drs.AudioDeviceResourceId,
        _ => null,
    };

    /// <summary>
    /// Returns the resource ID from the matching default access entry on
    /// a permission set, or <c>null</c> if no default is configured.
    /// </summary>
    private static Guid? ExtractDefaultResourceId(
        PermissionSetDB permissionSet, AgentActionType actionType) => actionType switch
    {
        AgentActionType.UnsafeExecuteAsDangerousShell
            => permissionSet.DefaultDangerousShellAccess?.SystemUserId,
        AgentActionType.ExecuteAsSafeShell
            => permissionSet.DefaultSafeShellAccess?.ContainerId,
        AgentActionType.AccessLocalInfoStore
            => permissionSet.DefaultLocalInfoStorePermission?.LocalInformationStoreId,
        AgentActionType.AccessExternalInfoStore
            => permissionSet.DefaultExternalInfoStorePermission?.ExternalInformationStoreId,
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
        CancellationToken ct)
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
            if (userPs is null || !HasMatchingGrant(userPs, actionType, resourceId))
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
            if (chPs is not null && HasMatchingGrant(chPs, actionType, resourceId))
                return true;
        }

        // Channel didn't have it — fall through to context.
        if (ch.AgentContext?.PermissionSetId is { } ctxPsId)
        {
            var ctxPs = await actions.LoadPermissionSetAsync(ctxPsId, ct);
            if (ctxPs is not null && HasMatchingGrant(ctxPs, actionType, resourceId))
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
    /// </summary>
    private static bool HasMatchingGrant(
        PermissionSetDB ps, AgentActionType actionType, Guid? resourceId) => actionType switch
    {
        // ── Global flags ──────────────────────────────────────────
        AgentActionType.CreateSubAgent    => ps.CanCreateSubAgents,
        AgentActionType.CreateContainer   => ps.CanCreateContainers,
        AgentActionType.RegisterInfoStore => ps.CanRegisterInfoStores,
        AgentActionType.AccessLocalhostInBrowser => ps.CanAccessLocalhostInBrowser,
        AgentActionType.AccessLocalhostCli       => ps.CanAccessLocalhostCli,

        // ── Per-resource grants ───────────────────────────────────
        AgentActionType.UnsafeExecuteAsDangerousShell when resourceId.HasValue
            => ps.DangerousShellAccesses.Any(a =>
                a.SystemUserId == resourceId || a.SystemUserId == WellKnownIds.AllResources),

        AgentActionType.ExecuteAsSafeShell when resourceId.HasValue
            => ps.SafeShellAccesses.Any(a =>
                a.ContainerId == resourceId || a.ContainerId == WellKnownIds.AllResources),

        AgentActionType.AccessLocalInfoStore when resourceId.HasValue
            => ps.LocalInfoStorePermissions.Any(a =>
                a.LocalInformationStoreId == resourceId || a.LocalInformationStoreId == WellKnownIds.AllResources),

        AgentActionType.AccessExternalInfoStore when resourceId.HasValue
            => ps.ExternalInfoStorePermissions.Any(a =>
                a.ExternalInformationStoreId == resourceId || a.ExternalInformationStoreId == WellKnownIds.AllResources),

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

        _ => false,
    };

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
            Segments: IsTranscriptionAction(job.ActionType)
                ? job.TranscriptionSegments
                    .OrderBy(s => s.StartTime)
                    .Select(s => new TranscriptionSegmentResponse(
                        s.Id, s.Text, s.StartTime, s.EndTime, s.Confidence, s.Timestamp))
                    .ToList()
                : null);
}
