using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;

namespace SharpClaw.Runtime.Host;

public interface IModuleStorageTelemetry
{
    void Record(ModuleStorageTelemetryEvent telemetryEvent);
}

public sealed record ModuleStorageTelemetryEvent(
    string ModuleId,
    string StorageName,
    string Operation,
    bool Success,
    TimeSpan Duration,
    long InputBytes,
    long OutputBytes,
    int RecordCount);

public sealed class ModuleStorageTelemetry(
    ILogger<ModuleStorageTelemetry> logger) : IModuleStorageTelemetry
{
    private static readonly Meter Meter = new("SharpClaw.Modules.Storage", "1.0.0");
    private static readonly Counter<long> OperationCounter =
        Meter.CreateCounter<long>("sharpclaw.module_storage.operations");
    private static readonly Counter<long> FailureCounter =
        Meter.CreateCounter<long>("sharpclaw.module_storage.failures");
    private static readonly Histogram<double> DurationHistogram =
        Meter.CreateHistogram<double>("sharpclaw.module_storage.duration_ms");
    private static readonly Histogram<long> InputBytesHistogram =
        Meter.CreateHistogram<long>("sharpclaw.module_storage.input_bytes");
    private static readonly Histogram<long> OutputBytesHistogram =
        Meter.CreateHistogram<long>("sharpclaw.module_storage.output_bytes");
    private static readonly Histogram<int> RecordCountHistogram =
        Meter.CreateHistogram<int>("sharpclaw.module_storage.records");

    public void Record(ModuleStorageTelemetryEvent telemetryEvent)
    {
        var tags = new TagList
        {
            { "module_id", telemetryEvent.ModuleId },
            { "storage_name", telemetryEvent.StorageName },
            { "operation", telemetryEvent.Operation },
            { "success", telemetryEvent.Success },
        };

        OperationCounter.Add(1, tags);
        if (!telemetryEvent.Success)
            FailureCounter.Add(1, tags);

        DurationHistogram.Record(telemetryEvent.Duration.TotalMilliseconds, tags);
        InputBytesHistogram.Record(telemetryEvent.InputBytes, tags);
        OutputBytesHistogram.Record(telemetryEvent.OutputBytes, tags);
        RecordCountHistogram.Record(telemetryEvent.RecordCount, tags);

        logger.LogDebug(
            "Module storage {Operation} for {ModuleId}/{StorageName} completed Success={Success} DurationMs={DurationMs:F2} Records={RecordCount}",
            telemetryEvent.Operation,
            telemetryEvent.ModuleId,
            telemetryEvent.StorageName,
            telemetryEvent.Success,
            telemetryEvent.Duration.TotalMilliseconds,
            telemetryEvent.RecordCount);
    }
}
