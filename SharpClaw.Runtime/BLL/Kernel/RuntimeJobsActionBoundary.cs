using SharpClaw.Contracts.Modules;
using SharpClaw.Core.Kernel;

namespace SharpClaw.Runtime.BLL.Kernel;

/// <summary>Runs one Jobs family through its before, root, and after actions.</summary>
internal sealed class RuntimeJobsActionBoundary(
    KernelGraph graph,
    KernelActionDispatcher dispatcher,
    Func<KernelActionExecutionContext> executionContextFactory)
{
    public async ValueTask<object?> RunAsync<TFamily>(
        KernelActionExecutionContext executionContext,
        SharpClawActionKey actionKey,
        object? value,
        Func<object?, CancellationToken, ValueTask<object?>> terminal,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(executionContext);
        ArgumentNullException.ThrowIfNull(terminal);
        if (!RuntimeJobsActionManifest.Families.Contains(
                actionKey.Value,
                StringComparer.Ordinal))
        {
            throw new ArgumentException(
                $"Action '{actionKey}' is not a published Jobs family root.",
                nameof(actionKey));
        }

        var input = new RuntimeJobsActionModule.RuntimeJobsInput<TFamily>(value);
        var beforeKey = new SharpClawActionKey($"{actionKey.Value}.before");
        var afterKey = new SharpClawActionKey($"{actionKey.Value}.after");
        var checkpoint = new JobCheckpoint<RuntimeJobsActionModule.RuntimeJobsInput<TFamily>>(
            null,
            null,
            Guid.NewGuid(),
            Guid.NewGuid(),
            JobStatus.Pending,
            null,
            JobSafePoint.BeforeTerminal,
            input,
            0);

        var beforeCompleted = 0;
        var prepared = await dispatcher.RunRequiredWithContextAsync(
            executionContext,
            graph.GetJobsBeforeAction<RuntimeJobsActionModule.RuntimeJobsInput<TFamily>>(beforeKey),
            checkpoint,
            (effective, _) =>
            {
                Interlocked.Exchange(ref beforeCompleted, 1);
                return ValueTask.FromResult(effective);
            },
            graph.ActionSnapshot,
            cancellationToken);

        if (Volatile.Read(ref beforeCompleted) == 0)
        {
            throw new KernelActionExecutionException(
                $"Jobs action '{beforeKey.Value}' completed without running its terminal.");
        }

        var rootCompleted = 0;
        var rootResult = new TaskCompletionSource<RuntimeJobsActionModule.RuntimeJobsResult<TFamily>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var result = await dispatcher.RunRequiredWithContextAsync(
            executionContext,
            graph.GetJobsAction<
                RuntimeJobsActionModule.RuntimeJobsInput<TFamily>,
                RuntimeJobsActionModule.RuntimeJobsResult<TFamily>>(actionKey),
            prepared.Value,
            async (effective, ct) =>
            {
                if (Interlocked.CompareExchange(ref rootCompleted, 1, 0) != 0)
                    return await rootResult.Task.WaitAsync(ct);

                try
                {
                    var completed = new RuntimeJobsActionModule.RuntimeJobsResult<TFamily>(
                        await terminal(effective.Value, ct));
                    rootResult.TrySetResult(completed);
                    return completed;
                }
                catch (Exception exception)
                {
                    rootResult.TrySetException(exception);
                    throw;
                }
            },
            graph.ActionSnapshot,
            cancellationToken);

        if (Volatile.Read(ref rootCompleted) == 0)
        {
            throw new KernelActionExecutionException(
                $"Jobs action '{actionKey.Value}' completed without running its terminal.");
        }

        var afterCompleted = 0;
        var completed = await dispatcher.RunRequiredWithContextAsync(
            executionContext,
            graph.GetJobsAfterAction<RuntimeJobsActionModule.RuntimeJobsResult<TFamily>>(afterKey),
            new JobCheckpoint<RuntimeJobsActionModule.RuntimeJobsResult<TFamily>>(
                prepared.JobId,
                prepared.AttemptId,
                prepared.InvocationId,
                prepared.IdempotencyKey,
                JobStatus.Completed,
                JobStatus.Completed,
                JobSafePoint.AfterTerminal,
                result,
                prepared.ExpectedRevision),
            (effective, _) =>
            {
                Interlocked.Exchange(ref afterCompleted, 1);
                return ValueTask.FromResult(effective);
            },
            graph.ActionSnapshot,
            cancellationToken);

        if (Volatile.Read(ref afterCompleted) == 0)
        {
            throw new KernelActionExecutionException(
                $"Jobs action '{afterKey.Value}' completed without running its terminal.");
        }

        return completed.Value.Value;
    }

    public ValueTask<object?> RunAsync<TFamily>(
        SharpClawActionKey actionKey,
        object? value,
        Func<object?, CancellationToken, ValueTask<object?>> terminal,
        CancellationToken cancellationToken = default) =>
        RunAsync<TFamily>(
            executionContextFactory(),
            actionKey,
            value,
            terminal,
            cancellationToken);
}
