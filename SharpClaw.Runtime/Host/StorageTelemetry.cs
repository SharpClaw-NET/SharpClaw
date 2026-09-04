using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;

namespace SharpClaw.Runtime.Host;

public interface IStorageTelemetry
{
    void Record(ScopedStorageTelemetryEvent telemetryEvent);
}

public sealed record ScopedStorageTelemetryEvent(
    string SourceId,
    string StorageName,
    string Operation,
    bool Success,
    TimeSpan Duration,
    long InputBytes,
    long OutputBytes,
    int RecordCount);

public sealed class ScopedStorageTelemetry(
    ILogger<ScopedStorageTelemetry> logger) : IStorageTelemetry
{
    private static readonly Meter Meter = new("SharpClaw.Registrations.Storage", "1.0.0");
    private static readonly Counter<long> OperationCounter =
        Meter.CreateCounter<long>("sharpclaw.registration_storage.operations");
    private static readonly Counter<long> FailureCounter =
        Meter.CreateCounter<long>("sharpclaw.registration_storage.failures");
    private static readonly Histogram<double> DurationHistogram =
        Meter.CreateHistogram<double>("sharpclaw.registration_storage.duration_ms");
    private static readonly Histogram<long> InputBytesHistogram =
        Meter.CreateHistogram<long>("sharpclaw.registration_storage.input_bytes");
    private static readonly Histogram<long> OutputBytesHistogram =
        Meter.CreateHistogram<long>("sharpclaw.registration_storage.output_bytes");
    private static readonly Histogram<int> RecordCountHistogram =
        Meter.CreateHistogram<int>("sharpclaw.registration_storage.records");

    public void Record(ScopedStorageTelemetryEvent telemetryEvent)
    {
        var tags = new TagList
        {
            { "registration_id", telemetryEvent.SourceId },
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
            "Registration storage {Operation} for {SourceId}/{StorageName} completed Success={Success} DurationMs={DurationMs:F2} Records={RecordCount}",
            telemetryEvent.Operation,
            telemetryEvent.SourceId,
            telemetryEvent.StorageName,
            telemetryEvent.Success,
            telemetryEvent.Duration.TotalMilliseconds,
            telemetryEvent.RecordCount);
    }
}
