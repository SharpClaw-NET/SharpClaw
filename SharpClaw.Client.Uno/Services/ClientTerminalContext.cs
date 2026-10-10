using System.Diagnostics.CodeAnalysis;

namespace SharpClaw.Services;

/// <summary>Preserves client thread affinity without adding UI policy to Core.</summary>
internal readonly struct ClientTerminalContext(SynchronizationContext? context)
{
    private readonly SynchronizationContext? _context = context;

    internal static ClientTerminalContext Capture() => new(SynchronizationContext.Current);

    internal ValueTask<TResult> InvokeAsync<TResult>(
        Func<CancellationToken, ValueTask<TResult>> terminal,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_context is null || ReferenceEquals(_context, SynchronizationContext.Current))
            return terminal(cancellationToken);

        return PostAsync(_context, terminal, cancellationToken);
    }

    [SuppressMessage("Design", "CA1031", Justification =
        "Queue failures are propagated through the completion task while atomically preventing late queued effects; they are not swallowed.")]
    [SuppressMessage("Usage", "VSTHRD001", Justification =
        "Uno requires the SynchronizationContext captured from its renderer, not Visual Studio JoinableTaskFactory. This Post is nonblocking and propagates queue cancellation/fault through one owned completion task.")]
    private static async ValueTask<TResult> PostAsync<TResult>(
        SynchronizationContext context,
        Func<CancellationToken, ValueTask<TResult>> terminal,
        CancellationToken cancellationToken)
    {
        // Capture the authorized kernel terminal's ambient scope, not the
        // earlier UI caller's scope. Only scheduling affinity comes from UI.
        var executionContext = ExecutionContext.Capture()
            ?? throw new InvalidOperationException(
                "A client terminal cannot cross UI threads with execution-context flow suppressed.");
        var work = new PostedTerminal<TResult>(terminal, executionContext, cancellationToken);
        using var cancellation = cancellationToken.UnsafeRegister(
            static state => ((PostedTerminal<TResult>)state!).CancelQueued(), work);
        try
        {
            context.Post(static state => ((PostedTerminal<TResult>)state!).Start(), work);
        }
        catch (Exception exception)
        {
            work.FailQueued(exception);
        }

        return await work.Completion.ConfigureAwait(false);
    }

    private sealed class PostedTerminal<TResult>(
        Func<CancellationToken, ValueTask<TResult>> terminal,
        ExecutionContext executionContext,
        CancellationToken cancellationToken)
    {
        private readonly TaskCompletionSource<TResult> _completion = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int _startedOrAbandoned;

        internal Task<TResult> Completion => _completion.Task;

        internal void CancelQueued()
        {
            if (Interlocked.CompareExchange(ref _startedOrAbandoned, 1, 0) == 0)
                _completion.TrySetCanceled(cancellationToken);
        }

        internal void FailQueued(Exception exception)
        {
            if (Interlocked.CompareExchange(ref _startedOrAbandoned, 1, 0) == 0)
                _completion.TrySetException(exception);
        }

        internal void Start()
        {
            if (Interlocked.CompareExchange(ref _startedOrAbandoned, 1, 0) != 0)
                return;

            ExecutionContext.Run(executionContext,
                static state => _ = ((PostedTerminal<TResult>)state!).CompleteAsync(), this);
        }

        [SuppressMessage("Design", "CA1031", Justification =
            "Every terminal exception must fault its completion task with the original exception instead of escaping the UI callback or leaving a pending receipt.")]
        private async Task CompleteAsync()
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Only invoking the terminal requires UI affinity. Completing
                // its non-UI receipt must not need another UI queue turn.
                _completion.TrySetResult(await terminal(cancellationToken).ConfigureAwait(false));
            }
            catch (OperationCanceledException exception)
            {
                _completion.TrySetCanceled(exception.CancellationToken);
            }
            catch (Exception exception)
            {
                _completion.TrySetException(exception);
            }
            // After Start wins, cancellation cannot complete this receipt ahead
            // of the terminal. Core still owns deadlines and uncertain effects.
        }
    }
}
