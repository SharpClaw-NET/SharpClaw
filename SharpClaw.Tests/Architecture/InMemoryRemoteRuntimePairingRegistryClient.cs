using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using SharpClaw.Gateway.RemoteRuntimeBridge;
using SharpClaw.Shared.RemoteRuntimeBridge;

namespace SharpClaw.Tests.Architecture;

internal sealed class InMemoryRemoteRuntimePairingRegistryClient : IRemoteRuntimePairingRegistryClient
{
    private readonly object _gate = new();
    private RemoteRuntimePairingRegistrySnapshot? _entry;
    private X509Certificate2? _clientCertificate;

    public InMemoryRemoteRuntimePairingRegistryClient(
        RemoteRuntimeBridgeTarget target,
        bool active = false)
    {
        Target = target;
        if (active)
            CreateActivePair();
    }

    public RemoteRuntimeBridgeTarget Target { get; }

    public Guid PairId
        => _entry?.PairId ?? throw new InvalidOperationException("The test registry has no pairing.");

    public X509Certificate2 ClientCertificate
    {
        get
        {
            if (_clientCertificate is null)
                throw new InvalidOperationException("The test registry has no client certificate.");
            return new X509Certificate2(_clientCertificate);
        }
    }

    public Task<RemoteRuntimePairingInvitation> CreateInvitationAsync(
        RemoteRuntimeRegistryInvitationRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var invitation = new RemoteRuntimePairingInvitation(
            Guid.NewGuid(),
            "test-invitation-secret",
            request.GatewayInstanceId,
            request.GatewayServerPublicKeyHash,
            request.AuthoritativeRuntimeInstanceId,
            request.AuthoritativeRuntimeInstallFingerprint,
            1,
            DateTimeOffset.UtcNow.AddSeconds(request.LifetimeSeconds));
        lock (_gate)
        {
            _entry = CreateEntry(
                invitation,
                RemoteRuntimePairStatus.InvitationIssued,
                null,
                null,
                null,
                null);
        }

        return Task.FromResult(invitation);
    }

    public Task<RemoteRuntimePairingRegistrySnapshot> ClaimAsync(
        RemoteRuntimePairingClaimRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.PairId == Guid.Empty
            || string.IsNullOrWhiteSpace(request.Secret)
            || string.IsNullOrWhiteSpace(request.ProxyRuntimeInstanceId)
            || string.IsNullOrWhiteSpace(request.CertificateSigningRequestBase64)
            || string.IsNullOrWhiteSpace(request.ProofSignatureBase64))
        {
            throw new RemoteRuntimePairingException(
                "InvalidClaim",
                "The test pairing claim is incomplete.");
        }

        lock (_gate)
        {
            var current = RequireEntry();
            _entry = current with
            {
                Status = RemoteRuntimePairStatus.ClaimPending,
                ProxyRuntimeInstanceId = request.ProxyRuntimeInstanceId,
            };
            return Task.FromResult(_entry);
        }
    }

    public Task<RemoteRuntimePairingCertificateResponse> IssueClientCertificateAsync(
        RemoteRuntimePairingCertificateRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            CreateActivePair();
            var certificate = _clientCertificate!;
            return Task.FromResult(new RemoteRuntimePairingCertificateResponse(
                Convert.ToBase64String(certificate.Export(X509ContentType.Cert)),
                _entry!.ProxyRuntimePublicKeyHash!,
                certificate.Thumbprint!,
                certificate.NotAfter.ToUniversalTime()));
        }
    }

    public Task<RemoteRuntimePairingRegistrySnapshot> ApproveAsync(
        RemoteRuntimeRegistryApprovalRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var current = RequireEntry();
            _entry = current with
            {
                Status = RemoteRuntimePairStatus.Active,
                ClientCertificateIdentity = request.ClientCertificateIdentity
                    ?? current.ProxyRuntimePublicKeyHash,
                ApprovedAtUtc = DateTimeOffset.UtcNow,
            };
            return Task.FromResult(_entry);
        }
    }

    public Task<RemoteRuntimePairingRegistrySnapshot> RevokeAsync(
        RemoteRuntimeRegistryRevocationRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var current = RequireEntry();
            _entry = current with
            {
                Status = RemoteRuntimePairStatus.Revoked,
                StatusReason = request.Reason,
                RevokedAtUtc = DateTimeOffset.UtcNow,
            };
            return Task.FromResult(_entry);
        }
    }

    public Task<RemoteRuntimePairingRegistrySnapshot?> FindActiveAsync(
        string gatewayInstanceId,
        string authoritativeRuntimeInstanceId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var entry = _entry;
            return Task.FromResult(
                entry is not null
                && entry.IsActive(DateTimeOffset.UtcNow)
                && entry.GatewayInstanceId == gatewayInstanceId
                && entry.AuthoritativeRuntimeInstanceId == authoritativeRuntimeInstanceId
                    ? entry
                    : null);
        }
    }

    public async Task<RemoteRuntimePairingRegistrySnapshot> RequireActiveCertificateAsync(
        X509Certificate2 certificate,
        string gatewayInstanceId,
        string authoritativeRuntimeInstanceId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var key = certificate.GetECDsaPublicKey()
            ?? throw new RemoteRuntimePairingException(
                "PairNotAuthorized",
                "The client certificate has no ECDSA public key.");
        var publicKeyHash = RemoteRuntimeCertificateHash.Compute(key);
        var entry = await FindActiveAsync(
            gatewayInstanceId,
            authoritativeRuntimeInstanceId,
            cancellationToken);
        if (entry is null || entry.ProxyRuntimePublicKeyHash != publicKeyHash)
            throw new RemoteRuntimePairingException(
                "PairNotAuthorized",
                "The client certificate is not active.");
        return entry;
    }

    public void Invalidate(Guid pairId)
    {
    }

    public ValueTask DisposeAsync()
    {
        _clientCertificate?.Dispose();
        _clientCertificate = null;
        return ValueTask.CompletedTask;
    }

    private void CreateActivePair()
    {
        if (_entry?.Status == RemoteRuntimePairStatus.Active && _clientCertificate is not null)
            return;

        var pairId = _entry?.PairId ?? Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=test-proxy", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(
                [new Oid("1.3.6.1.5.5.7.3.2")],
                true));
        using var certificate = request.CreateSelfSigned(
            now.AddMinutes(-1),
            now.AddHours(1));
        _clientCertificate?.Dispose();
        var pfx = certificate.Export(X509ContentType.Pfx);
        try
        {
            _clientCertificate = X509CertificateLoader.LoadPkcs12(
                pfx,
                password: null,
                keyStorageFlags: X509KeyStorageFlags.UserKeySet
                    | X509KeyStorageFlags.PersistKeySet
                    | X509KeyStorageFlags.Exportable);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pfx);
        }
        _entry = new RemoteRuntimePairingRegistrySnapshot(
            Guid.NewGuid(),
            pairId,
            RemoteRuntimePairStatus.Active,
            Target.GatewayInstanceId,
            "gateway-server-hash",
            Target.AuthoritativeRuntimeInstanceId,
            Target.AuthoritativeRuntimeInstallFingerprint,
            1,
            "proxy-1",
            RemoteRuntimeCertificateHash.Compute(key),
            _clientCertificate.Thumbprint,
            null,
            null,
            null,
            now,
            now,
            now,
            null,
            null,
            now.AddHours(1),
            now,
            now,
            1);
    }

    private RemoteRuntimePairingRegistrySnapshot CreateEntry(
        RemoteRuntimePairingInvitation invitation,
        RemoteRuntimePairStatus status,
        string? proxyRuntimeInstanceId,
        string? proxyRuntimePublicKeyHash,
        string? clientCertificateIdentity,
        string? statusReason)
        => new(
            Guid.NewGuid(),
            invitation.PairId,
            status,
            invitation.GatewayInstanceId,
            invitation.GatewayServerPublicKeyHash,
            invitation.AuthoritativeRuntimeInstanceId,
            invitation.AuthoritativeRuntimeInstallFingerprint,
            invitation.BridgeProtocolMajor,
            proxyRuntimeInstanceId,
            proxyRuntimePublicKeyHash,
            clientCertificateIdentity,
            null,
            null,
            statusReason,
            DateTimeOffset.UtcNow,
            null,
            null,
            null,
            null,
            invitation.ExpiresAtUtc,
            null,
            DateTimeOffset.UtcNow,
            1);

    private RemoteRuntimePairingRegistrySnapshot RequireEntry()
        => _entry ?? throw new RemoteRuntimePairingException("PairNotFound", "The test pairing was not created.");
}
