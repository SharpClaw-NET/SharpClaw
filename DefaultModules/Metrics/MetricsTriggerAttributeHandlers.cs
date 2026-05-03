using SharpClaw.Contracts.Tasks;

namespace SharpClaw.Modules.Metrics;

/// <summary>
/// Module-owned <see cref="ITaskTriggerAttributeHandler"/> for
/// <c>[OnMetricThreshold]</c>. Behavior preserved verbatim from the legacy
/// core parser switch.
/// </summary>
internal static class MetricsTriggerAttributeHandlers
{
    public static IReadOnlyDictionary<string, ITaskTriggerAttributeHandler> All { get; } =
        new Dictionary<string, ITaskTriggerAttributeHandler>(StringComparer.Ordinal)
        {
            ["OnMetricThreshold"] = new OnMetricThresholdHandler(),
        };

    private sealed class OnMetricThresholdHandler : ITaskTriggerAttributeHandler
    {
        public TaskTriggerDefinition? Handle(TaskTriggerAttributeContext context)
        {
            var p = new Dictionary<string, string?>(StringComparer.Ordinal);
            var source = context.GetStringArg(0);
            if (!string.IsNullOrEmpty(source))
                p[MetricTriggerKeys.Source] = source;
            var threshold = context.GetNamedDoubleArg("Threshold");
            if (threshold.HasValue)
                p[MetricTriggerKeys.Threshold] = threshold.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var direction = context.GetNamedEnumArg<ThresholdDirection>("Direction") ?? ThresholdDirection.Either;
            p[MetricTriggerKeys.Direction] = direction.ToString();
            var pollInterval = context.GetNamedIntArg("PollInterval");
            if (pollInterval.HasValue)
                p[MetricTriggerKeys.PollIntervalSecs] = pollInterval.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return new TaskTriggerDefinition
            {
                TriggerKey = MetricTriggerKeys.MetricThreshold,
                Parameters = p,
            };
        }
    }
}
