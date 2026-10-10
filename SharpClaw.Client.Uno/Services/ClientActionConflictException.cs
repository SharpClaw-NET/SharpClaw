using System.Collections.ObjectModel;
using SharpClaw.Contracts.Kernel;

namespace SharpClaw.Services;


public sealed class ClientActionConflictException : InvalidOperationException
{
    public ClientActionConflictException() { }

    public ClientActionConflictException(string message) : base(message) { }

    public ClientActionConflictException(string message, Exception innerException)
        : base(message, innerException) { }
}
