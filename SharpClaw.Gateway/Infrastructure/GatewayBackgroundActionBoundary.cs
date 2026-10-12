using System.Runtime.ExceptionServices;
using SharpClaw.Contracts.Kernel;
using SharpClaw.Core.Kernel;

namespace SharpClaw.Gateway.Infrastructure;

internal sealed class GatewayBackgroundActionBoundary
{
    private readonly KernelGraph _graph;
    private readonly KernelActionDispatcher _dispatcher;

    public GatewayBackgroundActionBoundary(
        KernelGraph graph,
        KernelActionDispatcher dispatcher)
    {
        _graph = graph ?? throw new ArgumentNullException(nameof(graph));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        GatewayActionManifest.Validate(graph);
    }

    internal KernelGraph Graph => _graph;

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031",
        Justification = "Seal terminal admission and join the entire retained physical action chain before request/response retirement. Preserve the initiating Core receipt failure over late physical failure.")]
    internal async ValueTask<TResult> RunActionAsync<TPayload, TResult>(
        SharpClawActionKey actionKey,
        TPayload payload,
        Func<TPayload, CancellationToken, ValueTask<TResult>> terminal,
        CancellationToken cancellationToken,
        KernelActionExecutionContext? executionContext = null)
    {
        ArgumentNullException.ThrowIfNull(terminal);
        if (!GatewayActionManifest.Published.Contains(actionKey))
        {
            throw new ArgumentException(
                $"Action '{actionKey.Value}' is not a published Gateway action.",
                nameof(actionKey));
        }

        var physical = new GatewayActionTerminal<TPayload, TResult>(terminal);
        ExceptionDispatchInfo? failure = null;
        object? result = null;
        try
        {
            result = await _dispatcher.RunRequiredWithContextAsync<KernelActionEnvelope, object>(
                executionContext ?? CreateHostExecutionContext(), _graph.GetStandardAction(actionKey),
                new KernelActionEnvelope(actionKey, payload), async (envelope, token) =>
                {
                    if (envelope.Action.Key != actionKey || envelope.Action.Payload is not TPayload effective)
                        throw new KernelActionExecutionException($"Gateway action '{actionKey.Value}' returned an invalid payload.");
                    return (await physical.RunAsync(effective, token).ConfigureAwait(false))!;
                }, _graph.ActionSnapshot, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) { failure = ExceptionDispatchInfo.Capture(exception); }
        finally
        {
            try { await physical.JoinAsync().ConfigureAwait(false); }
            catch (Exception exception) { failure ??= ExceptionDispatchInfo.Capture(exception); }
        }
        failure?.Throw();
        if (!physical.WasInvoked)
            throw new KernelActionExecutionException($"Gateway action '{actionKey.Value}' completed without running its terminal.");
        return result is TResult typedResult ? typedResult : await physical.ResultAsync().ConfigureAwait(false);
    }

    internal async ValueTask StartAsync(
        GatewayBackgroundServiceInvocation invocation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        await RunPhaseAsync(
            new SharpClawActionKey("background.service.start"),
            invocation,
            static (_, _) => ValueTask.CompletedTask,
            cancellationToken).ConfigureAwait(false);
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
                cancellationToken).ConfigureAwait(false);
            await RunPhaseAsync(
                new SharpClawActionKey("background.tick.execute"),
                invocation,
                (_, ct) => work(ct),
                cancellationToken).ConfigureAwait(false);
            await RunPhaseAsync(
                new SharpClawActionKey("background.tick.complete"),
                invocation,
                static (_, _) => ValueTask.CompletedTask,
                cancellationToken).ConfigureAwait(false);
        }
        catch (KernelActionCancelledException exception)
        {
            await RunSignalOrCombineAsync(
                new SharpClawActionKey("background.tick.cancel"),
                invocation,
                exception).ConfigureAwait(false);
            ExceptionDispatchInfo.Capture(exception).Throw();
            throw;
        }
        catch (OperationCanceledException exception)
        {
            await RunSignalOrCombineAsync(
                new SharpClawActionKey("background.tick.cancel"),
                invocation,
                exception).ConfigureAwait(false);
            ExceptionDispatchInfo.Capture(exception).Throw();
            throw;
        }
        catch (Exception exception)
        {
            await RunSignalOrCombineAsync(
                new SharpClawActionKey("background.tick.fail"),
                invocation,
                exception).ConfigureAwait(false);
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
            cancellationToken).ConfigureAwait(false);
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
                CancellationToken.None).ConfigureAwait(false);
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
        await RunActionAsync<object, bool>(
            actionKey,
            payload,
            async (_, ct) =>
            {
                await terminal(payload, ct).ConfigureAwait(false);
                return true;
            },
            cancellationToken).ConfigureAwait(false);
    }

    private static KernelActionExecutionContext CreateHostExecutionContext() =>
        new(
            RequestPrincipal.Anonymous,
            ExtensionFeatureSet.Empty,
            Guid.NewGuid(),
            Guid.NewGuid());

}
