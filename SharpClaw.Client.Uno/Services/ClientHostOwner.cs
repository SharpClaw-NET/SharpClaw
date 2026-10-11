using Microsoft.Extensions.Hosting;

namespace SharpClaw.Services;

/// <summary>Owns the built DI Host even when asynchronous navigation initialization fails.</summary>
internal sealed class ClientHostOwner(IHost host, ClientDesktopLifetime? desktop = null) : IAsyncDisposable
{
    private readonly Lock _gate = new();
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2213",
        Justification = "The cached Task.Run retirement joins actual native-loop settlement and DisposeHostAsync. Every DisposeAsync joins that same physical task, including faults; startup registers this owner before initialization and joins it on close or late publication refusal.")]
    private readonly IHost _host = host;
    private readonly ClientDesktopLifetime? _desktop = desktop;
    private Task? _retirement;

    internal IHost Host => _host;

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            _retirement ??= Task.Run(RetireAsync, CancellationToken.None);
            GC.SuppressFinalize(this);
            return new ValueTask(_retirement);
        }
    }

    private async Task RetireAsync()
    {
        if (_desktop is { } desktop)
            await desktop.RetireAfterHostAsync(DisposeHostAsync).ConfigureAwait(false);
        else await DisposeHostAsync().ConfigureAwait(false);
    }

    private async ValueTask DisposeHostAsync()
    {
        if (_host is IAsyncDisposable asyncHost) await asyncHost.DisposeAsync().ConfigureAwait(false);
        else _host.Dispose();
    }
}
