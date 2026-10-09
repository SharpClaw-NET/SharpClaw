using System.Text.Json;

using SharpClaw.Contracts.Kernel;
using SharpClaw.Contracts.Providers;

namespace SharpClaw.DefaultPackages.TestHarness;


public sealed record TestHarnessCostBehavior
{
    public ProviderCostResult? Result { get; init; } = new(
        0.25m,
        "usd",
        [new ProviderCostDailyBucket(DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddDays(1), 0.25m)]);
    public int LatencyMs { get; init; }
    public bool PermissionDenied { get; init; }
}
