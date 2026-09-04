using SharpClaw.Contracts.Kernel;
using SharpClaw.Core.Kernel;

namespace SharpClaw.Services;

/// <summary>Supplies request-local authority to root client action calls.</summary>
public sealed class ClientActionContextSource
{
    private readonly AsyncLocal<ClientActionRequestContext?> _ambient = new();

    public KernelActionExecutionContext CreateContext()
    {
        var value = _ambient.Value ?? new ClientActionRequestContext(
            RequestPrincipal.Anonymous,
            ExtensionFeatureSet.Empty);
        return new KernelActionExecutionContext(
            value.Caller,
            value.Features,
            Guid.NewGuid(),
            Guid.NewGuid());
    }

    public IDisposable Push(ClientActionRequestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var previous = _ambient.Value;
        _ambient.Value = context;
        return new Scope(() => _ambient.Value = previous);
    }

    private sealed class Scope(Action release) : IDisposable
    {
        public void Dispose() => release();
    }
}
