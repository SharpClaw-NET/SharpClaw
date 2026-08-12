using System.Threading;
using SharpClaw.Contracts.Modules;
using SharpClaw.Core.Kernel;

namespace SharpClaw.Services;

/// <summary>Supplies validated client session authority to root action calls.</summary>
public sealed class ClientActionContextSource
{
    private readonly AsyncLocal<ClientActionRequestContext?> _ambient = new();
    private ClientActionRequestContext _session =
        new(RequestPrincipal.Anonymous, ExtensionFeatureSet.Empty);

    public KernelActionExecutionContext CreateContext()
    {
        var value = _ambient.Value ?? Volatile.Read(ref _session);
        return new KernelActionExecutionContext(
            value.Caller,
            value.Features,
            Guid.NewGuid(),
            Guid.NewGuid());
    }

    public void SetSession(
        RequestPrincipal caller,
        ExtensionFeatureSet features)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(features);
        Volatile.Write(ref _session, new ClientActionRequestContext(caller, features));
    }

    public void SetSession(ClientActionRequestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        SetSession(context.Caller, context.Features);
    }

    public static ClientActionRequestContext ForAuthenticatedUser(
        Guid userId,
        string? displayName,
        IReadOnlySet<string>? roles = null,
        ExtensionFeatureSet? features = null) =>
        new(
            new RequestPrincipal(
                userId.ToString("N"),
                displayName,
                roles ?? new HashSet<string>(StringComparer.Ordinal),
                true),
            features ?? ExtensionFeatureSet.Empty);

    public void ClearSession() =>
        Volatile.Write(
            ref _session,
            new ClientActionRequestContext(
                RequestPrincipal.Anonymous,
                ExtensionFeatureSet.Empty));

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
