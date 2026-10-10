using System.Text.Json;
using SharpClaw.Contracts.Kernel;
using SharpClaw.ModuleSDK;

namespace SharpClaw.DefaultPackages.TestHarness;

#if TEST_HARNESS_IN_PROCESS
public sealed class TestHarnessScopedCliHandler : ICliHandler, IDisposable
{
    private static int _created;
    private static int _disposed;
    private static int _active;
    private readonly Guid _instanceId = Guid.NewGuid();
    private int _isDisposed;

    public TestHarnessScopedCliHandler()
    {
        Interlocked.Increment(ref _created);
        Interlocked.Increment(ref _active);
    }

    public ValueTask<CliResult> ExecuteAsync(
        CliInvocation invocation,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new CliResult(
            true,
            [new CliOutput("stdout", JsonSerializer.Serialize(new
            {
                instanceId = _instanceId,
                created = Volatile.Read(ref _created),
                disposed = Volatile.Read(ref _disposed),
                active = Volatile.Read(ref _active),
            }))]));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) != 0)
            return;
        Interlocked.Decrement(ref _active);
        Interlocked.Increment(ref _disposed);
    }
}
#endif
