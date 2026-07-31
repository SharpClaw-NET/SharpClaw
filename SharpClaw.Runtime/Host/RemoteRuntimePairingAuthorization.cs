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
        var options = plan.RequireRemoteProxyOptions();
        var secrets = RemoteRuntimeProxySessionSecrets.Create(
            instancePaths,
            options.PrivateKeySecret,
            options.ClientCertificateSecret);
        var state = await secrets.ReadAsync(cancellationToken);
        if (state is null)
        {
            await RuntimePairingClient.PairAsync(plan, instancePaths, cancellationToken);
            // Reload the protected session after pairing to validate persisted state.
            state = await RemoteRuntimeProxySessionSecrets.Create(
                    instancePaths,
                    options.PrivateKeySecret,
                    options.ClientCertificateSecret)
                .ReadAsync(cancellationToken)
                ?? throw new InvalidOperationException(
                    "RemoteProxy pairing completed without an active approved session.");
        }

        if (state.CertificateNotAfterUtc <= DateTimeOffset.UtcNow)
        {
            using var privateKey = await secrets.LoadPrivateKeyAsync(
                state,
                cancellationToken);
            await RuntimePairingClient.RenewAndReissueAsync(
                plan,
                state,
                privateKey,
                secrets,
                cancellationToken);
            state = await secrets.ReadAsync(cancellationToken)
                ?? throw new InvalidOperationException(
                    "RemoteProxy certificate renewal did not persist an active session.");
        }

        var clientCertificate = await secrets.LoadClientCertificateAsync(state, cancellationToken);
        try
        {
            await RuntimePairingClient.ValidateActiveSessionAsync(
                plan,
                state,
                clientCertificate,
                cancellationToken);
            return new RemoteRuntimeProxySession(instancePaths, state, clientCertificate);
        }
        catch
        {
            clientCertificate.Dispose();
            throw;
        }
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
