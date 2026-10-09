using System.Collections.ObjectModel;
using SharpClaw.Contracts.Kernel;

namespace SharpClaw.Services;


public sealed record ClientNavigationInvocation(
    string Route,
    string? Qualifier,
    long ExpectedVersion,
    Guid NavigationId);
