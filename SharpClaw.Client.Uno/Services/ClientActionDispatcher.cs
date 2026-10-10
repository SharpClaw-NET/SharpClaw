using System.Collections.Concurrent;
using System.Net.Http;
using SharpClaw.Contracts.Kernel;
using SharpClaw.Core.Kernel;

namespace SharpClaw.Services;

/// <summary>Routes all Uno commands and state transitions through one Core dispatcher.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001",
    Justification = "This application-lifetime dispatcher retains reusable managed-only gates; AvailableWaitHandle is never accessed and every acquisition is released in finally. No native wait handle or disposable service is owned by these gates.")]
public sealed class ClientActionDispatcher
{
    private readonly KernelGraph _graph;
    private readonly KernelActionDispatcher _dispatcher;
    private readonly ClientActionContextSource _contextSource;
    private readonly IKernelActionRepeatEvidenceAuthority _repeatEvidenceAuthority;
    private readonly SemaphoreSlim _navigationGate = new(1, 1);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _stateGates = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _stateVersions = new(StringComparer.Ordinal);
    private long _navigationVersion;

    public ClientActionDispatcher()
        : this(new ClientActionContextSource())
    {
    }

    public ClientActionDispatcher(ClientActionContextSource contextSource)
        : this(
            ClientActionServiceSet.Create(),
            ClientActionServiceSet.CreateOptions(),
            contextSource,
            repeatEvidenceAuthority: null)
    {
    }

    internal static ClientActionDispatcher CreateProduction(
        ClientActionContextSource contextSource,
        ClientActionServiceSet.IClientActionContextSink? contextSink = null) =>
        new(
            ClientActionServiceSet.Create(contextSink),
            ClientActionServiceSet.CreateOptions(),
            contextSource,
            repeatEvidenceAuthority: null);

    internal ClientActionDispatcher(
        IEnumerable<ServiceDescriptor> serviceDescriptors,
        KernelGraphCompileOptions? options,
        ClientActionContextSource? contextSource = null,
        IKernelActionRepeatEvidenceAuthority? repeatEvidenceAuthority = null)
    {
        ArgumentNullException.ThrowIfNull(serviceDescriptors);
        _contextSource = contextSource ?? new ClientActionContextSource();

        var services = new ServiceCollection();
        foreach (var descriptor in serviceDescriptors)
            ((ICollection<ServiceDescriptor>)services).Add(descriptor);

        var serviceProvider = services.BuildServiceProvider();
        _graph = new KernelGraphBuilder().Compile(serviceProvider, options);
        _repeatEvidenceAuthority = repeatEvidenceAuthority ?? new ClientRepeatEvidenceAuthority();
        _dispatcher = new KernelActionDispatcher(
            _graph,
            _contextSource.CreateContext(),
            resultSnapshotter: new ClientActionResultSnapshotter(),
            repeatEvidenceAuthority: _repeatEvidenceAuthority);
    }

    internal KernelGraph Graph => _graph;

    public async ValueTask<TResult> RunWithContextAsync<TResult>(
        ClientActionRequestContext requestContext,
        ClientCommandInvocation invocation,
        Func<ClientCommandInvocation, CancellationToken, ValueTask<TResult>> terminal,
        CancellationToken cancellationToken = default)
    {
        using var scope = _contextSource.Push(requestContext);
        return await RunCommandAsync(invocation, terminal, cancellationToken).ConfigureAwait(true);
    }

    public async ValueTask<TResult> RunCommandAsync<TResult>(
        ClientCommandInvocation invocation,
        Func<ClientCommandInvocation, CancellationToken, ValueTask<TResult>> terminal,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        ArgumentNullException.ThrowIfNull(terminal);

        var context = CreateExecutionContextFromSource();
        try
        {
            var received = await RunActionAsync(
                context,
                ClientActionCatalog.CommandReceive,
                invocation,
                static (value, _) => ValueTask.FromResult(value),
                cancellationToken).ConfigureAwait(true);
            var validated = await RunActionAsync(
                context,
                ClientActionCatalog.CommandValidate,
                received,
                static (value, _) => ValueTask.FromResult(value),
                cancellationToken).ConfigureAwait(true);
            var dispatched = validated;
            var result = await RunActionAsync(
                context,
                ClientActionCatalog.CommandDispatch,
                validated,
                async (value, token) =>
                {
                    dispatched = value;
                    return await terminal(value, token).ConfigureAwait(true);
                },
                cancellationToken).ConfigureAwait(true);
            await RunActionAsync(
                context,
                ClientActionCatalog.CommandComplete,
                new ClientCommandSignal(dispatched.CommandId, dispatched.Operation),
                static (_, _) => ValueTask.FromResult(true),
                cancellationToken).ConfigureAwait(true);
            return result;
        }
        catch (KernelActionCancelledException)
        {
            await TrySignalAsync(context, ClientActionCatalog.CommandCancel, invocation).ConfigureAwait(true);
            throw;
        }
        catch (OperationCanceledException)
        {
            await TrySignalAsync(context, ClientActionCatalog.CommandCancel, invocation).ConfigureAwait(true);
            throw;
        }
        catch
        {
            await TrySignalAsync(context, ClientActionCatalog.CommandFail, invocation).ConfigureAwait(true);
            throw;
        }
    }

    public ValueTask<TResult> RunCommandAsync<TResult>(
        string operation,
        Func<CancellationToken, ValueTask<TResult>> terminal,
        CancellationToken cancellationToken = default) =>
        RunCommandAsync(
            new ClientCommandInvocation(
                operation,
                "CLIENT",
                operation,
                Guid.NewGuid()),
            (_, token) => terminal(token),
            cancellationToken);

    public async ValueTask RunCommandAsync(
        string operation,
        Func<CancellationToken, ValueTask> terminal,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(terminal);
        await RunCommandAsync(
            operation,
            async token =>
            {
                await terminal(token).ConfigureAwait(true);
                return true;
            },
            cancellationToken).ConfigureAwait(true);
    }

    public async ValueTask NavigateAsync(
        string route,
        string? qualifier,
        Func<ClientNavigationInvocation, CancellationToken, ValueTask> terminal,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(route);
        ArgumentNullException.ThrowIfNull(terminal);

        var context = CreateExecutionContextFromSource();
        var invocation = new ClientNavigationInvocation(
            route,
            qualifier,
            Interlocked.Read(ref _navigationVersion),
            Guid.NewGuid());
        try
        {
            var prepared = await RunActionAsync(
                context,
                ClientActionCatalog.NavigationPrepare,
                invocation,
                static (value, _) => ValueTask.FromResult(value),
                cancellationToken).ConfigureAwait(true);

            await CommitNavigationAsync(context, invocation, prepared, terminal, cancellationToken).ConfigureAwait(true);
        }
        catch (KernelActionCancelledException)
        {
            await TrySignalAsync(context, ClientActionCatalog.CommandCancel,
                new ClientCommandInvocation("navigation", "CLIENT", route, invocation.NavigationId)).ConfigureAwait(true);
            throw;
        }
        catch (OperationCanceledException)
        {
            await TrySignalAsync(context, ClientActionCatalog.CommandCancel,
                new ClientCommandInvocation("navigation", "CLIENT", route, invocation.NavigationId)).ConfigureAwait(true);
            throw;
        }
        catch
        {
            await TrySignalAsync(context, ClientActionCatalog.CommandFail,
                new ClientCommandInvocation("navigation", "CLIENT", route, invocation.NavigationId)).ConfigureAwait(true);
            throw;
        }
    }

    private async ValueTask CommitNavigationAsync(
        KernelActionExecutionContext context,
        ClientNavigationInvocation invocation,
        ClientNavigationInvocation prepared,
        Func<ClientNavigationInvocation, CancellationToken, ValueTask> terminal,
        CancellationToken cancellationToken)
    {
        await _navigationGate.WaitAsync(cancellationToken).ConfigureAwait(true);
        try
        {
            if (invocation.ExpectedVersion != Interlocked.Read(ref _navigationVersion))
            {
                throw new ClientActionConflictException(
                    $"Navigation '{prepared.Route}' conflicted with a newer navigation.");
            }

            var receipt = new ClientCommitReceipt();
            var terminalSucceeded = 0;
            await RunActionAsync(
                context,
                ClientActionCatalog.NavigationCommit,
                prepared,
                async (_, token) =>
                {
                    if (Volatile.Read(ref terminalSucceeded) != 0)
                        return receipt;

                    await terminal(prepared, token).ConfigureAwait(true);
                    Volatile.Write(ref terminalSucceeded, 1);
                    return receipt;
                },
                cancellationToken).ConfigureAwait(true);

            if (Volatile.Read(ref terminalSucceeded) == 0)
                throw new ClientActionConflictException(
                    $"Navigation '{prepared.Route}' was not committed by the host.");

            Interlocked.Increment(ref _navigationVersion);
        }
        finally
        {
            _navigationGate.Release();
        }
    }

    internal long GetNavigationVersionForTest() =>
        Interlocked.Read(ref _navigationVersion);

    public long GetStateVersion(string stateKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateKey);
        return _stateVersions.GetOrAdd(stateKey, 0);
    }

    public async ValueTask<long> CommitStateAsync(
        string stateKey,
        long expectedVersion,
        Func<CancellationToken, ValueTask> terminal,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateKey);
        ArgumentNullException.ThrowIfNull(terminal);

        var context = CreateExecutionContextFromSource();
        var invocation = new ClientStateInvocation(stateKey, expectedVersion, Guid.NewGuid());
        try
        {
            var prepared = await RunActionAsync(
                context,
                ClientActionCatalog.StatePrepare,
                invocation,
                static (value, _) => ValueTask.FromResult(value),
                cancellationToken).ConfigureAwait(true);
            return await CommitPreparedStateAsync(context, invocation, prepared, terminal, cancellationToken).ConfigureAwait(true);
        }
        catch (KernelActionCancelledException)
        {
            await TrySignalAsync(context, ClientActionCatalog.CommandCancel,
                new ClientCommandInvocation("state", "CLIENT", stateKey, invocation.MutationId)).ConfigureAwait(true);
            throw;
        }
        catch (OperationCanceledException)
        {
            await TrySignalAsync(context, ClientActionCatalog.CommandCancel,
                new ClientCommandInvocation("state", "CLIENT", stateKey, invocation.MutationId)).ConfigureAwait(true);
            throw;
        }
        catch
        {
            await TrySignalAsync(context, ClientActionCatalog.CommandFail,
                new ClientCommandInvocation("state", "CLIENT", stateKey, invocation.MutationId)).ConfigureAwait(true);
            throw;
        }
    }

    private async ValueTask<long> CommitPreparedStateAsync(
        KernelActionExecutionContext context,
        ClientStateInvocation invocation,
        ClientStateInvocation prepared,
        Func<CancellationToken, ValueTask> terminal,
        CancellationToken cancellationToken)
    {
        var stateKey = invocation.StateKey;
        var gate = _stateGates.GetOrAdd(stateKey, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(true);
        try
        {
            var currentVersion = GetStateVersion(invocation.StateKey);
            if (invocation.ExpectedVersion != currentVersion)
            {
                throw new ClientActionConflictException(
                    $"State '{invocation.StateKey}' changed from version {invocation.ExpectedVersion}.");
            }

            var receipt = new ClientCommitReceipt();
            var terminalSucceeded = 0;
            await RunActionAsync(
                context,
                ClientActionCatalog.StateCommit,
                prepared,
                async (_, token) =>
                {
                    if (Volatile.Read(ref terminalSucceeded) != 0)
                        return receipt;

                    await terminal(token).ConfigureAwait(true);
                    Volatile.Write(ref terminalSucceeded, 1);
                    return receipt;
                },
                cancellationToken).ConfigureAwait(true);

            if (Volatile.Read(ref terminalSucceeded) == 0)
                throw new ClientActionConflictException(
                    $"State '{invocation.StateKey}' was not committed by the host.");

            return _stateVersions.AddOrUpdate(
                invocation.StateKey,
                1,
                static (_, version) => checked(version + 1));
        }
        finally
        {
            gate.Release();
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031",
        Justification = "Failure/cancellation signalling is secondary to the original action failure; its observed failure must not replace that original exception or turn a failed command into success.")]
    private async ValueTask TrySignalAsync(
        KernelActionExecutionContext context,
        SharpClawActionKey actionKey,
        ClientCommandInvocation invocation)
    {
        try
        {
            await RunActionAsync(
                context,
                actionKey,
                new ClientCommandSignal(invocation.CommandId, invocation.Operation),
                static (_, _) => ValueTask.FromResult(true),
                CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            ClientStartupDiagnostics.Current.Record(ClientStartupStage.UnhandledException, exception);
            // Preserve the original command, navigation, or state failure.
        }
    }

    private async ValueTask<TResult> RunActionAsync<TPayload, TResult>(
        KernelActionExecutionContext context,
        SharpClawActionKey actionKey,
        TPayload payload,
        Func<TPayload, CancellationToken, ValueTask<TResult>> terminal,
        CancellationToken cancellationToken)
    {
        var terminalContext = ClientTerminalContext.Capture();
        var descriptor = _graph.GetStandardAction(actionKey);
        var result = await _dispatcher.RunRequiredWithContextAsync<KernelActionEnvelope, object>(
            context,
            descriptor,
            new KernelActionEnvelope(actionKey, payload),
            async (envelope, actionToken) =>
            {
                if (envelope.Action.Payload is not TPayload effectivePayload)
                    throw new KernelActionExecutionException(
                        $"Client action '{actionKey.Value}' returned an invalid payload type.");

                return (object?)await terminalContext.InvokeAsync(
                    token => terminal(effectivePayload, token), actionToken).ConfigureAwait(false)
                    ?? throw new KernelActionExecutionException(
                        $"Client action '{actionKey.Value}' returned a null result.");
            },
            _graph.ActionSnapshot,
            cancellationToken).ConfigureAwait(true);

        if (result is not TResult typedResult)
            throw new KernelActionExecutionException(
                $"Client action '{actionKey.Value}' returned an invalid result type.");
        return typedResult;
    }

    private KernelActionExecutionContext CreateExecutionContextFromSource() =>
        _contextSource.CreateContext();

    private sealed class ClientCommitReceipt
    {
    }

    private sealed class ClientActionResultSnapshotter : IKernelActionResultSnapshotter
    {
        private readonly JsonKernelActionResultSnapshotter _default = new();

        public TResult Snapshot<TResult>(TResult result) =>
            result is HttpResponseMessage
                ? result
                : _default.Snapshot(result);
    }

    private sealed class ClientRepeatEvidenceAuthority : IKernelActionRepeatEvidenceAuthority
    {
        public ValueTask<KernelActionRepeatEvidence?> AuthorizeAsync(
            KernelActionRepeatEvidenceRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<KernelActionRepeatEvidence?>(null);
        }
    }
}
