using System.Collections.ObjectModel;
using SharpClaw.Contracts.Kernel;

namespace SharpClaw.Services;


[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1515",
    Justification = "The net10.0 headless target exposes this public command invocation data. The Uno desktop target shares its declaration; changing accessibility would split the existing source/binary contract across these targets.")]
public sealed record ClientCommandInvocation(
    string Operation,
    string Method,
    string Path,
    Guid CommandId,
    string? RequestTarget = null)
{
    public string EffectiveRequestTarget => RequestTarget ?? Path;
}
