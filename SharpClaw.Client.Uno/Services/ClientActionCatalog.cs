using System.Collections.ObjectModel;
using SharpClaw.Contracts.Kernel;

namespace SharpClaw.Services;

public static class ClientActionCatalog
{
    public static SharpClawActionKey CommandReceive { get; } = Find("client.command.receive");
    public static SharpClawActionKey CommandValidate { get; } = Find("client.command.validate");
    public static SharpClawActionKey CommandDispatch { get; } = Find("client.command.dispatch");
    public static SharpClawActionKey CommandComplete { get; } = Find("client.command.complete");
    public static SharpClawActionKey CommandFail { get; } = Find("client.command.fail");
    public static SharpClawActionKey CommandCancel { get; } = Find("client.command.cancel");
    public static SharpClawActionKey NavigationPrepare { get; } = Find("client.navigation.prepare");
    public static SharpClawActionKey NavigationCommit { get; } = Find("client.navigation.commit");
    public static SharpClawActionKey StatePrepare { get; } = Find("client.state.prepare");
    public static SharpClawActionKey StateCommit { get; } = Find("client.state.commit");

    public static IReadOnlyList<SharpClawActionKey> All { get; } =
        new ReadOnlyCollection<SharpClawActionKey>(
        [
            CommandReceive,
            CommandValidate,
            CommandDispatch,
            CommandComplete,
            CommandFail,
            CommandCancel,
            NavigationPrepare,
            NavigationCommit,
            StatePrepare,
            StateCommit,
        ]);

    public static IReadOnlyList<ClientActionCoverageEntry> Coverage { get; } =
        new ReadOnlyCollection<ClientActionCoverageEntry>(
        [
            new("client.command.receive", "command ingress", CommandReceive),
            new("client.command.validate", "command validation", CommandValidate),
            new("client.command.dispatch", "command execution", CommandDispatch),
            new("client.command.complete", "command completion", CommandComplete),
            new("client.command.fail", "command failure", CommandFail),
            new("client.command.cancel", "command cancellation", CommandCancel),
            new("client.navigation.prepare", "navigation preparation", NavigationPrepare),
            new("client.navigation.commit", "navigation commit", NavigationCommit),
            new("client.state.prepare", "state preparation", StatePrepare),
            new("client.state.commit", "state commit", StateCommit),
        ]);

    public static bool Contains(SharpClawActionKey key) =>
        All.Any(action => action == key);

    private static SharpClawActionKey Find(string value) =>
        SharpClawActionCatalog.Kernel.Single(key =>
            string.Equals(key.Value, value, StringComparison.Ordinal));
}

public sealed partial record ClientActionCoverageEntry(
    string Id,
    string Boundary,
    SharpClawActionKey ActionKey);

public sealed record ClientActionRequestContext(
    RequestPrincipal Caller,
    ExtensionFeatureSet Features);

public sealed record ClientCommandInvocation(
    string Operation,
    string Method,
    string Path,
    Guid CommandId,
    string? RequestTarget = null)
{
    public string EffectiveRequestTarget => RequestTarget ?? Path;
}

public sealed record ClientCommandSignal(
    Guid CommandId,
    string Operation);

public sealed record ClientNavigationInvocation(
    string Route,
    string? Qualifier,
    long ExpectedVersion,
    Guid NavigationId);

public sealed record ClientStateInvocation(
    string StateKey,
    long ExpectedVersion,
    Guid MutationId);

public sealed class ClientActionConflictException(string message) : InvalidOperationException(message);
