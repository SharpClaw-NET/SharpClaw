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

        var pending = await registry.ClaimAsync(new RemoteRuntimePairingClaim(
            invitation.PairId,
            invitation.Secret,
            "proxy-a",
            "proxy-key-hash",
            "csr"));
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

        var act = () => registry.ClaimAsync(new RemoteRuntimePairingClaim(
            invitation.PairId,
            "wrong-secret",
            "proxy-a",
            "proxy-key-hash",
            "csr"));
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
            await registry.ClaimAsync(new RemoteRuntimePairingClaim(
                invitation.PairId,
                invitation.Secret,
                "proxy-a",
                "proxy-key-hash",
                "csr"));
        }

        await registry.ApproveAsync(
            invitations[0].PairId,
            "proxy-a",
            "runtime-a",
            "certificate-0");
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
}
