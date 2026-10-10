namespace SharpClaw.Tests;

internal sealed class ControlledOperationGate(Exception? failure = null)
{
    private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _calls;

    public int Calls => Volatile.Read(ref _calls);

    public Task WaitForEntryAsync() => _entered.Task.WaitAsync(
        TimeSpan.FromSeconds(5), TestContext.CurrentContext.CancellationToken);

    public void Release() => _release.TrySetResult();

    public async ValueTask RunAsync()
    {
        Interlocked.Increment(ref _calls);
        _entered.TrySetResult();
        await _release.Task.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false);
        if (failure is not null)
            throw failure;
    }
}
