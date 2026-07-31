using System.Net.Http.Json;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using SharpClaw.Shared.Instances;
using SharpClaw.Shared.RemoteRuntimeBridge;

namespace SharpClaw.Runtime.Host;

public static class RuntimePairingClient
{
    public static async Task RunAsync(
        RuntimeLaunchPlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);

        if (plan.Mode != RuntimeLaunchMode.PairingClient)
            throw new ArgumentException("The launch plan is not PairingClient mode.", nameof(plan));

        cancellationToken.ThrowIfCancellationRequested();
        var instancePaths = RuntimeInstancePathResolver.CreateBackend();
        await PairAsync(plan, instancePaths, cancellationToken);
    }

    internal static async Task PairAsync(
        RuntimeLaunchPlan plan,
        SharpClawInstancePaths instancePaths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(instancePaths);

        if (plan.Mode is not RuntimeLaunchMode.PairingClient
            and not RuntimeLaunchMode.RemoteProxy)
        {
            throw new ArgumentException(
                "The launch plan does not request remote pairing.",
                nameof(plan));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var options = plan.RequireRemoteProxyOptions();
        if (!Uri.TryCreate(options.GatewayUrl, UriKind.Absolute, out var gatewayBridgeUri)
            || !string.Equals(gatewayBridgeUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "PairingClient mode requires an HTTPS Gateway bridge URL.");
        }

        var invitation = options.CreateInvitation();
        using var httpClient = CreatePinnedClient(
            gatewayBridgeUri,
            invitation.GatewayServerPublicKeyHash,
            options.ConnectTimeoutSeconds,
            options.ActivityTimeoutSeconds);
        var pairingClient = new RemoteRuntimePairingClient(
            httpClient,
            sessionSecretsFactory: paths => RemoteRuntimeProxySessionSecrets.Create(
                paths,
                options.PrivateKeySecret,
                options.ClientCertificateSecret));
        await pairingClient.PairAsync(invitation, instancePaths, cancellationToken);
    }

    internal static async Task ValidateActiveSessionAsync(
        RuntimeLaunchPlan plan,
        RemoteRuntimeProxySessionState state,
        X509Certificate2 clientCertificate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(clientCertificate);

        var options = plan.RequireRemoteProxyOptions();
        if (!string.Equals(
                state.AuthoritativeRuntimeInstanceId,
                options.AuthoritativeRuntimeInstanceId,
                StringComparison.Ordinal)
            || !string.Equals(
                state.ProxyRuntimeInstanceId,
                options.ProxyRuntimeInstanceId,
                StringComparison.Ordinal)
            || !string.Equals(
                state.GatewayServerPublicKeyHash,
                options.GatewayServerPublicKeyHash,
                StringComparison.Ordinal))
        {
            throw new RemoteRuntimePairingException(
                "PairingTargetMismatch",
                "The stored proxy session does not match the configured Runtime target.");
        }

        if (!Uri.TryCreate(state.GatewayBridgeUrl, UriKind.Absolute, out var stateGatewayUri)
            || !Uri.TryCreate(options.GatewayUrl, UriKind.Absolute, out var configuredGatewayUri)
            || !string.Equals(
                stateGatewayUri.GetLeftPart(UriPartial.Authority),
                configuredGatewayUri.GetLeftPart(UriPartial.Authority),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new RemoteRuntimePairingException(
                "PairingTargetMismatch",
                "The stored proxy session does not match the configured Gateway target.");
        }

        using var httpClient = CreatePinnedClient(
            stateGatewayUri,
            state.GatewayServerPublicKeyHash,
            options.ConnectTimeoutSeconds,
            options.ActivityTimeoutSeconds,
            clientCertificate);
        var pairingClient = new RemoteRuntimePairingClient(httpClient);
        await pairingClient.ValidateActiveSessionAsync(
            state,
            options.GatewayInstanceId,
            options.AuthoritativeRuntimeInstanceId,
            options.ProxyRuntimeInstanceId,
            clientCertificate,
            cancellationToken);
    }

    internal static async Task RenewAndReissueAsync(
        RuntimeLaunchPlan plan,
        RemoteRuntimeProxySessionState state,
        ECDsa privateKey,
        RemoteRuntimeProxySessionSecrets secrets,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(privateKey);
        ArgumentNullException.ThrowIfNull(secrets);

        var options = plan.RequireRemoteProxyOptions();
        if (!Uri.TryCreate(state.GatewayBridgeUrl, UriKind.Absolute, out var gatewayUri)
            || !string.Equals(gatewayUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new RemoteRuntimePairingException(
                "PairingTargetMismatch",
                "The stored Gateway bridge URL is invalid.");
        }

        using var httpClient = CreatePinnedClient(
            gatewayUri,
            state.GatewayServerPublicKeyHash,
            options.ConnectTimeoutSeconds,
            options.ActivityTimeoutSeconds);
        var pairingClient = new RemoteRuntimePairingClient(httpClient);
        var publicKeyHash = RemoteRuntimeCertificateHash.Compute(privateKey);
        var renewalExpiry = DateTimeOffset.UtcNow.AddDays(30);
        var renewalPayload = RemoteRuntimePairingProof.CreateRenewalProofPayload(
            state.PairId,
            publicKeyHash,
            renewalExpiry,
            state.BridgeProtocolMajor);
        var renewalSignature = privateKey.SignData(renewalPayload, HashAlgorithmName.SHA256);
        try
        {
            await pairingClient.RenewAsync(
                new RemoteRuntimePairingRenewalRequest(
                    state.PairId,
                    renewalExpiry,
                    Convert.ToBase64String(renewalSignature)),
                cancellationToken);

            var certificatePayload = RemoteRuntimePairingProof.CreateCertificateProofPayload(
                state.PairId,
                publicKeyHash,
                state.BridgeProtocolMajor);
            var certificateSignature = privateKey.SignData(
                certificatePayload,
                HashAlgorithmName.SHA256);
            try
            {
                var certificate = await pairingClient.IssueClientCertificateAsync(
                    new RemoteRuntimePairingCertificateRequest(
                        state.PairId,
                        Convert.ToBase64String(certificateSignature)),
                    cancellationToken);
                using var publicCertificate = X509CertificateLoader.LoadCertificate(
                    Convert.FromBase64String(certificate.CertificateDerBase64));
                if (!string.Equals(
                        RemoteRuntimeCertificateHash.Compute(publicCertificate),
                        publicKeyHash,
                        StringComparison.Ordinal)
                    || !string.Equals(
                        publicCertificate.Thumbprint,
                        certificate.CertificateThumbprint,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new RemoteRuntimePairingException(
                        "CertificateMismatch",
                        "The renewed certificate does not match the stored proxy key.");
                }

                using var certificateWithKey = publicCertificate.CopyWithPrivateKey(privateKey);
                var pfx = certificateWithKey.Export(X509ContentType.Pfx);
                var privateKeyBytes = privateKey.ExportPkcs8PrivateKey();
                try
                {
                    await secrets.SaveAsync(
                        state with
                        {
                            ClientCertificatePfxBase64 = Convert.ToBase64String(pfx),
                            CertificateNotAfterUtc = certificate.NotAfterUtc,
                            CertificateThumbprint = certificate.CertificateThumbprint,
                        },
                        privateKeyBytes,
                        cancellationToken);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(pfx);
                    CryptographicOperations.ZeroMemory(privateKeyBytes);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(certificatePayload);
                CryptographicOperations.ZeroMemory(certificateSignature);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(renewalPayload);
            CryptographicOperations.ZeroMemory(renewalSignature);
        }
    }

    private static HttpClient CreatePinnedClient(
        Uri gatewayBridgeUri,
        string expectedServerPublicKeyHash,
        int connectTimeoutSeconds,
        int activityTimeoutSeconds,
        X509Certificate2? clientCertificate = null)
    {
        if (string.IsNullOrWhiteSpace(expectedServerPublicKeyHash))
        {
            throw new InvalidOperationException(
                "The pairing invitation does not contain a Gateway certificate fingerprint.");
        }

        var sslOptions = new SslClientAuthenticationOptions
        {
            RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                HasPinnedPublicKey(certificate, expectedServerPublicKeyHash),
        };
        if (clientCertificate is not null)
        {
            sslOptions.ClientCertificates = new X509CertificateCollection
            {
                clientCertificate,
            };
        }

        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(connectTimeoutSeconds),
            SslOptions = sslOptions,
        };
        return new HttpClient(handler)
        {
            BaseAddress = gatewayBridgeUri,
            Timeout = TimeSpan.FromSeconds(activityTimeoutSeconds),
        };
    }

    private static bool HasPinnedPublicKey(
        X509Certificate? certificate,
        string expectedHash)
    {
        if (certificate is null)
            return false;

        var serverCertificate = certificate as X509Certificate2;
        var ownsCertificate = serverCertificate is null;
        serverCertificate ??= new X509Certificate2(certificate);
        try
        {
            var actualHash = RemoteRuntimeCertificateHash.Compute(serverCertificate);
            return string.Equals(actualHash, expectedHash, StringComparison.Ordinal);
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        finally
        {
            if (ownsCertificate)
                serverCertificate.Dispose();
        }
    }

    private static void ValidateInvitation(RemoteRuntimePairingInvitation invitation)
    {
        if (invitation.PairId == Guid.Empty
            || string.IsNullOrWhiteSpace(invitation.Secret)
            || string.IsNullOrWhiteSpace(invitation.GatewayInstanceId)
            || string.IsNullOrWhiteSpace(invitation.GatewayServerPublicKeyHash)
            || string.IsNullOrWhiteSpace(invitation.AuthoritativeRuntimeInstanceId)
            || string.IsNullOrWhiteSpace(invitation.AuthoritativeRuntimeInstallFingerprint)
            || invitation.BridgeProtocolMajor != RemoteRuntimeBridgePaths.CurrentProtocolMajor)
        {
            throw new InvalidOperationException("The pairing invitation is incomplete.");
        }

        if (invitation.ExpiresAtUtc <= DateTimeOffset.UtcNow)
            throw new InvalidOperationException("The pairing invitation has expired.");
    }
}

internal sealed class RemoteRuntimePairingClient(
    HttpClient httpClient,
    TimeSpan? approvalTimeout = null,
    TimeSpan? approvalPollInterval = null,
    Func<SharpClawInstancePaths, RemoteRuntimeProxySessionSecrets>? sessionSecretsFactory = null)
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true,
        };

    private readonly TimeSpan _approvalTimeout = approvalTimeout ?? TimeSpan.FromMinutes(5);
    private readonly TimeSpan _approvalPollInterval = approvalPollInterval ?? TimeSpan.FromSeconds(1);
    private readonly Func<SharpClawInstancePaths, RemoteRuntimeProxySessionSecrets> _sessionSecretsFactory =
        sessionSecretsFactory ?? RemoteRuntimeProxySessionSecrets.Create;

    public async Task<RemoteRuntimeProxySessionState> PairAsync(
        RemoteRuntimePairingInvitation invitation,
        SharpClawInstancePaths instancePaths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(invitation);
        ArgumentNullException.ThrowIfNull(instancePaths);
        ValidateTiming(_approvalTimeout, nameof(approvalTimeout));
        ValidateTiming(_approvalPollInterval, nameof(approvalPollInterval));
        ValidateInvitation(invitation);

        var proxyRuntimeInstanceId = instancePaths.Manifest.InstanceId;
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var certificateRequest = new CertificateRequest(
            $"CN=sharpclaw-proxy-{proxyRuntimeInstanceId}",
            key,
            HashAlgorithmName.SHA256);
        certificateRequest.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        certificateRequest.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(
                [new Oid("1.3.6.1.5.5.7.3.2")],
                true));
        var certificateSigningRequest = certificateRequest.CreateSigningRequest();
        var publicKeyHash = RemoteRuntimeCertificateHash.Compute(key);
        var proofPayload = RemoteRuntimePairingProof.CreateClaimProofPayload(
            invitation,
            proxyRuntimeInstanceId,
            publicKeyHash);
        var proofSignature = key.SignData(proofPayload, HashAlgorithmName.SHA256);
        var certificateProofPayload = RemoteRuntimePairingProof.CreateCertificateProofPayload(
            invitation.PairId,
            publicKeyHash,
            invitation.BridgeProtocolMajor);
        var certificateProofSignature = key.SignData(
            certificateProofPayload,
            HashAlgorithmName.SHA256);

        try
        {
            var claim = await PostAsync<RemoteRuntimePairingClaimRequest, RemoteRuntimePairingClaimResponse>(
                RemoteRuntimeBridgePaths.PairingClaim,
                new RemoteRuntimePairingClaimRequest(
                    invitation.PairId,
                    invitation.Secret,
                    proxyRuntimeInstanceId,
                    Convert.ToBase64String(certificateSigningRequest),
                    Convert.ToBase64String(proofSignature)),
                cancellationToken);
            ValidateClaim(claim, invitation, proxyRuntimeInstanceId);

            var certificate = await WaitForCertificateAsync(
                invitation,
                Convert.ToBase64String(certificateProofSignature),
                cancellationToken);
            using var publicCertificate = X509CertificateLoader.LoadCertificate(
                Convert.FromBase64String(certificate.CertificateDerBase64));
            var actualCertificateHash = RemoteRuntimeCertificateHash.Compute(publicCertificate);
            if (!string.Equals(actualCertificateHash, publicKeyHash, StringComparison.Ordinal)
                || !string.Equals(
                    certificate.ProxyRuntimePublicKeyHash,
                    publicKeyHash,
                    StringComparison.Ordinal)
                || !string.Equals(
                    publicCertificate.Thumbprint,
                    certificate.CertificateThumbprint,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new RemoteRuntimePairingException(
                    "CertificateMismatch",
                    "The Gateway returned a certificate for a different proxy key.");
            }

            using var certificateWithKey = publicCertificate.CopyWithPrivateKey(key);
            var pfx = certificateWithKey.Export(X509ContentType.Pfx);
            try
            {
                var state = new RemoteRuntimeProxySessionState(
                    invitation.PairId,
                    httpClient.BaseAddress?.GetLeftPart(UriPartial.Authority)
                        ?? throw new InvalidOperationException("The Gateway bridge URL is missing."),
                    invitation.GatewayServerPublicKeyHash,
                    invitation.AuthoritativeRuntimeInstanceId,
                    proxyRuntimeInstanceId,
                    Convert.ToBase64String(pfx),
                    certificate.NotAfterUtc,
                    invitation.AuthoritativeRuntimeInstallFingerprint,
                    certificate.CertificateThumbprint,
                    invitation.BridgeProtocolMajor);
                var privateKeyBytes = key.ExportPkcs8PrivateKey();
                try
                {
                    await _sessionSecretsFactory(instancePaths)
                        .SaveAsync(state, privateKeyBytes, cancellationToken);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(privateKeyBytes);
                }
                return state;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(pfx);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(certificateSigningRequest);
            CryptographicOperations.ZeroMemory(proofPayload);
            CryptographicOperations.ZeroMemory(proofSignature);
            CryptographicOperations.ZeroMemory(certificateProofPayload);
            CryptographicOperations.ZeroMemory(certificateProofSignature);
        }
    }

    private async Task<RemoteRuntimePairingCertificateResponse> WaitForCertificateAsync(
        RemoteRuntimePairingInvitation invitation,
        string certificateProofSignatureBase64,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + _approvalTimeout;
        while (true)
        {
            try
            {
                return await PostAsync<RemoteRuntimePairingCertificateRequest, RemoteRuntimePairingCertificateResponse>(
                    RemoteRuntimeBridgePaths.PairingCertificate,
                    new RemoteRuntimePairingCertificateRequest(
                        invitation.PairId,
                        certificateProofSignatureBase64),
                    cancellationToken);
            }
            catch (RemoteRuntimePairingException exception)
                when (exception.Code == "InvalidPairState"
                    && DateTimeOffset.UtcNow < deadline
                    && DateTimeOffset.UtcNow < invitation.ExpiresAtUtc)
            {
                await Task.Delay(_approvalPollInterval, cancellationToken);
            }

            if (DateTimeOffset.UtcNow >= invitation.ExpiresAtUtc)
            {
                throw new RemoteRuntimePairingException(
                    "PairingExpired",
                    "The pairing invitation expired before approval.");
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new RemoteRuntimePairingException(
                    "PairingTimeout",
                    "The pairing was not approved before the client timeout.");
            }
        }
    }

    internal Task<RemoteRuntimePairingCertificateResponse> IssueClientCertificateAsync(
        RemoteRuntimePairingCertificateRequest request,
        CancellationToken cancellationToken)
        => PostAsync<RemoteRuntimePairingCertificateRequest, RemoteRuntimePairingCertificateResponse>(
            RemoteRuntimeBridgePaths.PairingCertificate,
            request,
            cancellationToken);

    internal Task<RemoteRuntimePairingRegistrySnapshot> RenewAsync(
        RemoteRuntimePairingRenewalRequest request,
        CancellationToken cancellationToken)
        => PostAsync<RemoteRuntimePairingRenewalRequest, RemoteRuntimePairingRegistrySnapshot>(
            RemoteRuntimeBridgePaths.PairingRenew,
            request,
            cancellationToken);

    internal async Task ValidateActiveSessionAsync(
        RemoteRuntimeProxySessionState state,
        string gatewayInstanceId,
        string authoritativeRuntimeInstanceId,
        string proxyRuntimeInstanceId,
        X509Certificate2 clientCertificate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(clientCertificate);
        RequireText(gatewayInstanceId, nameof(gatewayInstanceId));
        RequireText(authoritativeRuntimeInstanceId, nameof(authoritativeRuntimeInstanceId));
        RequireText(proxyRuntimeInstanceId, nameof(proxyRuntimeInstanceId));

        using var key = clientCertificate.GetECDsaPublicKey()
            ?? throw new RemoteRuntimePairingException(
                "PairNotAuthorized",
                "The proxy certificate does not contain an ECDSA public key.");
        var publicKeyHash = RemoteRuntimeCertificateHash.Compute(key);
        var path = RemoteRuntimeBridgePaths.RegistryActive
            + "?gatewayInstanceId="
            + Uri.EscapeDataString(gatewayInstanceId)
            + "&authoritativeRuntimeInstanceId="
            + Uri.EscapeDataString(authoritativeRuntimeInstanceId)
            + "&proxyRuntimeInstanceId="
            + Uri.EscapeDataString(proxyRuntimeInstanceId)
            + "&certificateIdentity="
            + Uri.EscapeDataString(clientCertificate.Thumbprint ?? string.Empty)
            + "&authoritativeRuntimeInstallFingerprint="
            + Uri.EscapeDataString(state.AuthoritativeRuntimeInstallFingerprint ?? string.Empty);

        using var response = await httpClient.GetAsync(path, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new RemoteRuntimePairingException(
                "PairNotAuthorized",
                $"The authoritative Runtime did not approve the stored proxy session. HTTP {(int)response.StatusCode}.");
        }

        var active = await response.Content.ReadFromJsonAsync<RemoteRuntimePairingRegistrySnapshot>(
            JsonOptions,
            cancellationToken);
        if (active is null
            || active.Status != RemoteRuntimePairStatus.Active
            || active.PairId != state.PairId
            || !string.Equals(
                active.GatewayInstanceId,
                gatewayInstanceId,
                StringComparison.Ordinal)
            || !string.Equals(
                active.AuthoritativeRuntimeInstanceId,
                authoritativeRuntimeInstanceId,
                StringComparison.Ordinal)
            || !string.Equals(
                active.ProxyRuntimeInstanceId,
                proxyRuntimeInstanceId,
                StringComparison.Ordinal)
            || !string.Equals(
                active.ProxyRuntimePublicKeyHash,
                publicKeyHash,
                StringComparison.Ordinal)
            || !string.Equals(
                active.ClientCertificateIdentity,
                clientCertificate.Thumbprint,
                StringComparison.OrdinalIgnoreCase)
            || !string.Equals(
                active.AuthoritativeRuntimeInstallFingerprint,
                state.AuthoritativeRuntimeInstallFingerprint,
                StringComparison.Ordinal)
            || active.BridgeProtocolMajor != RemoteRuntimeBridgePaths.CurrentProtocolMajor
            || active.ClientCertificateIssuedAtUtc > DateTimeOffset.UtcNow
            || active.ClientCertificateExpiresAtUtc <= DateTimeOffset.UtcNow
            || clientCertificate.NotBefore.ToUniversalTime() > DateTimeOffset.UtcNow
            || clientCertificate.NotAfter.ToUniversalTime() <= DateTimeOffset.UtcNow
            || !active.IsActive(DateTimeOffset.UtcNow))
        {
            throw new RemoteRuntimePairingException(
                "PairingTargetMismatch",
                "The authoritative Runtime returned a different or inactive proxy session.");
        }
    }

    private async Task<TResponse> PostAsync<TRequest, TResponse>(
        string path,
        TRequest request,
        CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsJsonAsync(
            path,
            request,
            JsonOptions,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadFromJsonAsync<RemoteRuntimeErrorResponse>(
                JsonOptions,
                cancellationToken);
            throw new RemoteRuntimePairingException(
                error?.Code ?? "PairingRequestFailed",
                error is null
                    ? $"The pairing request failed with HTTP {(int)response.StatusCode}."
                    : $"The pairing request failed with HTTP {(int)response.StatusCode}: {error.Error ?? error.Code}.");
        }

        return await response.Content.ReadFromJsonAsync<TResponse>(
                JsonOptions,
                cancellationToken)
            ?? throw new RemoteRuntimePairingException(
                "PairingResponseInvalid",
                "The pairing response was empty.");
    }

    private static void ValidateClaim(
        RemoteRuntimePairingClaimResponse response,
        RemoteRuntimePairingInvitation invitation,
        string proxyRuntimeInstanceId)
    {
        if (response.PairId != invitation.PairId
            || !string.Equals(
                response.GatewayInstanceId,
                invitation.GatewayInstanceId,
                StringComparison.Ordinal)
            || !string.Equals(
                response.AuthoritativeRuntimeInstanceId,
                invitation.AuthoritativeRuntimeInstanceId,
                StringComparison.Ordinal)
            || !string.Equals(
                response.ProxyRuntimeInstanceId,
                proxyRuntimeInstanceId,
                StringComparison.Ordinal)
            || !string.Equals(
                response.Status,
                RemoteRuntimePairStatus.ClaimPending.ToString(),
                StringComparison.Ordinal))
        {
            throw new RemoteRuntimePairingException(
                "PairingTargetMismatch",
                "The Gateway returned a claim for a different Runtime target.");
        }
    }

    private static void ValidateInvitation(RemoteRuntimePairingInvitation invitation)
    {
        if (invitation.PairId == Guid.Empty
            || string.IsNullOrWhiteSpace(invitation.Secret)
            || string.IsNullOrWhiteSpace(invitation.GatewayInstanceId)
            || string.IsNullOrWhiteSpace(invitation.GatewayServerPublicKeyHash)
            || string.IsNullOrWhiteSpace(invitation.AuthoritativeRuntimeInstanceId)
            || string.IsNullOrWhiteSpace(invitation.AuthoritativeRuntimeInstallFingerprint)
            || invitation.BridgeProtocolMajor != RemoteRuntimeBridgePaths.CurrentProtocolMajor
            || invitation.ExpiresAtUtc <= DateTimeOffset.UtcNow)
        {
            throw new InvalidOperationException("The pairing invitation is invalid or expired.");
        }
    }

    private static void ValidateTiming(TimeSpan value, string parameterName)
    {
        if (value <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(parameterName, "The pairing duration must be positive.");
    }

    private static void RequireText(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("A nonblank value is required.", parameterName);
    }

    private sealed record RemoteRuntimeErrorResponse(string? Code, string? Error);
}
