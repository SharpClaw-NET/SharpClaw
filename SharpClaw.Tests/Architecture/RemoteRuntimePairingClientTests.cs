using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using FluentAssertions;
using NUnit.Framework;
using SharpClaw.Runtime.Host;
using SharpClaw.Shared.Instances;
using SharpClaw.Shared.RemoteRuntimeBridge;

namespace SharpClaw.Tests.Architecture;

[TestFixture]
[NonParallelizable]
public sealed class RemoteRuntimePairingClientTests
{
    [Test]
    public async Task Pairing_client_claims_waits_for_approval_and_persists_protected_session()
    {
        var root = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "pairing-client-" + Guid.NewGuid().ToString("N"));
        var paths = new SharpClawInstancePaths(
            SharpClawInstanceKind.Backend,
            Path.Combine(root, "backend"),
            Path.Combine(root, "shared"));
        var environmentDirectory = Path.Combine(root, "Environment");
        Directory.CreateDirectory(environmentDirectory);
        File.WriteAllText(
            Path.Combine(environmentDirectory, ".env.template"),
            "Test__Unrelated=\"keep\"\n");
        File.WriteAllText(
            Path.Combine(environmentDirectory, ".dev.env.template"),
            "Test__Unrelated=\"keep\"\n");
        var invitation = new RemoteRuntimePairingInvitation(
            Guid.NewGuid(),
            "one-time-secret",
            "gateway-1",
            "gateway-public-key-hash",
            "runtime-authority-1",
            "install-fingerprint-1",
            1,
            DateTimeOffset.UtcNow.AddMinutes(5));
        var handler = new PairingClientHandler(invitation);
        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://gateway.test:48925"),
        };
        var client = new RemoteRuntimePairingClient(
            httpClient,
            TimeSpan.FromSeconds(2),
            TimeSpan.FromMilliseconds(1),
            instancePaths => RemoteRuntimeProxySessionSecrets.Create(environmentDirectory, instancePaths));

        try
        {
            var state = await client.PairAsync(invitation, paths);

            state.PairId.Should().Be(invitation.PairId);
            state.GatewayBridgeUrl.Should().Be("https://gateway.test:48925");
            state.AuthoritativeRuntimeInstanceId.Should().Be(invitation.AuthoritativeRuntimeInstanceId);
            state.ProxyRuntimeInstanceId.Should().NotBeNullOrWhiteSpace();
            state.ClientCertificatePfxBase64.Should().NotBeNullOrWhiteSpace();
            handler.Claim.Should().NotBeNull();
            handler.CertificateRequests.Should().BeGreaterThanOrEqualTo(2);
            handler.Claim!.Secret.Should().Be(invitation.Secret);
            handler.Claim.ProofSignatureBase64.Should().NotBeNullOrWhiteSpace();

            var store = RemoteRuntimeProxySessionSecrets.Create(environmentDirectory, paths);
            var restored = await store.ReadAsync();
            restored.Should().BeEquivalentTo(state);
            using var certificate = await store.LoadClientCertificateAsync(restored!);
            certificate.HasPrivateKey.Should().BeTrue();
            certificate.Subject.Should().StartWith("CN=sharpclaw-proxy-");
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task Pairing_client_rejects_expired_invitation_before_network_use()
    {
        var root = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "pairing-client-expired-" + Guid.NewGuid().ToString("N"));
        var paths = new SharpClawInstancePaths(
            SharpClawInstanceKind.Backend,
            Path.Combine(root, "backend"),
            Path.Combine(root, "shared"));
        var invitation = new RemoteRuntimePairingInvitation(
            Guid.NewGuid(),
            "one-time-secret",
            "gateway-1",
            "gateway-public-key-hash",
            "runtime-authority-1",
            "install-fingerprint-1",
            1,
            DateTimeOffset.UtcNow.AddMinutes(-1));
        using var httpClient = new HttpClient(new ThrowingHandler());
        var client = new RemoteRuntimePairingClient(httpClient);

        try
        {
            var action = () => client.PairAsync(invitation, paths);

            await action.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*invalid or expired*");
            Directory.Exists(paths.SecretsDirectory).Should().BeFalse();
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private sealed class PairingClientHandler(RemoteRuntimePairingInvitation invitation)
        : HttpMessageHandler
    {
        private byte[]? _certificateDer;
        private int _certificateRequests;

        public RemoteRuntimePairingClaimRequest? Claim { get; private set; }

        public int CertificateRequests => _certificateRequests;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            if (request.RequestUri!.AbsolutePath == RemoteRuntimeBridgePaths.PairingClaim)
            {
                Claim = JsonSerializer.Deserialize<RemoteRuntimePairingClaimRequest>(
                    body,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)
                    {
                        PropertyNameCaseInsensitive = true,
                    });
                _certificateDer = CreateCertificateDer(Claim!.CertificateSigningRequestBase64);
                var claim = new RemoteRuntimePairingClaimResponse(
                    invitation.PairId,
                    RemoteRuntimePairStatus.ClaimPending.ToString(),
                    invitation.GatewayInstanceId,
                    invitation.AuthoritativeRuntimeInstanceId,
                    Claim!.ProxyRuntimeInstanceId);
                return JsonResponse(claim);
            }

            Interlocked.Increment(ref _certificateRequests);
            if (_certificateRequests == 1)
            {
                return JsonResponse(
                    new { code = "InvalidPairState", error = "Approval is pending." },
                    HttpStatusCode.BadRequest);
            }

            var certificate = X509CertificateLoader.LoadCertificate(_certificateDer!);
            using (certificate)
            {
                var result = new RemoteRuntimePairingCertificateResponse(
                    Convert.ToBase64String(_certificateDer!),
                    RemoteRuntimeCertificateHash.Compute(certificate),
                    certificate.Thumbprint!,
                    certificate.NotAfter.ToUniversalTime());
                return JsonResponse(result);
            }
        }

        private static byte[] CreateCertificateDer(string certificateSigningRequestBase64)
        {
            var now = DateTimeOffset.UtcNow;
            var notBefore = now.AddMinutes(-1);
            var request = CertificateRequest.LoadSigningRequest(
                Convert.FromBase64String(certificateSigningRequestBase64),
                HashAlgorithmName.SHA256);
            using var authorityKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var authorityRequest = new CertificateRequest(
                "CN=pairing-test-authority",
                authorityKey,
                HashAlgorithmName.SHA256);
            authorityRequest.CertificateExtensions.Add(
                new X509BasicConstraintsExtension(true, false, 0, true));
            using var authority = authorityRequest.CreateSelfSigned(
                notBefore,
                now.AddMinutes(30));
            var leafNotAfter = authority.NotAfter.ToUniversalTime().AddSeconds(-1);
            using var certificate = request.Create(
                authority,
                notBefore,
                leafNotAfter,
                RandomNumberGenerator.GetBytes(16));
            return certificate.Export(X509ContentType.Cert);
        }

        private static HttpResponseMessage JsonResponse<T>(
            T value,
            HttpStatusCode statusCode = HttpStatusCode.OK)
            => new(statusCode)
            {
                Content = JsonContent.Create(value),
            };
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => throw new AssertionException("The expired invitation must stop before network use.");
    }
}
