using System.Text.Json;

using SharpClaw.Contracts.Kernel;
using SharpClaw.Contracts.Providers;

namespace SharpClaw.DefaultPackages.TestHarness;


public sealed record CapturedProviderTiming(
    int Sequence,
    string ProviderKey,
    string Surface,
    int ConfiguredDelayMs,
    long ElapsedMs,
    bool Failed)
{
    public long StartedAtTimestamp { get; init; }
    public long CompletedAtTimestamp { get; init; }
}
