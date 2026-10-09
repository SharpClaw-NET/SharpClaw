using System.Collections.ObjectModel;
using SharpClaw.Contracts.Kernel;

namespace SharpClaw.Services;


public sealed record ClientCommandInvocation(
    string Operation,
    string Method,
    string Path,
    Guid CommandId,
    string? RequestTarget = null)
{
    public string EffectiveRequestTarget => RequestTarget ?? Path;
}
