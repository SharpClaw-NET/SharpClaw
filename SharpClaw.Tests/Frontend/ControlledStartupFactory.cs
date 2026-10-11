namespace SharpClaw.Tests.Frontend;

internal sealed class ControlledStartupFactory(Exception? failure = null) : IDisposable
{
    private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ManualResetEventSlim _release = new(false);

    internal Task WaitForEntryAsync() => _entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.CurrentContext.CancellationToken);
    internal void Release() => _release.Set();

    internal T Create<T>(Func<T> create)
    {
        _entered.TrySetResult();
        // Production factories are synchronous. Only the test's explicit Task.Run worker waits here.
        if (!_release.Wait(TimeSpan.FromSeconds(5), CancellationToken.None))
            throw new TimeoutException("The controlled startup constructor was not released.");
        if (failure is not null) throw failure;
        return create();
    }

    public void Dispose()
    {
        _release.Dispose();
        GC.SuppressFinalize(this);
    }
}
