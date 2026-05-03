namespace SharpClaw.Modules.AgentOrchestration.ScheduledJobs;

// ── Requests ──────────────────────────────────────────────────────

/// <summary>
/// Create a new scheduled job. Exactly one of <see cref="CronExpression"/> or
/// <see cref="RepeatInterval"/> must be supplied; both is invalid.
/// When <see cref="NextRunAt"/> is omitted and a cron expression is given,
/// it is auto-derived from the current UTC time.
/// </summary>
public sealed record CreateScheduledJobRequest(
    string Name,
    Guid? TaskDefinitionId = null,
    DateTimeOffset? NextRunAt = null,
    TimeSpan? RepeatInterval = null,
    string? CronExpression = null,
    string? CronTimezone = null,
    MissedFirePolicy MissedFirePolicy = MissedFirePolicy.FireOnceAndRecompute,
    Dictionary<string, string>? ParameterValues = null,
    Guid? CallerAgentId = null,
    int MaxRetries = 3);

/// <summary>
/// Partial update — only non-null fields are applied.
/// </summary>
public sealed record UpdateScheduledJobRequest(
    string? Name = null,
    DateTimeOffset? NextRunAt = null,
    TimeSpan? RepeatInterval = null,
    string? CronExpression = null,
    string? CronTimezone = null,
    MissedFirePolicy? MissedFirePolicy = null,
    Dictionary<string, string>? ParameterValues = null,
    Guid? CallerAgentId = null,
    int? MaxRetries = null);

// ── Responses ─────────────────────────────────────────────────────

public sealed record ScheduledJobResponse(
    Guid Id,
    string Name,
    ScheduledTaskStatus Status,
    DateTimeOffset NextRunAt,
    TimeSpan? RepeatInterval,
    string? CronExpression,
    string? CronTimezone,
    MissedFirePolicy MissedFirePolicy,
    Guid? TaskDefinitionId,
    DateTimeOffset? LastRunAt,
    string? LastError,
    int RetryCount,
    int MaxRetries,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>Response for cron preview endpoints.</summary>
public sealed record CronPreviewResponse(
    string Expression,
    string? Timezone,
    IReadOnlyList<DateTimeOffset> NextOccurrences);
