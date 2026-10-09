using System.Collections.ObjectModel;
using SharpClaw.Contracts.Kernel;

namespace SharpClaw.Services;


public sealed record ClientStateInvocation(
    string StateKey,
    long ExpectedVersion,
    Guid MutationId);
