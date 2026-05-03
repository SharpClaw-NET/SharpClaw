namespace SharpClaw.Modules.Metrics;

/// <summary>
/// Trigger and parameter keys owned by the metrics module. String values
/// are persisted verbatim in binding rows and serialized scripts.
/// </summary>
public static class MetricTriggerKeys
{
    public const string MetricThreshold = "MetricThreshold";

    // Parameter names — must match TaskTriggerDefinition property names.
    public const string Source           = "MetricSource";
    public const string Threshold        = "MetricThreshold";
    public const string Direction        = "MetricDirection";
    public const string PollIntervalSecs = "MetricPollIntervalSecs";
}
