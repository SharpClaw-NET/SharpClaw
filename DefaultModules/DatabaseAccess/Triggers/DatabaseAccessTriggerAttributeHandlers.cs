using SharpClaw.Contracts.Tasks;

namespace SharpClaw.Modules.DatabaseAccess.Triggers;

/// <summary>
/// Module-owned <see cref="ITaskTriggerAttributeHandler"/> for
/// <c>[OnQueryReturnsRows]</c>. Behavior preserved verbatim from the legacy
/// core parser switch, including the TASK431 SELECT COUNT(*) shape warning.
/// </summary>
internal static class DatabaseAccessTriggerAttributeHandlers
{
    public static IReadOnlyDictionary<string, ITaskTriggerAttributeHandler> All { get; } =
        new Dictionary<string, ITaskTriggerAttributeHandler>(StringComparer.Ordinal)
        {
            ["OnQueryReturnsRows"] = new OnQueryReturnsRowsHandler(),
        };

    private sealed class OnQueryReturnsRowsHandler : ITaskTriggerAttributeHandler
    {
        public TaskTriggerDefinition? Handle(TaskTriggerAttributeContext context)
        {
            var query    = context.GetStringArg(0);
            var interval = context.GetNamedIntArg("PollInterval");

            if (!IsSelectCountQuery(query))
            {
                context.Report(
                    TaskTriggerAttributeDiagnosticSeverity.Warning,
                    "TASK431",
                    "[OnQueryReturnsRows] query should be a SELECT COUNT(*) expression " +
                    "to avoid unintended side-effects.");
            }

            var p = new Dictionary<string, string?>(StringComparer.Ordinal);
            if (!string.IsNullOrEmpty(query))
                p[DatabaseAccessTriggerKeys.SqlQuery] = query;
            if (interval.HasValue)
                p[DatabaseAccessTriggerKeys.QueryPollIntervalSecs] = interval.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);

            return new TaskTriggerDefinition
            {
                TriggerKey = DatabaseAccessTriggerKeys.QueryReturnsRows,
                Parameters = p,
            };
        }

        private static bool IsSelectCountQuery(string? query)
        {
            if (string.IsNullOrWhiteSpace(query))
                return false;

            var normalized = query.Replace('\n', ' ').Replace('\r', ' ');
            var upper = normalized.Trim().ToUpperInvariant();
            return upper.StartsWith("SELECT COUNT(", StringComparison.Ordinal);
        }
    }
}
