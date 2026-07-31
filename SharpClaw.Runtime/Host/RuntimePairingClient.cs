using System.Net.Http.Json;
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
        var options = plan.RequireRemoteProxyOptions();
        if (!Uri.TryCreate(options.GatewayUrl, UriKind.Absolute, out var gatewayBridgeUri)
            || !string.Equals(gatewayBridgeUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "PairingClient mode requires an HTTPS Gateway bridge URL.");
        }

        var invitation = options.CreateInvitation();
        var instancePaths = RuntimeInstancePathResolver.CreateBackend();
        using var httpClient = CreatePinnedClient(
            gatewayBridgeUri,
            invitation.GatewayServerPublicKeyHash);
        var pairingClient = new RemoteRuntimePairingClient(httpClient);
        await pairingClient.PairAsync(invitation, instancePaths, cancellationToken);
    }

    private static HttpClient CreatePinnedClient(
        Uri gatewayBridgeUri,
        string expectedServerPublicKeyHash)
    {
        if (string.IsNullOrWhiteSpace(expectedServerPublicKeyHash))
        {
            throw new InvalidOperationException(
                "The pairing invitation does not contain a Gateway certificate fingerprint.");
        }

        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
                HasPinnedPublicKey(certificate, expectedServerPublicKeyHash),
        };
        return new HttpClient(handler)
        {
            BaseAddress = gatewayBridgeUri,
            Timeout = TimeSpan.FromSeconds(30),
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
            || invitation.BridgeProtocolMajor <= 0)
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
    TimeSpan? approvalPollInterval = null)
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true,
        };

    private readonly TimeSpan _approvalTimeout = approvalTimeout ?? TimeSpan.FromMinutes(5);
    private readonly TimeSpan _approvalPollInterval = approvalPollInterval ?? TimeSpan.FromSeconds(1);

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
        var proofPayload = RemoteRuntimePairingStore.CreateClaimProofPayload(
            invitation,
            proxyRuntimeInstanceId,
            publicKeyHash);
        var proofSignature = key.SignData(proofPayload, HashAlgorithmName.SHA256);

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
                    certificate.NotAfterUtc);
                await RemoteRuntimeProxySessionStore.Create(instancePaths)
                    .SaveAsync(state, cancellationToken);
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
        }
    }

    private async Task<RemoteRuntimePairingCertificateResponse> WaitForCertificateAsync(
        RemoteRuntimePairingInvitation invitation,
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
                        invitation.Secret),
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
                $"The pairing request failed with HTTP {(int)response.StatusCode}.");
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
            || invitation.BridgeProtocolMajor <= 0
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

    private sealed record RemoteRuntimeErrorResponse(string? Code, string? Error);
}
