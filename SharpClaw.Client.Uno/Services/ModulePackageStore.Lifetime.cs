using System.Runtime.ExceptionServices;

namespace SharpClaw.Services;

internal sealed partial class ModulePackageStore
{
    private readonly Lock _lifetimeGate = new();
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2213",
        Justification = "The cached context-free shutdown cancels this source, joins every retained physical operation, then disposes it in the dependency cleanup loop. The analyzer cannot follow the Task.Run/ValueTask and array ownership path; disposing before the joins is unsafe.")]
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<Task> _pending = [];
    private readonly Func<PreparedModulePackage, CancellationToken, Task>? _beforeTransfer;
    private Task? _shutdown;

    private Task<T> RunOwnedAsync<T>(Func<CancellationToken, Task<T>> work,
        CancellationToken cancellationToken, Action<T>? retire = null,
        Func<T, CancellationToken, Task>? beforeTransfer = null, bool inspection = true)
    {
        lock (_lifetimeGate)
        {
            ObjectDisposedException.ThrowIf(_shutdown is not null, this);
            cancellationToken.ThrowIfCancellationRequested();
            _pending.RemoveAll(static task => task.IsCompleted);
            var task = Task.Run(() => RunAdmittedAsync(work, retire,
                beforeTransfer, inspection, cancellationToken), CancellationToken.None);
            _pending.Add(task);
            return task;
        }
    }

    private async Task<T> RunAdmittedAsync<T>(Func<CancellationToken, Task<T>> work,
        Action<T>? retire, Func<T, CancellationToken, Task>? beforeTransfer,
        bool inspection, CancellationToken cancellationToken)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken,
            inspection ? _lifetime.Token : CancellationToken.None);
        var result = await work(lifetime.Token).ConfigureAwait(false);
        try
        {
            if (beforeTransfer is not null) await beforeTransfer(result, lifetime.Token).ConfigureAwait(false);
            lock (_lifetimeGate)
            {
                if (inspection)
                {
                    if (_shutdown is not null) throw new OperationCanceledException("Module inspection is closing.", lifetime.Token);
                    lifetime.Token.ThrowIfCancellationRequested();
                }
                return result;
            }
        }
        catch
        {
            // A candidate acquired by the physical operation still belongs here
            // until the guarded return transfers it to the caller.
            ObserveRetirement(retire, result);
            throw;
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031",
        Justification = "A failed inspection retains its original error or cancellation; secondary scratch retirement failure is observed with bounded metadata.")]
    private static void ObserveRetirement<T>(Action<T>? retire, T result)
    {
        try { retire?.Invoke(result); }
        catch (Exception exception) { ClientStartupDiagnostics.Current.Record(ClientStartupStage.UnhandledException, exception); }
    }

    internal void CloseAdmission()
    {
        lock (_lifetimeGate)
        {
            if (_shutdown is not null) return;
            var pending = _pending.Where(static task => !task.IsCompleted).ToArray();
            _shutdown = Task.Run(() => DisposeCoreAsync(pending), CancellationToken.None);
        }
    }

    public ValueTask DisposeAsync()
    {
        CloseAdmission();
        lock (_lifetimeGate)
        {
            GC.SuppressFinalize(this);
            return new ValueTask(_shutdown!);
        }
    }

    public void Dispose()
    {
        // Legacy synchronous owners must also join. App/Host use DisposeAsync;
        // all retained work runs context-free and never needs a UI continuation.
#pragma warning disable VSTHRD002
        DisposeAsync().AsTask().GetAwaiter().GetResult();
#pragma warning restore VSTHRD002
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031",
        Justification = "Shutdown joins every admitted task and attempts all dependency cleanup, then rethrows the first physical/cancellation-callback/disposal failure. Expected inspection cancellation remains its caller's outcome.")]
    private async Task DisposeCoreAsync(Task[] pending)
    {
        ExceptionDispatchInfo? failure = null;
        try { await _lifetime.CancelAsync().ConfigureAwait(false); }
        catch (Exception exception) { failure = ExceptionDispatchInfo.Capture(exception); }
        foreach (var task in pending)
        {
            try
            {
                // These are the actual Task.Run operations retained at admission.
                // Cancellation is a request; their physical settlement is required.
#pragma warning disable VSTHRD003
                await task.ConfigureAwait(false);
#pragma warning restore VSTHRD003
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
            catch (Exception exception) { failure ??= ExceptionDispatchInfo.Capture(exception); }
        }
        foreach (var resource in new IDisposable[] { _sources, _gate, _lifetime })
        {
            try { resource.Dispose(); }
            catch (Exception exception) { failure ??= ExceptionDispatchInfo.Capture(exception); }
        }
        failure?.Throw();
    }
}
