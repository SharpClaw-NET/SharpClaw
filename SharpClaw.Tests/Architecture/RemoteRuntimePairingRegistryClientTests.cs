using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using FluentAssertions;
using SharpClaw.Gateway.RemoteRuntimeBridge;
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
    public async Task Validation_cache_requires_runtime_invalidation_after_revocation()
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
        (await client.RequireActiveCertificateAsync(
            certificate,
            entry.GatewayInstanceId,
            entry.AuthoritativeRuntimeInstanceId,
            CancellationToken.None))
            .PairId.Should().Be(entry.PairId);
        handler.ActiveCalls.Should().Be(1);

        client.Invalidate(entry.PairId);
        var action = () => client.RequireActiveCertificateAsync(
            certificate,
            entry.GatewayInstanceId,
            entry.AuthoritativeRuntimeInstanceId,
            CancellationToken.None);

        await action.Should().ThrowAsync<RemoteRuntimePairingException>()
            .Where(exception => exception.Code == "PairNotAuthorized");
        handler.ActiveCalls.Should().Be(2);
    }

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
                    Active ? entry : null);
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
