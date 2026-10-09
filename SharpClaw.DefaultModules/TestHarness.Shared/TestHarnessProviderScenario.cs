using System.Text.Json;

using SharpClaw.Contracts.Kernel;
using SharpClaw.Contracts.Providers;

namespace SharpClaw.DefaultPackages.TestHarness;


public sealed record TestHarnessProviderScenario
{
    public IReadOnlyList<string> ModelIds { get; init; } = [TestHarnessConstants.ModelId];
    public IReadOnlyList<TestHarnessProviderTurn> Turns { get; init; } = [new()];
    public int FailuresBeforeSuccess { get; init; }
    public string FailureMessage { get; init; } = "test harness configured provider failure";
    public ProviderCostResult? CostResult { get; init; } = new(
        0.25m,
        "usd",
        [new ProviderCostDailyBucket(DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddDays(1), 0.25m)]);
}
