using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;

namespace SharpClaw.Runtime.Host;


internal interface IStorageTelemetry
{
    void Record(ScopedStorageTelemetryEvent telemetryEvent);
}
