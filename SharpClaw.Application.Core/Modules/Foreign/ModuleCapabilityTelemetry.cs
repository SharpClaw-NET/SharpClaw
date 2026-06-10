using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;

namespace SharpClaw.Application.Core.Modules.Foreign;

public interface IModuleCapabilityTelemetry
{
    void Record(ModuleCapabilityTelemetryEvent telemetryEvent);
}

public sealed record ModuleCapabilityTelemetryEvent(
    string ModuleId,
    string Path,
    bool Success,
    TimeSpan Duration);

public sealed class ModuleCapabilityTelemetry(
    ILogger<ModuleCapabilityTelemetry> logger) : IModuleCapabilityTelemetry
{
    private static readonly Meter Meter = new("SharpClaw.Modules.Capabilities", "1.0.0");
    private static readonly Counter<long> CallCounter =
        Meter.CreateCounter<long>("sharpclaw.module_capability.calls");
    private static readonly Counter<long> FailureCounter =
        Meter.CreateCounter<long>("sharpclaw.module_capability.failures");
    private static readonly Histogram<double> DurationHistogram =
        Meter.CreateHistogram<double>("sharpclaw.module_capability.duration_ms");

    public void Record(ModuleCapabilityTelemetryEvent telemetryEvent)
    {
        var tags = new TagList
        {
            { "module_id", telemetryEvent.ModuleId },
            { "path", telemetryEvent.Path },
            { "success", telemetryEvent.Success },
        };

        CallCounter.Add(1, tags);
        if (!telemetryEvent.Success)
            FailureCounter.Add(1, tags);

        DurationHistogram.Record(telemetryEvent.Duration.TotalMilliseconds, tags);

        logger.LogDebug(
            "Module capability {Path} for {ModuleId} completed Success={Success} DurationMs={DurationMs:F2}",
            telemetryEvent.Path,
            telemetryEvent.ModuleId,
            telemetryEvent.Success,
            telemetryEvent.Duration.TotalMilliseconds);
    }
}
