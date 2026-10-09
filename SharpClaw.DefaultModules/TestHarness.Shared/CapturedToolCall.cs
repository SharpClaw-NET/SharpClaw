using System.Text.Json;

using SharpClaw.Contracts.Kernel;
using SharpClaw.Contracts.Providers;

namespace SharpClaw.DefaultPackages.TestHarness;


public sealed record CapturedToolCall(
    int Sequence,
    string Kind,
    string ToolName,
    string ParametersJson,
    Guid AgentId,
    Guid ChannelId,
    Guid? ThreadId,
    Guid? JobId,
    long ElapsedMs,
    bool Failed)
{
    public long StartedAtTimestamp { get; init; }
    public long CompletedAtTimestamp { get; init; }
}
