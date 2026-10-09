using SharpClaw.Contracts.Kernel;

namespace SharpClaw.Runtime.BLL.Kernel;


/// <summary>Non-secret metadata carried by one Runtime CLI action.</summary>
internal sealed record RuntimeCliActionInvocation(
    string Stage,
    string? Command,
    int ArgumentCount,
    string? FailureType = null);
