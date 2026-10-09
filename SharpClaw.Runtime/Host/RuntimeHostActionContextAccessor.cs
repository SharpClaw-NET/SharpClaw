using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.Kernel;
using SharpClaw.Core.Kernel;
using SharpClaw.Runtime.BLL.Kernel;

namespace SharpClaw.Runtime.Host;


internal sealed class RuntimeHostActionContextAccessor
{
    private readonly AsyncLocal<HostActionEntryRequestContext?> _current = new();

    public HostActionEntryRequestContext? Current => _current.Value;

    public IDisposable Push(HostActionEntryRequestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var previous = _current.Value;
        _current.Value = context;
        return new RestoreScope(this, previous);
    }

    private sealed class RestoreScope(
        RuntimeHostActionContextAccessor owner,
        HostActionEntryRequestContext? previous) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                owner._current.Value = previous;
        }
    }
}
