using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using SharpClaw.Contracts.DTOs.AgentActions;
using SharpClaw.Contracts.DTOs.Chat;
using SharpClaw.Contracts.DTOs.Diagnostics;
using SharpClaw.Contracts.Entities.Core.Jobs;
using SharpClaw.Contracts.Enums;
using SharpClaw.Core.Jobs;
using SharpClaw.Runtime.INF.DurableStorage;
using SharpClaw.Runtime.INF.Persistence;
using SharpClaw.Shared.DurableStorage;

namespace SharpClaw.Runtime.BLL.Services;

/// <summary>
/// Runtime implementation of Core's storage-neutral execution persistence
/// ports. It translates decisions and events into compact EF state, durable
/// diagnostic records, and encrypted artifacts.
/// </summary>
public sealed class DurableExecutionPersistence(
    SharpClawDbContext db,
    ExecutionDiagnosticStore diagnostics,
    IExecutionArtifactStore artifacts)
{
    private const int PreviewBytes = 2048;
    private const int ErrorMessageBytes = 2048;

    public async Task PersistJobDecisionAsync(
        AgentJobDB job,
        AgentJobLifecycleDecision decision,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(decision);
        var isTerminal = IsTerminal(job.Status);
        var writeMode = isTerminal
            ? DurableWriteMode.Buffered
            : DurableWriteMode.Durable;

        for (var index = 0; index < decision.Logs.Count; index++)
        {
            var log = decision.Logs[index];
            var receipt = await diagnostics.AppendJobLogAsync(
                job.Id,
                log.Message,
                log.Level,
                "JobLifecycle",
                recordId: DeriveRecordId(decision.DecisionId, index),
                writeMode: writeMode,
                cancellationToken: cancellationToken);
            SetJobLogSummary(job, receipt.Sequence, receipt.RecordCount);
        }

        if (decision.UpdateResult)
        {
            await PersistResultAsync(
                job,
                decision.Result,
                cancellationToken);
        }

        if (decision.UpdateFailure)
        {
            if (!string.IsNullOrWhiteSpace(decision.ErrorDetails))
            {
                var receipt = await diagnostics.AppendJobLogAsync(
                    job.Id,
                    decision.ErrorDetails,
                    "Error",
                    "ExecutionFailureDetail",
                    recordId: DeriveRecordId(
                        decision.DecisionId,
                        decision.Logs.Count),
                    writeMode: writeMode,
                    cancellationToken: cancellationToken);
                SetJobLogSummary(job, receipt.Sequence, receipt.RecordCount);
            }

            SetShadow(
                job,
                ExecutionMetadataColumns.ErrorCode,
                BoundUtf8(decision.ErrorCode, 128));
            SetShadow(
                job,
                ExecutionMetadataColumns.ErrorMessage,
                BoundUtf8(decision.ErrorMessage, ErrorMessageBytes));
        }

        SetShadow(
            job,
            ExecutionMetadataColumns.DiagnosticCompleteness,
            DiagnosticCompleteness.Complete);
        TrackJobAudit(job, decision.DecisionId);
        if (isTerminal)
            await diagnostics.SealJobAsync(job.Id, cancellationToken);
        await db.SaveChangesThroughKernelAsync(cancellationToken);
    }

    public Task SaveJobStateAsync(CancellationToken cancellationToken) =>
        db.SaveChangesThroughKernelAsync(cancellationToken);

    public async Task AppendJobLogAsync(
        Guid jobId,
        string message,
        string level,
        string eventName,
        CancellationToken cancellationToken)
    {
        var job = db.AgentJobs.Local
                      .FirstOrDefault(candidate => candidate.Id == jobId)
            ?? await db.AgentJobs.FirstOrDefaultAsync(
                candidate => candidate.Id == jobId,
                cancellationToken);
        if (job is null)
            return;

        var receipt = await diagnostics.AppendJobLogAsync(
            jobId,
            message,
            level,
            eventName,
            cancellationToken: cancellationToken);
        SetJobLogSummary(job, receipt.Sequence, receipt.RecordCount);
        SetShadow(
            job,
            ExecutionMetadataColumns.DiagnosticCompleteness,
            DiagnosticCompleteness.Complete);
        await db.SaveChangesThroughKernelAsync(cancellationToken);
    }

    public AgentJobDetailResponse ToJobDetail(
        AgentJobDB job,
        ChannelCostResponse? channelCost = null)
    {
        ArgumentNullException.ThrowIfNull(job);
        var values = db.Entry(job).CurrentValues;
        var artifactId = GetShadow<Guid?>(
            values,
            ExecutionMetadataColumns.ResultArtifactId);
        var mediaType = GetShadow<string?>(
            values,
            ExecutionMetadataColumns.ResultMediaType);
        var length = GetShadow<long?>(
            values,
            ExecutionMetadataColumns.ResultLength);
        var sha256 = GetShadow<string?>(
            values,
            ExecutionMetadataColumns.ResultSha256);
        var artifact = artifactId is { } id
            && mediaType is not null
            && length is { } artifactLength
            && sha256 is not null
                ? new ExecutionArtifactResponse(
                    id,
                    mediaType,
                    artifactLength,
                    sha256,
                    GetShadow<string?>(
                        values,
                        ExecutionMetadataColumns.ResultPreview))
                : null;
        var usage = job.PromptTokens is not null
            || job.CompletionTokens is not null
                ? new TokenUsageResponse(
                    job.PromptTokens ?? 0,
                    job.CompletionTokens ?? 0,
                    (job.PromptTokens ?? 0) + (job.CompletionTokens ?? 0))
                : null;

        return new AgentJobDetailResponse(
            job.Id,
            job.ChannelId,
            job.AgentId,
            job.ActionKey,
            job.ResourceId,
            job.Status,
            job.EffectiveClearance,
            artifact,
            GetShadow<string?>(values, ExecutionMetadataColumns.ErrorCode),
            GetShadow<string?>(values, ExecutionMetadataColumns.ErrorMessage),
            GetShadow<DiagnosticCompleteness>(
                values,
                ExecutionMetadataColumns.DiagnosticCompleteness),
            GetShadow<long?>(
                values,
                ExecutionMetadataColumns.FinalLogSequence),
            GetShadow<long>(values, ExecutionMetadataColumns.LogRecordCount),
            job.CreatedAt,
            job.StartedAt,
            job.CompletedAt,
            job.ScriptJson,
            job.WorkingDirectory,
            usage,
            channelCost);
    }

    private async Task PersistResultAsync(
        AgentJobDB job,
        string? result,
        CancellationToken cancellationToken)
    {
        if (result is null)
        {
            SetShadow<Guid?>(
                job,
                ExecutionMetadataColumns.ResultArtifactId,
                null);
            SetShadow<string?>(
                job,
                ExecutionMetadataColumns.ResultMediaType,
                null);
            SetShadow<long?>(
                job,
                ExecutionMetadataColumns.ResultLength,
                null);
            SetShadow<string?>(
                job,
                ExecutionMetadataColumns.ResultSha256,
                null);
            SetShadow<string?>(
                job,
                ExecutionMetadataColumns.ResultPreview,
                null);
            return;
        }

        var bytes = Encoding.UTF8.GetBytes(result);
        var digest = Convert.ToHexString(SHA256.HashData(bytes))
            .ToLowerInvariant();
        if (string.Equals(
                GetShadow<string?>(
                    job,
                    ExecutionMetadataColumns.ResultSha256),
                digest,
                StringComparison.Ordinal))
        {
            return;
        }

        await using var content = new MemoryStream(bytes, writable: false);
        var descriptor = await artifacts.PutAsync(
            content,
            new ArtifactWriteRequest(
                ExecutionOwnerKind.AgentJob,
                job.Id,
                "text/plain; charset=utf-8",
                BoundUtf8(result, PreviewBytes)),
            cancellationToken);
        SetShadow(
            job,
            ExecutionMetadataColumns.ResultArtifactId,
            descriptor.Id);
        SetShadow(
            job,
            ExecutionMetadataColumns.ResultMediaType,
            descriptor.MediaType);
        SetShadow(
            job,
            ExecutionMetadataColumns.ResultLength,
            descriptor.Length);
        SetShadow(
            job,
            ExecutionMetadataColumns.ResultSha256,
            descriptor.Sha256);
        SetShadow(
            job,
            ExecutionMetadataColumns.ResultPreview,
            descriptor.Preview);
    }

    private void SetJobLogSummary(
        AgentJobDB job,
        long sequence,
        long count)
    {
        SetShadow(
            job,
            ExecutionMetadataColumns.FinalLogSequence,
            sequence);
        SetShadow(job, ExecutionMetadataColumns.LogRecordCount, count);
    }

    private void TrackJobAudit(AgentJobDB job, Guid decisionId)
    {
        var entry = db.Entry(job);
        TrackStateAudit(
            decisionId,
            ExecutionOwnerKind.AgentJob,
            job.Id,
            entry.State == EntityState.Added
                ? null
                : entry.Property(candidate => candidate.Status)
                    .OriginalValue.ToString(),
            job.Status.ToString());
    }

    private void TrackStateAudit(
        Guid auditId,
        ExecutionOwnerKind ownerKind,
        Guid ownerId,
        string? previousState,
        string newState)
    {
        if (previousState is not null
            && string.Equals(previousState, newState, StringComparison.Ordinal))
        {
            return;
        }

        if (db.ExecutionAuditEvents.Local.Any(audit => audit.Id == auditId))
            return;

        db.ExecutionAuditEvents.Add(new ExecutionAuditEventDB
        {
            Id = auditId,
            OwnerKind = ownerKind,
            OwnerId = ownerId,
            EventKind = previousState is null ? "Created" : "StateChanged",
            PreviousState = previousState,
            NewState = newState,
        });
    }

    private void SetShadow<TValue>(
        object entity,
        string propertyName,
        TValue value)
    {
        db.Entry(entity).Property(propertyName).CurrentValue = value;
    }

    private TValue GetShadow<TValue>(
        object entity,
        string propertyName)
    {
        var value = db.Entry(entity).Property(propertyName).CurrentValue;
        return value is null ? default! : (TValue)value;
    }

    private static TValue GetShadow<TValue>(
        PropertyValues values,
        string propertyName)
    {
        var value = values[propertyName];
        return value is null ? default! : (TValue)value;
    }

    private static Guid DeriveRecordId(Guid decisionId, int ordinal)
    {
        Span<byte> input = stackalloc byte[20];
        decisionId.TryWriteBytes(input[..16]);
        BinaryPrimitives.WriteInt32LittleEndian(input[16..], ordinal);
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(input, digest);
        return new Guid(digest[..16]);
    }

    private static bool IsTerminal(AgentJobStatus status) => status is
        AgentJobStatus.Completed
        or AgentJobStatus.Failed
        or AgentJobStatus.Denied
        or AgentJobStatus.Cancelled;

    private static string? BoundUtf8(string? value, int maxBytes)
    {
        if (value is null || Encoding.UTF8.GetByteCount(value) <= maxBytes)
            return value;

        var length = Math.Min(value.Length, maxBytes);
        while (length > 0
               && Encoding.UTF8.GetByteCount(value.AsSpan(0, length))
                   > maxBytes)
        {
            length--;
        }

        return value[..length];
    }
}
