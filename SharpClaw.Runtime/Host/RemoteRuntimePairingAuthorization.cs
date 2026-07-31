using System.Security.Cryptography.X509Certificates;
using SharpClaw.Shared.Instances;
using SharpClaw.Shared.RemoteRuntimeBridge;

namespace SharpClaw.Runtime.Host;

internal static class RemoteRuntimePairingAuthorization
{
    public static async Task<RemoteRuntimeProxySession> LoadApprovedSessionAsync(
        RuntimeLaunchPlan plan,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        cancellationToken.ThrowIfCancellationRequested();

        var instancePaths = RuntimeInstancePathResolver.CreateBackend();
        plan.RequireRemoteProxyOptions();

        var expectedSessionPath = Path.Combine(
            instancePaths.RemoteRuntimeProxyStateDirectory,
            ".env");

        if (!File.Exists(expectedSessionPath))
        {
            throw new InvalidOperationException(
                "RemoteProxy mode requires an existing approved pairing session before binding.");
        }

        var store = RemoteRuntimeProxySessionStore.Create(instancePaths);
        var state = await store.ReadAsync(cancellationToken)
            ?? throw new InvalidOperationException(
                "RemoteProxy mode requires an active approved pairing session before binding.");
        var clientCertificate = await store.LoadClientCertificateAsync(state, cancellationToken);
        return new RemoteRuntimeProxySession(instancePaths, state, clientCertificate);
    }
}

internal sealed class RemoteRuntimeProxySession(
    SharpClawInstancePaths instancePaths,
    RemoteRuntimeProxySessionState state,
    X509Certificate2 clientCertificate) : IDisposable
{
    private X509Certificate2? _clientCertificate = clientCertificate;

    public SharpClawInstancePaths InstancePaths { get; } = instancePaths;

    public RemoteRuntimeProxySessionState State { get; } = state;

    public X509Certificate2 DetachClientCertificate()
        => Interlocked.Exchange(ref _clientCertificate, null)
            ?? throw new ObjectDisposedException(nameof(RemoteRuntimeProxySession));

    public void Dispose() => Interlocked.Exchange(ref _clientCertificate, null)?.Dispose();
}
