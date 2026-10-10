using System.Runtime.ExceptionServices;
using SharpClaw.Contracts.Kernel;
using SharpClaw.Core.Kernel;
using SharpClaw.Runtime.BLL.Kernel;

namespace SharpClaw.Runtime.Host;

internal sealed class RemoteProxyActionBoundary
{
    private static readonly SharpClawActionKey Receive = new("runtime.request.receive");
    private static readonly SharpClawActionKey Invoke = new("runtime.request.handler.invoke");
    private static readonly SharpClawActionKey ResolveKey = new("security.api_key.resolve");
    private static readonly SharpClawActionKey ValidatePairing = new("security.remote_pairing.validate");
    private readonly KernelGraph _graph;
    private readonly KernelActionDispatcher _dispatcher;

    public RemoteProxyActionBoundary(KernelGraph graph, KernelActionDispatcher dispatcher)
    {
        _graph = graph ?? throw new ArgumentNullException(nameof(graph));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        foreach (var key in new[] { Receive, Invoke, ResolveKey, ValidatePairing })
        {
            if (!graph.ContainsAction(key))
                throw new InvalidOperationException($"The remote proxy action graph is missing '{key.Value}'.");
        }
        foreach (var key in RuntimeLifecycleActionCatalog.All)
        {
            if (!graph.ContainsAction(key))
                throw new InvalidOperationException($"The remote proxy action graph is missing '{key.Value}'.");
        }
    }

    internal async ValueTask RunRequestAsync(
        KernelActionExecutionContext executionContext,
        RemoteProxyRequestInvocation invocation,
        Func<CancellationToken, ValueTask> terminal,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(terminal);
        var received = await RunActionAsync(executionContext, Receive, invocation,
            static (payload, _) => ValueTask.FromResult(payload), cancellationToken).ConfigureAwait(false);
        await RunActionAsync(executionContext, Invoke, received, async (_, token) =>
        {
            await terminal(token).ConfigureAwait(false);
            return true;
        }, cancellationToken).ConfigureAwait(false);
    }

    internal ValueTask<bool> ResolveApiKeyAsync(
        KernelActionExecutionContext executionContext,
        string path,
        bool baseAllowed,
        CancellationToken cancellationToken) =>
        RunDecisionAsync(executionContext, ResolveKey, "resolve", path, baseAllowed, cancellationToken);

    internal ValueTask<bool> ValidatePairingAsync(
        KernelActionExecutionContext executionContext,
        string path,
        bool baseAllowed,
        CancellationToken cancellationToken) =>
        RunDecisionAsync(executionContext, ValidatePairing, "validate", path, baseAllowed, cancellationToken);

    internal async ValueTask RunLifecycleAsync(
        KernelActionExecutionContext executionContext,
        SharpClawActionKey key,
        RemoteProxyLifecycleInvocation invocation,
        Func<CancellationToken, ValueTask> terminal,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(terminal);
        if (!RuntimeLifecycleActionCatalog.Contains(key))
            throw new ArgumentException("The action is not a published Runtime lifecycle action.", nameof(key));
        var completed = await RunActionAsync(executionContext, key, invocation, async (_, token) =>
        {
            await terminal(token).ConfigureAwait(false);
            return true;
        }, cancellationToken).ConfigureAwait(false);
        if (!completed)
            throw new KernelActionExecutionException($"Remote proxy lifecycle action '{key.Value}' did not authorize completion.");
    }

    private async ValueTask<bool> RunDecisionAsync(
        KernelActionExecutionContext executionContext,
        SharpClawActionKey key,
        string operation,
        string path,
        bool baseAllowed,
        CancellationToken cancellationToken)
    {
        var result = await RunActionAsync(executionContext, key,
            new RuntimeSecurityActionInvocation(operation, path),
            (_, _) => ValueTask.FromResult(baseAllowed), cancellationToken).ConfigureAwait(false);
        return baseAllowed && result;
    }

    private async ValueTask<TResult> RunActionAsync<TPayload, TResult>(
        KernelActionExecutionContext executionContext,
        SharpClawActionKey key,
        TPayload payload,
        Func<TPayload, CancellationToken, ValueTask<TResult>> terminal,
        CancellationToken cancellationToken)
    {
        using var physicalCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var invocation = new OnceTerminal<TPayload, TResult>(terminal, physicalCancellation.Token);
        object? result = null;
        ExceptionDispatchInfo? failure = null;
        try
        {
            result = await _dispatcher.RunRequiredWithContextAsync<KernelActionEnvelope, object>(
                executionContext,
                _graph.GetStandardAction(key),
                new KernelActionEnvelope(key, payload),
                (envelope, token) => invocation.RunAsync(envelope, key, token),
                _graph.ActionSnapshot,
                cancellationToken).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Core can settle its receipt before the physical terminal; retain its failure and rethrow after the owned terminal joins.
        catch (Exception exception)
        {
            failure = ExceptionDispatchInfo.Capture(exception);
        }
#pragma warning restore CA1031
        finally
        {
            failure = await CloseAndJoinAsync(invocation, physicalCancellation, failure).ConfigureAwait(false);
        }
        failure?.Throw();
        if (!invocation.WasInvoked || result is not TResult typedResult)
            throw new KernelActionExecutionException($"Remote proxy action '{key.Value}' did not complete its required terminal.");
        return typedResult;
    }

    private static async ValueTask<ExceptionDispatchInfo?> CloseAndJoinAsync<TPayload, TResult>(
        OnceTerminal<TPayload, TResult> invocation,
        CancellationTokenSource physicalCancellation,
        ExceptionDispatchInfo? failure)
    {
        invocation.CloseAdmission();
        try
        {
            await physicalCancellation.CancelAsync().ConfigureAwait(false);
        }
#pragma warning disable CA1031 // A cancellation callback failure must not skip joining the admitted physical terminal; the first failure is rethrown by RunActionAsync.
        catch (Exception exception)
        {
            failure ??= ExceptionDispatchInfo.Capture(exception);
        }
#pragma warning restore CA1031
        try
        {
            await invocation.JoinStartedAsync().ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Observe any physical terminal failure after Core settles, preserving the earlier Core failure when both exist.
        catch (Exception exception)
        {
            failure ??= ExceptionDispatchInfo.Capture(exception);
        }
#pragma warning restore CA1031
        return failure;
    }

    private sealed class OnceTerminal<TPayload, TResult>(
        Func<TPayload, CancellationToken, ValueTask<TResult>> terminal,
        CancellationToken physicalCancellation)
    {
        private readonly TaskCompletionSource<TResult> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _invoked;

        public bool WasInvoked => Volatile.Read(ref _invoked) == 1;

        public void CloseAdmission() => Interlocked.CompareExchange(ref _invoked, 2, 0);

        public ValueTask JoinStartedAsync() => WasInvoked ? new ValueTask(_completion.Task) : ValueTask.CompletedTask;

        public async ValueTask<object> RunAsync(
            ActionContext<KernelActionEnvelope> context,
            SharpClawActionKey key,
            CancellationToken cancellationToken)
        {
            if (context.Action.Key != key || context.Action.Payload is not TPayload payload)
                throw new KernelActionExecutionException($"Remote proxy action '{key.Value}' returned an invalid payload.");
            var admission = Interlocked.CompareExchange(ref _invoked, 1, 0);
            if (admission == 2)
                throw new KernelActionExecutionException("The remote proxy terminal is no longer admitted.");
            if (admission == 1)
            {
#pragma warning disable VSTHRD003 // Repeated hooks join the same context-free terminal; it has no UI or JoinableTask dependency.
                return (object?)await _completion.Task.ConfigureAwait(false)
                    ?? throw new KernelActionExecutionException("The remote proxy terminal returned a null result.");
#pragma warning restore VSTHRD003
            }

            try
            {
                var result = await InvokeTerminalAsync(payload, cancellationToken).ConfigureAwait(false);
                if (result is null)
                    throw new KernelActionExecutionException("The remote proxy terminal returned a null result.");
                _completion.TrySetResult(result);
                return result;
            }
            catch (Exception exception)
            {
                _completion.TrySetException(exception);
                _ = _completion.Task.Exception;
                throw;
            }
        }

        private async ValueTask<TResult> InvokeTerminalAsync(TPayload payload, CancellationToken cancellationToken)
        {
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, physicalCancellation);
            cancellation.Token.ThrowIfCancellationRequested();
            return await terminal(payload, cancellation.Token).ConfigureAwait(false);
        }
    }
}
