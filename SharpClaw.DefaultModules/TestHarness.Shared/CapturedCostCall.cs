using System.Text.Json;

using SharpClaw.Contracts.Kernel;
using SharpClaw.Contracts.Providers;

namespace SharpClaw.DefaultPackages.TestHarness;


public sealed record CapturedCostCall(
    int Sequence,
    string ProviderKey,
    DateTimeOffset StartTime,
    DateTimeOffset? EndTime,
    long ElapsedMs,
    bool PermissionDenied);
