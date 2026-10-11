using Microsoft.Extensions.Hosting;

namespace SharpClaw.Tests.Frontend;

internal sealed class LateStartupHost : IHost, IAsyncDisposable
{
    private readonly Lock _gate = new();
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2213",
        Justification = "The cached Task.Run disposal joins held retirement and releases this exclusive file in DisposeCoreAsync finally, including retirement fault. Dispose/DisposeAsync both join that same actual task; controlled tests verify the handle remains owned until retirement and is released afterwards.")]
    private readonly FileStream _stream;
    private readonly ControlledOperationGate _retirement;
    private Task? _disposal;

    internal LateStartupHost(string path, ControlledOperationGate retirement)
    {
        _stream = File.Open(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        _retirement = retirement;
    }

    public IServiceProvider Services { get; } = new EmptyServices();
    public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "VSTHRD002",
        Justification = "IHost requires synchronous Dispose; the cached physical disposal runs on Task.Run and uses only context-free retirement/file operations. Async tests and production Host retirement use DisposeAsync.")]
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            _disposal ??= Task.Run(DisposeCoreAsync, CancellationToken.None);
            GC.SuppressFinalize(this);
            return new ValueTask(_disposal);
        }
    }

    private async Task DisposeCoreAsync()
    {
        try { await _retirement.RunAsync().ConfigureAwait(false); }
        finally { await _stream.DisposeAsync().ConfigureAwait(false); }
    }

    private sealed class EmptyServices : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
