using System.Text.Json;

using SharpClaw.Contracts.Kernel;
using SharpClaw.Contracts.Providers;

namespace SharpClaw.DefaultPackages.TestHarness;


public sealed record TestHarnessToolBehavior
{
    public string Result { get; init; } = "test harness tool result";
    public int LatencyMs { get; init; }
    public int PayloadBytes { get; init; }
    public bool ThrowFailure { get; init; }
    public bool MalformedOutput { get; init; }
}
