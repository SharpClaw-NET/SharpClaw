using System.Text.Json;

using SharpClaw.Contracts.Kernel;
using SharpClaw.Contracts.Providers;

namespace SharpClaw.DefaultPackages.TestHarness;


public sealed record TestHarnessHeaderTagBehavior
{
    public string Value { get; init; } = "test harness header tag";
    public int LatencyMs { get; init; }
    public bool ThrowFailure { get; init; }
}
