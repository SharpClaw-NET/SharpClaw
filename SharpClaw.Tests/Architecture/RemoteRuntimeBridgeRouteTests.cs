using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using SharpClaw.Gateway.Configuration;
using SharpClaw.Gateway.RemoteRuntimeBridge;
using SharpClaw.Shared.RemoteRuntimeBridge;

namespace SharpClaw.Tests.Architecture;

[TestFixture]
[NonParallelizable]
public sealed class RemoteRuntimeBridgeRouteTests
{
    [Test]
    public async Task Private_enrollment_routes_require_admin_key_and_keep_normal_routes_protected()
    {
        var root = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "bridge-routes-" + Guid.NewGuid().ToString("N"));
        var certificatePath = Path.Combine(root, "bridge.pfx");
        using var certificate = CreateServerCertificate();
        Directory.CreateDirectory(root);
        File.WriteAllBytes(certificatePath, certificate.Export(X509ContentType.Pfx));

        var options = new RemoteRuntimeBridgeOptions
        {
            Enabled = true,
            ListenUrl = $"https://127.0.0.1:{GetFreePort()}",
            ServerCertificatePath = certificatePath,
            AdministrationKey = "bridge-admin-key",
        };
        var target = new RemoteRuntimeBridgeTarget(
            "gateway-1",
            "runtime-1",
            "runtime-install-1",
            "https://127.0.0.1:1",
            "authoritative-api-key",
            "authoritative-gateway-token");
        await using var registryClient = new InMemoryRemoteRuntimePairingRegistryClient(target);
        await using var app = await RemoteRuntimeBridgeHost.BuildAsync(
            [],
            options,
            registryClient,
            target);

        try
        {
            await app.StartAsync();
            var address = app.Services
                .GetRequiredService<IServer>()
                .Features
                .Get<IServerAddressesFeature>()!
                .Addresses
                .Single();
            var port = new Uri(address).Port;
            using var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (_, presented, _, _) =>
                    HasPinnedPublicKey(presented, RemoteRuntimeCertificateHash.Compute(certificate)),
            };
            using var client = new HttpClient(handler)
            {
                BaseAddress = new Uri($"https://127.0.0.1:{port}"),
            };

            using var unauthorized = await client.PostAsJsonAsync(
                RemoteRuntimeBridgePaths.AdminInvitation,
                new RemoteRuntimePairingAdminInvitationRequest(60));
            unauthorized.StatusCode.Should().Be(HttpStatusCode.Forbidden);

            using var forwarded = new HttpRequestMessage(
                HttpMethod.Post,
                RemoteRuntimeBridgePaths.AdminInvitation)
            {
                Content = JsonContent.Create(new RemoteRuntimePairingAdminInvitationRequest(60)),
            };
            forwarded.Headers.Add(RemoteRuntimeBridgePaths.AdministrationKeyHeader, "bridge-admin-key");
            forwarded.Headers.Add("X-Forwarded-For", "127.0.0.1");
            using var forwardedResponse = await client.SendAsync(forwarded);
            forwardedResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);

            using var authorized = new HttpRequestMessage(
                HttpMethod.Post,
                RemoteRuntimeBridgePaths.AdminInvitation)
            {
                Content = JsonContent.Create(new RemoteRuntimePairingAdminInvitationRequest(60)),
            };
            authorized.Headers.Add(RemoteRuntimeBridgePaths.AdministrationKeyHeader, "bridge-admin-key");
            using var authorizedResponse = await client.SendAsync(authorized);
            authorizedResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            var invitation = await authorizedResponse.Content.ReadFromJsonAsync<RemoteRuntimePairingInvitation>();
            invitation.Should().NotBeNull();
            invitation!.GatewayInstanceId.Should().Be(target.GatewayInstanceId);
            invitation.AuthoritativeRuntimeInstanceId.Should().Be(target.AuthoritativeRuntimeInstanceId);
            invitation.AuthoritativeRuntimeInstallFingerprint.Should()
                .Be(target.AuthoritativeRuntimeInstallFingerprint);

            var pairId = registryClient.PairId;
            var adminPairingPath = RemoteRuntimeBridgePaths.AdminPairing
                .Replace("{pairId:guid}", pairId.ToString(), StringComparison.Ordinal);

            using var listRequest = new HttpRequestMessage(
                HttpMethod.Get,
                RemoteRuntimeBridgePaths.AdminPairings + "?take=10");
            listRequest.Headers.Add(
                RemoteRuntimeBridgePaths.AdministrationKeyHeader,
                "bridge-admin-key");
            using var listResponse = await client.SendAsync(listRequest);
            listResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            var page = await listResponse.Content
                .ReadFromJsonAsync<RemoteRuntimeRegistryPageResponse>();
            page.Should().NotBeNull();
            page!.Items.Should().ContainSingle(item => item.PairId == pairId);

            using var getRequest = new HttpRequestMessage(HttpMethod.Get, adminPairingPath);
            getRequest.Headers.Add(
                RemoteRuntimeBridgePaths.AdministrationKeyHeader,
                "bridge-admin-key");
            using var getResponse = await client.SendAsync(getRequest);
            getResponse.StatusCode.Should().Be(HttpStatusCode.OK);

            using var updateRequest = new HttpRequestMessage(HttpMethod.Put, adminPairingPath)
            {
                Content = JsonContent.Create(
                    new RemoteRuntimeRegistryDetailsRequest("test-pair", "route test")),
            };
            updateRequest.Headers.Add(
                RemoteRuntimeBridgePaths.AdministrationKeyHeader,
                "bridge-admin-key");
            using var updateResponse = await client.SendAsync(updateRequest);
            updateResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            var updated = await updateResponse.Content
                .ReadFromJsonAsync<RemoteRuntimePairingRegistrySnapshot>();
            updated.Should().NotBeNull();
            updated!.DisplayName.Should().Be("test-pair");

            using var renewRequest = new HttpRequestMessage(
                HttpMethod.Post,
                adminPairingPath + "/renew")
            {
                Content = JsonContent.Create(
                    new RemoteRuntimeRegistryRenewalRequest(DateTimeOffset.UtcNow.AddHours(2))),
            };
            renewRequest.Headers.Add(
                RemoteRuntimeBridgePaths.AdministrationKeyHeader,
                "bridge-admin-key");
            using var renewResponse = await client.SendAsync(renewRequest);
            renewResponse.StatusCode.Should().Be(HttpStatusCode.OK);

            using var rejectRequest = new HttpRequestMessage(
                HttpMethod.Post,
                adminPairingPath + "/reject")
            {
                Content = JsonContent.Create(new RemoteRuntimeRegistryReasonRequest("test reject")),
            };
            rejectRequest.Headers.Add(
                RemoteRuntimeBridgePaths.AdministrationKeyHeader,
                "bridge-admin-key");
            using var rejectResponse = await client.SendAsync(rejectRequest);
            rejectResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            var rejected = await rejectResponse.Content
                .ReadFromJsonAsync<RemoteRuntimePairingRegistrySnapshot>();
            rejected.Should().NotBeNull();
            rejected!.Status.Should().Be(RemoteRuntimePairStatus.Rejected);

            using var deleteRequest = new HttpRequestMessage(HttpMethod.Delete, adminPairingPath);
            deleteRequest.Headers.Add(
                RemoteRuntimeBridgePaths.AdministrationKeyHeader,
                "bridge-admin-key");
            using var deleteResponse = await client.SendAsync(deleteRequest);
            deleteResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

            using var missingRequest = new HttpRequestMessage(HttpMethod.Get, adminPairingPath);
            missingRequest.Headers.Add(
                RemoteRuntimeBridgePaths.AdministrationKeyHeader,
                "bridge-admin-key");
            using var missingResponse = await client.SendAsync(missingRequest);
            missingResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);

            using var malformedClaim = await client.PostAsJsonAsync(
                RemoteRuntimeBridgePaths.PairingClaim,
                new RemoteRuntimePairingClaimRequest(
                    Guid.Empty,
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    string.Empty));
            malformedClaim.StatusCode.Should().Be(HttpStatusCode.BadRequest);

            using var missingRenewalProof = await client.PostAsJsonAsync(
                RemoteRuntimeBridgePaths.PairingRenew,
                new RemoteRuntimePairingRenewalRequest(
                    pairId,
                    DateTimeOffset.UtcNow.AddHours(2),
                    string.Empty));
            missingRenewalProof.StatusCode.Should().Be(HttpStatusCode.BadRequest);

            using var normalRequest = await client.GetAsync("/api/health");
            normalRequest.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }
        finally
        {
            await app.StopAsync();
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static X509Certificate2 CreateServerCertificate()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=bridge-route-test", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(false, false, 0, false));
        return request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddMinutes(5));
    }

    private static bool HasPinnedPublicKey(
        X509Certificate? certificate,
        string expectedHash)
    {
        if (certificate is null)
            return false;

        var presented = certificate as X509Certificate2;
        var ownsCertificate = presented is null;
        presented ??= new X509Certificate2(certificate);
        try
        {
            return string.Equals(
                RemoteRuntimeCertificateHash.Compute(presented),
                expectedHash,
                StringComparison.Ordinal);
        }
        finally
        {
            if (ownsCertificate)
                presented.Dispose();
        }
    }
}
