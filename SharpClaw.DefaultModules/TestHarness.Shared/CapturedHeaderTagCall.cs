using System.Text.Json;

using SharpClaw.Contracts.Kernel;
using SharpClaw.Contracts.Providers;

namespace SharpClaw.DefaultPackages.TestHarness;


public sealed record CapturedHeaderTagCall(
    int Sequence,
    TestHarnessHeaderTagContext Context,
    long ElapsedMs,
    bool Failed);
