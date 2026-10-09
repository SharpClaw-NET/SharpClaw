using SharpClaw.Contracts.Kernel;
using SharpClaw.Core.Kernel;

namespace SharpClaw.Runtime.BLL.Kernel;


public sealed record RuntimeEventOutboxTransition(
    string RecordKey,
    string? Error,
    bool IsCancellation);
