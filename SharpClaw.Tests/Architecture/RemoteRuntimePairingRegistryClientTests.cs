using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using FluentAssertions;
using SharpClaw.Gateway.RemoteRuntimeBridge;
using SharpClaw.Runtime.Host;
using SharpClaw.Shared.RemoteRuntimeBridge;

namespace SharpClaw.Tests.Architecture;

[TestFixture]
public sealed class RemoteRuntimePairingRegistryClientTests
{
    [Test]
    public async Task Client_delegates_private_registry_crud_and_keeps_authority_headers()
    {
        var entry = CreateEntry();
        var handler = new RegistryHandler(entry);
        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://runtime.test:48923"),
        };
        httpClient.DefaultRequestHeaders.Add("X-Api-Key", "runtime-api-key");
        httpClient.DefaultRequestHeaders.Add("X-Gateway-Token", "gateway-token");
        await using var client = new RemoteRuntimePairingRegistryClient(httpClient);

        var page = await client.ListAsync(
            entry.GatewayInstanceId,
            entry.AuthoritativeRuntimeInstanceId,
            null,
            RemoteRuntimePairStatus.Active,
            "primary",
            25,
            null,
            CancellationToken.None);
        page.Items.Should().ContainSingle(item => item.PairId == entry.PairId);

        (await client.FindAsync(entry.PairId, CancellationToken.None))
            .Should().BeEquivalentTo(entry);
        (await client.UpdateAsync(
            entry.PairId,
            new RemoteRuntimeRegistryDetailsRequest("updated", "updated description"),
            CancellationToken.None))
            .Should().NotBeNull();
        (await client.RenewAsync(
            entry.PairId,
            new RemoteRuntimeRegistryRenewalRequest(DateTimeOffset.UtcNow.AddHours(2)),
            CancellationToken.None))
            .Should().NotBeNull();
        (await client.RejectAsync(entry.PairId, "operator request", CancellationToken.None))
            .Should().NotBeNull();
        await client.DeleteAsync(entry.PairId, CancellationToken.None);

        handler.Requests.Should().Contain(request =>
            request.Method == HttpMethod.Get
            && request.Uri.AbsolutePath == RemoteRuntimeBridgePaths.RegistryPairings);
        handler.Requests.Should().Contain(request =>
            request.Method == HttpMethod.Put
            && request.Uri.AbsolutePath.EndsWith(entry.PairId.ToString("D"), StringComparison.Ordinal));
        handler.Requests.Should().Contain(request =>
            request.Method == HttpMethod.Delete
            && request.Uri.AbsolutePath.EndsWith(entry.PairId.ToString("D"), StringComparison.Ordinal));
        handler.RequestHeaders.Should().OnlyContain(headers =>
            headers.ApiKey == "runtime-api-key"
            && headers.GatewayToken == "gateway-token");
    }

    [Test]
    public async Task Validation_reads_current_registry_state_after_revocation()
    {
        using var certificate = CreateClientCertificate();
        var entry = CreateEntry(certificate);
        var handler = new RegistryHandler(entry);
        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://runtime.test:48923"),
        };
        await using var client = new RemoteRuntimePairingRegistryClient(httpClient);

        (await client.RequireActiveCertificateAsync(
            certificate,
            entry.GatewayInstanceId,
            entry.AuthoritativeRuntimeInstanceId,
            CancellationToken.None))
            .PairId.Should().Be(entry.PairId);

        handler.Active = false;
        var action = () => client.RequireActiveCertificateAsync(
            certificate,
            entry.GatewayInstanceId,
            entry.AuthoritativeRuntimeInstanceId,
            CancellationToken.None);

        await action.Should().ThrowAsync<RemoteRuntimePairingException>()
            .Where(exception => exception.Code == "PairNotAuthorized");
        handler.ActiveCalls.Should().Be(2);
    }

    [Test]
    public async Task Stored_session_validation_accepts_current_active_target()
    {
        using var certificate = CreateClientCertificate();
        var entry = CreateEntry(certificate);
        var state = CreateState(entry);
        var handler = new RegistryHandler(entry);
        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://runtime.test:48923"),
        };
        var client = new RemoteRuntimePairingClient(httpClient);

        await client.ValidateActiveSessionAsync(
            state,
            entry.GatewayInstanceId,
            entry.AuthoritativeRuntimeInstanceId,
            entry.ProxyRuntimeInstanceId!,
            certificate,
            CancellationToken.None);

        handler.ActiveCalls.Should().Be(1);
        handler.Requests.Should().ContainSingle(request =>
            request.Method == HttpMethod.Get
            && request.Uri.AbsolutePath == RemoteRuntimeBridgePaths.RegistryActive
            && request.Uri.Query.Contains(
                "gatewayInstanceId=gateway-1",
                StringComparison.Ordinal));
    }

    [Test]
    public async Task Stored_session_validation_rejects_non_active_registry_states()
    {
        using var certificate = CreateClientCertificate();
        var entry = CreateEntry(certificate);
        var state = CreateState(entry);
        foreach (var status in new[]
                 {
                     RemoteRuntimePairStatus.ClaimPending,
                     RemoteRuntimePairStatus.Rejected,
                     RemoteRuntimePairStatus.Revoked,
                 })
        {
            var handler = new RegistryHandler(entry with { Status = status });
            using var httpClient = new HttpClient(handler)
            {
                BaseAddress = new Uri("https://runtime.test:48923"),
            };
            var client = new RemoteRuntimePairingClient(httpClient);

            var action = () => client.ValidateActiveSessionAsync(
                state,
                entry.GatewayInstanceId,
                entry.AuthoritativeRuntimeInstanceId,
                entry.ProxyRuntimeInstanceId!,
                certificate,
                CancellationToken.None);

            await action.Should().ThrowAsync<RemoteRuntimePairingException>();
        }

        var expiredHandler = new RegistryHandler(
            entry with { ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1) });
        using var expiredHttpClient = new HttpClient(expiredHandler)
        {
            BaseAddress = new Uri("https://runtime.test:48923"),
        };
        var expiredClient = new RemoteRuntimePairingClient(expiredHttpClient);
        var expiredAction = () => expiredClient.ValidateActiveSessionAsync(
            state,
            entry.GatewayInstanceId,
            entry.AuthoritativeRuntimeInstanceId,
            entry.ProxyRuntimeInstanceId!,
            certificate,
            CancellationToken.None);

        await expiredAction.Should().ThrowAsync<RemoteRuntimePairingException>();
    }

    private static RemoteRuntimeProxySessionState CreateState(
        RemoteRuntimePairingRegistrySnapshot entry)
        => new(
            entry.PairId,
            "https://runtime.test:48923",
            entry.GatewayServerPublicKeyHash,
            entry.AuthoritativeRuntimeInstanceId,
            entry.ProxyRuntimeInstanceId!,
            "certificate-payload",
            DateTimeOffset.UtcNow.AddHours(1));

    private static RemoteRuntimePairingRegistrySnapshot CreateEntry(
        X509Certificate2? certificate = null)
    {
        using var ownedCertificate = certificate is null ? CreateClientCertificate() : null;
        var selectedCertificate = certificate ?? ownedCertificate!;
        return new RemoteRuntimePairingRegistrySnapshot(
            Guid.NewGuid(),
            Guid.NewGuid(),
            RemoteRuntimePairStatus.Active,
            "gateway-1",
            "gateway-server-hash",
            "runtime-1",
            "runtime-install-1",
            1,
            "proxy-1",
            RemoteRuntimeCertificateHash.Compute(selectedCertificate),
            selectedCertificate.Thumbprint,
            "primary",
            "registry test",
            null,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            null,
            null,
            DateTimeOffset.UtcNow.AddHours(1),
            null,
            DateTimeOffset.UtcNow,
            1);
    }

    private static X509Certificate2 CreateClientCertificate()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=registry-client-test", key, HashAlgorithmName.SHA256);
        return request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddHours(1));
    }

    private sealed class RegistryHandler(RemoteRuntimePairingRegistrySnapshot entry)
        : HttpMessageHandler
    {
        public RemoteRuntimePairingRegistrySnapshot ActiveEntry { get; set; } = entry;
        public List<RequestRecord> Requests { get; } = [];
        public List<HeaderRecord> RequestHeaders { get; } = [];
        public bool Active { get; set; } = true;
        public int ActiveCalls { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(new(request.Method, request.RequestUri!));
            request.Headers.TryGetValues("X-Api-Key", out var apiKeys);
            request.Headers.TryGetValues("X-Gateway-Token", out var gatewayTokens);
            RequestHeaders.Add(new(
                apiKeys?.SingleOrDefault(),
                gatewayTokens?.SingleOrDefault()));

            if (request.Method == HttpMethod.Get
                && request.RequestUri!.AbsolutePath == RemoteRuntimeBridgePaths.RegistryActive)
            {
                ActiveCalls++;
                return JsonResponse(
                    Active ? ActiveEntry : null);
            }

            if (request.Method == HttpMethod.Delete)
                return new HttpResponseMessage(HttpStatusCode.NoContent);

            if (request.Method == HttpMethod.Get
                && request.RequestUri!.AbsolutePath == RemoteRuntimeBridgePaths.RegistryPairings)
            {
                return JsonResponse(new RemoteRuntimeRegistryPageResponse([entry], false, null));
            }

            if (request.Method == HttpMethod.Get)
                return JsonResponse(entry);

            if (request.Method == HttpMethod.Put)
                return JsonResponse(entry with { DisplayName = "updated" });

            return JsonResponse(entry with { StatusReason = "operator request" });
        }

        private static HttpResponseMessage JsonResponse<T>(T value)
            => new(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(value),
            };
    }

    private sealed record RequestRecord(HttpMethod Method, Uri Uri);

    private sealed record HeaderRecord(string? ApiKey, string? GatewayToken);
}
