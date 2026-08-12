using System.Runtime.ExceptionServices;
using SharpClaw.Contracts.Modules;
using SharpClaw.Core.Kernel;

namespace SharpClaw.Gateway.Infrastructure;

internal static class GatewayBackgroundActionManifest
{
    public static IReadOnlyList<SharpClawActionKey> Required { get; } =
        SharpClawActionCatalog.Kernel
            .Where(static key => key.Value.StartsWith(
                "background.",
                StringComparison.Ordinal))
            .ToArray();

    public static void Validate(KernelGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);

        var missing = Required
            .Where(key => !graph.ContainsAction(key))
            .Select(static key => key.Value)
            .ToArray();
        if (missing.Length > 0)
        {
            throw new InvalidOperationException(
                "The Gateway background action graph is incomplete. Missing actions: " +
                string.Join(", ", missing));
        }
    }
}

internal sealed record GatewayBackgroundServiceInvocation(string ServiceId);

internal sealed record GatewayBackgroundTickInvocation(
    string ServiceId,
    string Operation,
    Guid WorkId);

public sealed class GatewayBackgroundActionBoundary
{
    private readonly KernelGraph _graph;
    private readonly KernelActionDispatcher _dispatcher;

    public GatewayBackgroundActionBoundary()
        : this(CreateGraph())
    {
    }

    internal GatewayBackgroundActionBoundary(KernelGraph graph)
    {
        _graph = graph ?? throw new ArgumentNullException(nameof(graph));
        GatewayBackgroundActionManifest.Validate(graph);
        _dispatcher = new KernelActionDispatcher(
            graph,
            new KernelActionExecutionContext(
                RequestPrincipal.Anonymous,
                ExtensionFeatureSet.Empty,
                Guid.NewGuid(),
                Guid.NewGuid()));
    }

    internal GatewayBackgroundActionBoundary(
        KernelGraph graph,
        KernelActionDispatcher dispatcher)
    {
        _graph = graph ?? throw new ArgumentNullException(nameof(graph));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        GatewayBackgroundActionManifest.Validate(graph);
    }

    internal KernelGraph Graph => _graph;

    internal async ValueTask StartAsync(
        GatewayBackgroundServiceInvocation invocation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        await RunPhaseAsync(
            new SharpClawActionKey("background.service.start"),
            invocation,
            static (_, _) => ValueTask.CompletedTask,
            cancellationToken);
    }

    internal async ValueTask ExecuteTickAsync(
        GatewayBackgroundTickInvocation invocation,
        Func<CancellationToken, ValueTask> work,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        ArgumentNullException.ThrowIfNull(work);

        try
        {
            await RunPhaseAsync(
                new SharpClawActionKey("background.tick.prepare"),
                invocation,
                static (_, _) => ValueTask.CompletedTask,
                cancellationToken);
            await RunPhaseAsync(
                new SharpClawActionKey("background.tick.execute"),
                invocation,
                (_, ct) => work(ct),
                cancellationToken);
            await RunPhaseAsync(
                new SharpClawActionKey("background.tick.complete"),
                invocation,
                static (_, _) => ValueTask.CompletedTask,
                cancellationToken);
        }
        catch (KernelActionCancelledException exception)
        {
            await RunSignalOrCombineAsync(
                new SharpClawActionKey("background.tick.cancel"),
                invocation,
                exception);
            ExceptionDispatchInfo.Capture(exception).Throw();
            throw;
        }
        catch (OperationCanceledException exception)
        {
            await RunSignalOrCombineAsync(
                new SharpClawActionKey("background.tick.cancel"),
                invocation,
                exception);
            ExceptionDispatchInfo.Capture(exception).Throw();
            throw;
        }
        catch (Exception exception)
        {
            await RunSignalOrCombineAsync(
                new SharpClawActionKey("background.tick.fail"),
                invocation,
                exception);
            ExceptionDispatchInfo.Capture(exception).Throw();
            throw;
        }
    }

    internal async ValueTask StopAsync(
        GatewayBackgroundServiceInvocation invocation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        await RunPhaseAsync(
            new SharpClawActionKey("background.service.stop"),
            invocation,
            static (_, _) => ValueTask.CompletedTask,
            cancellationToken);
    }

    private async ValueTask RunSignalOrCombineAsync(
        SharpClawActionKey actionKey,
        GatewayBackgroundTickInvocation invocation,
        Exception original)
    {
        try
        {
            await RunPhaseAsync(
                actionKey,
                invocation,
                static (_, _) => ValueTask.CompletedTask,
                CancellationToken.None);
        }
        catch (Exception signalFailure)
        {
            throw new AggregateException(original, signalFailure);
        }
    }

    private async ValueTask RunPhaseAsync(
        SharpClawActionKey actionKey,
        object payload,
        Func<object?, CancellationToken, ValueTask> terminal,
        CancellationToken cancellationToken)
    {
        var terminalCompleted = 0;
        var executionContext = new KernelActionExecutionContext(
            RequestPrincipal.Anonymous,
            ExtensionFeatureSet.Empty,
            Guid.NewGuid(),
            Guid.NewGuid());

        await _dispatcher.RunRequiredWithContextAsync<KernelActionEnvelope, object>(
            executionContext,
            _graph.GetStandardAction(actionKey),
            new KernelActionEnvelope(actionKey, payload),
            async (envelope, ct) =>
            {
                if (Interlocked.CompareExchange(ref terminalCompleted, 1, 0) != 0)
                    throw new KernelActionExecutionException(
                        $"Background action '{actionKey.Value}' ran its terminal more than once.");

                await terminal(envelope.Payload, ct);
                return true;
            },
            _graph.ActionSnapshot,
            cancellationToken);

        if (Volatile.Read(ref terminalCompleted) == 0)
        {
            throw new KernelActionExecutionException(
                $"Background action '{actionKey.Value}' completed without running its terminal.");
        }
    }

    private static KernelGraph CreateGraph() =>
        new KernelGraphBuilder().Compile();
}
