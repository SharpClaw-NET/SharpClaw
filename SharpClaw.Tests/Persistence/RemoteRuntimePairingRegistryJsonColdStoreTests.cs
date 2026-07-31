using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using FluentAssertions;
using JSONColdStore;
using Microsoft.EntityFrameworkCore;
using SharpClaw.Contracts.Persistence;
using SharpClaw.Runtime.INF.Persistence;
using SharpClaw.Shared.RemoteRuntimeBridge;
using SharpClaw.Shared.Security;

namespace SharpClaw.Tests.Persistence;

public sealed class RemoteRuntimePairingRegistryJsonColdStoreTests
{
    [Test]
    public async Task Registry_PersistsEncryptedSecretAndSupportsLifecycleFilteringPaginationAndDeletion()
    {
        using var workspace = Workspace.Create();
        await using var db = workspace.CreateDbContext();
        await db.Database.EnsureCreatedAsync();
        var registry = workspace.CreateRegistry(db);

        var invitation = await registry.CreateInvitationAsync(
            "gateway-a",
            "gateway-key-hash",
            "runtime-a",
            "runtime-fingerprint",
            TimeSpan.FromMinutes(5),
            displayName: "Primary proxy",
            description: "JSONColdStore test",
            certificateAuthorityPfx: [1, 2, 3, 4]);

        var row = await db.RemoteRuntimePairings.AsNoTracking().SingleAsync();
        row.EncryptedCertificateAuthorityPfx.Should().NotBeNullOrWhiteSpace();
        row.EncryptedCertificateAuthorityPfx.Should().NotContain(Convert.ToBase64String([1, 2, 3, 4]));
        (await registry.GetCertificateAuthorityPfxAsync(invitation.PairId)).Should().Equal([1, 2, 3, 4]);

        var pending = await registry.ClaimAsync(CreateClaim(invitation, "proxy-a"));
        pending.Status.Should().Be(RemoteRuntimePairStatus.ClaimPending);

        var active = await registry.ApproveAsync(
            invitation.PairId,
            "proxy-a",
            "runtime-a",
            "certificate-thumbprint");
        active.Status.Should().Be(RemoteRuntimePairStatus.Active);
        (await registry.FindActiveTargetAsync("gateway-a", "runtime-a"))?.PairId.Should().Be(invitation.PairId);

        var firstPage = await registry.ListAsync(
            new RemoteRuntimePairingRegistryFilter(
                GatewayInstanceId: "gateway-a",
                Status: RemoteRuntimePairStatus.Active),
            take: 1);
        firstPage.Items.Should().ContainSingle(item => item.PairId == invitation.PairId);
        firstPage.HasMore.Should().BeFalse();

        var renewed = await registry.RenewAsync(invitation.PairId, DateTimeOffset.UtcNow.AddHours(1));
        renewed.ExpiresAtUtc.Should().BeAfter(active.ExpiresAtUtc);

        var touched = await registry.TouchLastSeenAsync(invitation.PairId);
        touched.LastSeenAtUtc.Should().NotBeNull();

        var revoked = await registry.RevokeAsync(invitation.PairId, "operator request");
        revoked.Status.Should().Be(RemoteRuntimePairStatus.Revoked);
        (await registry.FindActiveTargetAsync("gateway-a", "runtime-a")).Should().BeNull();

        await registry.DeleteAsync(invitation.PairId);
        (await registry.FindAsync(invitation.PairId)).Should().BeNull();
    }

    [Test]
    public async Task Registry_RejectsInvalidSecretAndUnsupportedProvider()
    {
        using var workspace = Workspace.Create();
        await using var db = workspace.CreateDbContext();
        await db.Database.EnsureCreatedAsync();
        var registry = workspace.CreateRegistry(db);
        var invitation = await registry.CreateInvitationAsync(
            "gateway-a",
            "gateway-key-hash",
            "runtime-a",
            "runtime-fingerprint",
            TimeSpan.FromMinutes(5));

        var wrongClaim = CreateClaim(invitation, "proxy-a") with { InvitationSecret = "wrong-secret" };
        var act = () => registry.ClaimAsync(wrongClaim);
        (await act.Should().ThrowAsync<RemoteRuntimePairingRegistryException>())
            .Which.Code.Should().Be("InvalidInvitation");

        var unsupported = new RemoteRuntimePairingRegistry(
            db,
            new DatabaseProviderOptions { Provider = StorageMode.SQLite },
            workspace.EncryptionOptions);
        var unsupportedAct = () => unsupported.FindAsync(invitation.PairId);
        (await unsupportedAct.Should().ThrowAsync<RemoteRuntimePairingRegistryException>())
            .Which.Code.Should().Be("PairingRegistryProviderUnsupported");
    }

    [Test]
    public async Task Registry_derives_public_key_hash_when_http_claim_omits_it()
    {
        using var workspace = Workspace.Create();
        await using var db = workspace.CreateDbContext();
        await db.Database.EnsureCreatedAsync();
        var registry = workspace.CreateRegistry(db);
        var invitation = await registry.CreateInvitationAsync(
            "gateway-a",
            "gateway-key-hash",
            "runtime-a",
            "runtime-fingerprint",
            TimeSpan.FromMinutes(5));

        var pending = await registry.ClaimAsync(
            CreateClaim(invitation, "proxy-a") with
            {
                ProxyRuntimePublicKeyHash = null,
            });

        pending.Status.Should().Be(RemoteRuntimePairStatus.ClaimPending);
        pending.ProxyRuntimePublicKeyHash.Should().NotBeNullOrWhiteSpace();
    }

    [Test]
    public async Task Registry_UsesStableCursorAndRejectsSecondActiveTarget()
    {
        using var workspace = Workspace.Create();
        await using var db = workspace.CreateDbContext();
        await db.Database.EnsureCreatedAsync();
        var registry = workspace.CreateRegistry(db);

        var invitations = new List<RemoteRuntimePairingInvitation>();
        for (var i = 0; i < 3; i++)
        {
            invitations.Add(await registry.CreateInvitationAsync(
                "gateway-a",
                "gateway-key-hash",
                "runtime-a",
                "runtime-fingerprint",
                TimeSpan.FromMinutes(5),
                displayName: $"proxy-{i}"));
        }

        var first = await registry.ListAsync(new RemoteRuntimePairingRegistryFilter(), take: 2);
        first.Items.Should().HaveCount(2);
        first.HasMore.Should().BeTrue();
        first.Next.Should().NotBeNull();

        var second = await registry.ListAsync(
            new RemoteRuntimePairingRegistryFilter(),
            take: 2,
            cursor: first.Next);
        second.Items.Should().ContainSingle();
        second.HasMore.Should().BeFalse();
        first.Items.Select(item => item.PairId)
            .Intersect(second.Items.Select(item => item.PairId))
            .Should().BeEmpty();

        foreach (var invitation in invitations)
        {
            await registry.ClaimAsync(CreateClaim(invitation, "proxy-a"));
        }

        await registry.ApproveAsync(
            invitations[0].PairId,
            "proxy-a",
            "runtime-a",
            "certificate-0");
        var issued = await registry.IssueClientCertificateAsync(
            invitations[0].PairId,
            invitations[0].Secret);
        using var issuedCertificate = X509CertificateLoader.LoadCertificate(issued.CertificateDer);
        RemoteRuntimeCertificateHash.Compute(issuedCertificate)
            .Should().Be((await registry.FindAsync(invitations[0].PairId))!.ProxyRuntimePublicKeyHash);
        var approveSecond = () => registry.ApproveAsync(
            invitations[1].PairId,
            "proxy-a",
            "runtime-a",
            "certificate-1");
        (await approveSecond.Should().ThrowAsync<RemoteRuntimePairingRegistryException>())
            .Which.Code.Should().Be("PairTargetAlreadyActive");
    }

    private sealed class Workspace : IDisposable
    {
        private Workspace(string root, EncryptionOptions encryptionOptions)
        {
            Root = root;
            EncryptionOptions = encryptionOptions;
        }

        public string Root { get; }
        public EncryptionOptions EncryptionOptions { get; }

        public static Workspace Create()
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                "SharpClaw.Tests",
                "remote-runtime-pairing-registry",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            return new Workspace(
                root,
                new EncryptionOptions
                {
                    Key = ApiKeyEncryptor.GenerateKey(),
                    EncryptProviderKeys = true,
                });
        }

        public SharpClawDbContext CreateDbContext()
        {
            var storageOptions = new JsonColdStoreStorageOptions
            {
                DataDirectory = Root,
                EncryptAtRest = false,
            };
            var options = new DbContextOptionsBuilder<SharpClawDbContext>()
                .UseJsonColdStoreDatabase(
                    Root,
                    store => JsonColdStoreRegistration.ConfigureStore(store, storageOptions, null))
                .Options;
            return new SharpClawDbContext(options);
        }

        public RemoteRuntimePairingRegistry CreateRegistry(SharpClawDbContext db)
            => new(
                db,
                new DatabaseProviderOptions
                {
                    Provider = StorageMode.JsonFile,
                    JsonFile = { DataDirectory = Root },
                },
                EncryptionOptions);

        public void Dispose()
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
    }

    private static RemoteRuntimePairingClaim CreateClaim(
        RemoteRuntimePairingInvitation invitation,
        string proxyRuntimeInstanceId)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(
            "CN=SharpClaw test proxy",
            key,
            HashAlgorithmName.SHA256);
        var csr = request.CreateSigningRequest();
        var publicKey = key.ExportSubjectPublicKeyInfo();
        var publicKeyHash = Convert.ToBase64String(SHA256.HashData(publicKey))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        var payload = RemoteRuntimePairingProof.CreateClaimProofPayload(
            invitation,
            proxyRuntimeInstanceId,
            publicKeyHash);
        var signature = key.SignData(payload, HashAlgorithmName.SHA256);
        CryptographicOperations.ZeroMemory(publicKey);
        CryptographicOperations.ZeroMemory(payload);
        return new RemoteRuntimePairingClaim(
            invitation.PairId,
            invitation.Secret,
            proxyRuntimeInstanceId,
            publicKeyHash,
            Convert.ToBase64String(csr),
            Convert.ToBase64String(signature));
    }
}
