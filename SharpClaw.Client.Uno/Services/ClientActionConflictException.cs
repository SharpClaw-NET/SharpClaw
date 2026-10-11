using System.Collections.ObjectModel;
using SharpClaw.Contracts.Kernel;

namespace SharpClaw.Services;


[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1515",
    Justification = "The net10.0 headless target exposes this public state-conflict exception. The Uno desktop target shares its declaration; changing accessibility would split the existing source/binary contract across these targets.")]
public sealed class ClientActionConflictException : InvalidOperationException
{
    public ClientActionConflictException() { }

    public ClientActionConflictException(string message) : base(message) { }

    public ClientActionConflictException(string message, Exception innerException)
        : base(message, innerException) { }
}
