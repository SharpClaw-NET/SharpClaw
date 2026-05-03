using Cronos;

namespace SharpClaw.Modules.AgentOrchestration.Services;

/// <summary>
/// Thin wrapper around the Cronos library. All cron parsing and
/// firing-time computation flows through this class so the library
/// can be swapped without touching service code.
/// </summary>
public static class CronEvaluator
{
    /// <summary>
    /// Try to parse a cron expression. Supports both standard five-field
    /// (minute-resolution) and six-field (second-resolution) formats.
    /// Returns <c>false</c> and populates <paramref name="error"/> on failure.
    /// </summary>
    public static bool TryParse(string expression, out string? error)
    {
        ArgumentNullException.ThrowIfNull(expression);
        try
        {
            Parse(expression);
            error = null;
            return true;
        }
        catch (CronFormatException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Compute the next occurrence after <paramref name="after"/>.
    /// Returns <c>null</c> when the expression can never fire again.
    /// </summary>
    public static DateTimeOffset? GetNextOccurrence(
        string expression,
        DateTimeOffset after,
        string? timezone = null)
    {
        ArgumentNullException.ThrowIfNull(expression);

        var tz = ResolveTimezone(timezone);
        return Parse(expression).GetNextOccurrence(after, tz);
    }

    /// <summary>
    /// Return the next <paramref name="count"/> occurrences after
    /// <paramref name="after"/>.
    /// </summary>
    public static IEnumerable<DateTimeOffset> GetNextOccurrences(
        string expression,
        DateTimeOffset after,
        string? timezone = null,
        int count = 10)
    {
        ArgumentNullException.ThrowIfNull(expression);

        var tz = ResolveTimezone(timezone);
        var cron = Parse(expression);
        var current = after;
        var yielded = 0;

        while (yielded < count)
        {
            var next = cron.GetNextOccurrence(current, tz);
            if (next is null) yield break;
            yield return next.Value;
            current = next.Value;
            yielded++;
        }
    }

    private static CronExpression Parse(string expression)
    {
        var format = expression.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length == 6
            ? CronFormat.IncludeSeconds
            : CronFormat.Standard;

        return CronExpression.Parse(expression, format);
    }

    private static TimeZoneInfo ResolveTimezone(string? timezone)
    {
        if (string.IsNullOrWhiteSpace(timezone))
            return TimeZoneInfo.Utc;

        return TimeZoneInfo.FindSystemTimeZoneById(timezone);
    }
}
