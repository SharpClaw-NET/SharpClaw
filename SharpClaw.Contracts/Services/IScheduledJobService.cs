using SharpClaw.Contracts.DTOs.Tasks;

namespace SharpClaw.Contracts.Services;

/// <summary>
/// Host-provided contract for scheduled-job CRUD, lifecycle, and cron
/// preview operations. Exposed through <c>SharpClaw.Contracts</c> so that
/// modules can map the <c>/scheduled-jobs</c> REST surface without taking
/// a project reference on <c>SharpClaw.Application.Core</c>.
/// </summary>
public interface IScheduledJobService
{
    /// <summary>Validate cron fields and persist a new scheduled job.</summary>
    Task<ScheduledJobResponse> CreateAsync(
        CreateScheduledJobRequest request, CancellationToken ct = default);

    /// <summary>Returns a single scheduled job by id, or <c>null</c>.</summary>
    Task<ScheduledJobResponse?> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>List all scheduled jobs ordered by next run time.</summary>
    Task<IReadOnlyList<ScheduledJobResponse>> ListAsync(CancellationToken ct = default);

    /// <summary>Apply a partial update to a scheduled job.</summary>
    Task<ScheduledJobResponse?> UpdateAsync(
        Guid id, UpdateScheduledJobRequest request, CancellationToken ct = default);

    /// <summary>Delete a scheduled job. Returns <c>false</c> if it does not exist.</summary>
    Task<bool> DeleteAsync(Guid id, CancellationToken ct = default);

    /// <summary>Pause a pending scheduled job.</summary>
    Task<ScheduledJobResponse?> PauseAsync(Guid id, CancellationToken ct = default);

    /// <summary>Resume a paused scheduled job, recomputing <c>NextRunAt</c> if needed.</summary>
    Task<ScheduledJobResponse?> ResumeAsync(Guid id, CancellationToken ct = default);

    /// <summary>Compute upcoming occurrences for a stored job's cron expression.</summary>
    Task<CronPreviewResponse?> PreviewJobAsync(
        Guid id, int count = 10, CancellationToken ct = default);

    /// <summary>Validate and evaluate a cron expression statelessly.</summary>
    CronPreviewResponse PreviewExpression(
        string expression, string? timezone = null, int count = 10);
}
