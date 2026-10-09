using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;

namespace SharpClaw.Runtime.Host;


internal sealed record ScopedStorageTelemetryEvent(
    string SourceId,
    string StorageName,
    string Operation,
    bool Success,
    TimeSpan Duration,
    long InputBytes,
    long OutputBytes,
    int RecordCount);
