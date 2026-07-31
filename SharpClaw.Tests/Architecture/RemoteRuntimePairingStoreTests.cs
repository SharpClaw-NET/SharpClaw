using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using FluentAssertions;
using SharpClaw.Shared.Instances;
using SharpClaw.Shared.RemoteRuntimeBridge;

namespace SharpClaw.Tests.Architecture;

[TestFixture]
[NonParallelizable]
public sealed class RemoteRuntimePairingStoreTests
{
    [Test]
    public async Task Invitation_claim_and_approval_bind_one_target_and_protect_secret()
    {
        using var workspace = PairingWorkspace.Create();
        var now = new DateTimeOffset(2026, 7, 31, 12, 0, 0, TimeSpan.Zero);
        var store = workspace.CreateStore(() => now);

        var invitation = await store.CreateInvitationAsync(
            "gateway-1",
            "gateway-key-hash",
            "runtime-1",
            "install-fingerprint",
            TimeSpan.FromMinutes(5));

        invitation.Secret.Should().NotBeNullOrWhiteSpace();
        var protectedBytes = File.ReadAllBytes(workspace.ActivePath);
        Encoding.UTF8.GetString(protectedBytes).Should().NotContain(invitation.Secret);

        var claim = await ClaimAsync(store, invitation, "proxy-1");
        claim.Status.Should().Be(RemoteRuntimePairStatus.ClaimPending);

        var approved = await store.ApproveClaimAsync(
            invitation.PairId,
            "proxy-1",
            "runtime-1");
        approved.Status.Should().Be(RemoteRuntimePairStatus.Active);

        var active = await store.RequireActiveAsync(
            invitation.PairId,
            "gateway-1",
            "runtime-1",
            "proxy-1");
        active.PairId.Should().Be(invitation.PairId);

        using var authority = await store.GetOrCreateCertificateAuthorityAsync();
        authority.HasPrivateKey.Should().BeTrue();
        var issued = await store.IssueClientCertificateAsync(invitation.PairId);
        issued.ProxyRuntimePublicKeyHash.Should().Be(active.ProxyRuntimePublicKeyHash);
        using var clientCertificate = X509CertificateLoader.LoadCertificate(issued.CertificateDer);
        clientCertificate.Subject.Should().Contain("proxy-1");
        clientCertificate.HasPrivateKey.Should().BeFalse();
        var validated = await store.RequireActiveCertificateAsync(
            clientCertificate,
            "gateway-1",
            "runtime-1");
        validated.PairId.Should().Be(invitation.PairId);

        var wrongTarget = () => store.RequireActiveCertificateAsync(
            clientCertificate,
            "gateway-1",
            "runtime-2");
        await wrongTarget.Should().ThrowAsync<RemoteRuntimePairingException>()
            .Where(exception => exception.Code == "PairNotAuthorized");

        var renewed = await store.RenewClientCertificateAsync(invitation.PairId);
        renewed.CertificateThumbprint.Should().NotBe(issued.CertificateThumbprint);
        var updatedProtectedBytes = File.ReadAllBytes(workspace.ActivePath);
        updatedProtectedBytes.Should().NotEqual(protectedBytes);
        updatedProtectedBytes[0].Should().Be(0x01);
        Encoding.UTF8.GetString(updatedProtectedBytes).Should().NotContain(invitation.Secret);
    }

    [Test]
    public async Task Claim_rejects_reuse_and_approval_rejects_target_change()
    {
        using var workspace = PairingWorkspace.Create();
        var store = workspace.CreateStore();
        var invitation = await store.CreateInvitationAsync(
            "gateway-1",
            "gateway-key-hash",
            "runtime-1",
            "install-fingerprint",
            TimeSpan.FromMinutes(5));

        await ClaimAsync(store, invitation, "proxy-1");

        var targetChange = () => store.ApproveClaimAsync(
            invitation.PairId,
            "proxy-2",
            "runtime-1");
        await targetChange.Should().ThrowAsync<RemoteRuntimePairingException>()
            .Where(exception => exception.Code == "PairTargetMismatch");

        var reuse = () => ClaimAsync(store, invitation, "proxy-2");
        await reuse.Should().ThrowAsync<RemoteRuntimePairingException>()
            .Where(exception => exception.Code == "InvalidPairState");
    }

    [Test]
    public async Task Expiry_and_revocation_remove_active_authorization()
    {
        using var workspace = PairingWorkspace.Create();
        var now = new DateTimeOffset(2026, 7, 31, 12, 0, 0, TimeSpan.Zero);
        var store = workspace.CreateStore(() => now);
        var invitation = await store.CreateInvitationAsync(
            "gateway-1",
            "gateway-key-hash",
            "runtime-1",
            "install-fingerprint",
            TimeSpan.FromMinutes(5));
        await ClaimAsync(store, invitation, "proxy-1");

        now = now.AddMinutes(6);
        var expiredApproval = () => store.ApproveClaimAsync(
            invitation.PairId,
            "proxy-1",
            "runtime-1");
        await expiredApproval.Should().ThrowAsync<RemoteRuntimePairingException>()
            .Where(exception => exception.Code == "InvalidPairState");

        var secondInvitation = await store.CreateInvitationAsync(
            "gateway-1",
            "gateway-key-hash",
            "runtime-1",
            "install-fingerprint",
            TimeSpan.FromMinutes(5));
        await ClaimAsync(store, secondInvitation, "proxy-1");
        await store.ApproveClaimAsync(
            secondInvitation.PairId,
            "proxy-1",
            "runtime-1");

        var revoked = await store.RevokeAsync(secondInvitation.PairId);
        revoked.Status.Should().Be(RemoteRuntimePairStatus.Revoked);

        var authorization = () => store.RequireActiveAsync(
            secondInvitation.PairId,
            "gateway-1",
            "runtime-1",
            "proxy-1");
        await authorization.Should().ThrowAsync<RemoteRuntimePairingException>()
            .Where(exception => exception.Code == "PairNotAuthorized");
    }

    [Test]
    public async Task A_proxy_cannot_bind_two_active_targets()
    {
        using var workspace = PairingWorkspace.Create();
        var store = workspace.CreateStore();
        var first = await CreateActiveAsync(store, "runtime-1", "proxy-1");
        _ = first;
        var second = await store.CreateInvitationAsync(
            "gateway-1",
            "gateway-key-hash",
            "runtime-2",
            "install-fingerprint-2",
            TimeSpan.FromMinutes(5));

        var claim = () => ClaimAsync(store, second, "proxy-1");
        await claim.Should().ThrowAsync<RemoteRuntimePairingException>()
            .Where(exception => exception.Code == "ProxyAlreadyPaired");
    }

    private static async Task<RemoteRuntimePairingInvitation> CreateActiveAsync(
        RemoteRuntimePairingStore store,
        string runtimeId,
        string proxyId)
    {
        var invitation = await store.CreateInvitationAsync(
            "gateway-1",
            "gateway-key-hash",
            runtimeId,
            "install-fingerprint",
            TimeSpan.FromMinutes(5));
        await ClaimAsync(store, invitation, proxyId);
        await store.ApproveClaimAsync(invitation.PairId, proxyId, runtimeId);
        return invitation;
    }

    [Test]
    public async Task Claim_requires_proof_of_possession()
    {
        using var workspace = PairingWorkspace.Create();
        var store = workspace.CreateStore();
        var invitation = await store.CreateInvitationAsync(
            "gateway-1",
            "gateway-key-hash",
            "runtime-1",
            "install-fingerprint",
            TimeSpan.FromMinutes(5));

        var rejected = () => store.ClaimInvitationAsync(
            invitation.PairId,
            invitation.Secret,
            "proxy-1",
            Convert.ToBase64String([1, 2, 3]),
            Convert.ToBase64String([4, 5, 6]));

        await rejected.Should().ThrowAsync<RemoteRuntimePairingException>()
            .Where(exception => exception.Code == "InvalidProof");
    }

    private static async Task<RemoteRuntimePairingRecord> ClaimAsync(
        RemoteRuntimePairingStore store,
        RemoteRuntimePairingInvitation invitation,
        string proxyRuntimeInstanceId)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(
            $"CN={proxyRuntimeInstanceId}",
            key,
            HashAlgorithmName.SHA256);
        var requestBytes = request.CreateSigningRequest();
        var publicKey = key.ExportSubjectPublicKeyInfo();
        var publicKeyHash = Convert.ToBase64String(SHA256.HashData(publicKey))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        var proofPayload = RemoteRuntimePairingStore.CreateClaimProofPayload(
            invitation,
            proxyRuntimeInstanceId,
            publicKeyHash);
        var proof = key.SignData(proofPayload, HashAlgorithmName.SHA256);

        var claim = await store.ClaimInvitationAsync(
            invitation.PairId,
            invitation.Secret,
            proxyRuntimeInstanceId,
            Convert.ToBase64String(requestBytes),
            Convert.ToBase64String(proof));

        CryptographicOperations.ZeroMemory(publicKey);
        CryptographicOperations.ZeroMemory(proofPayload);
        CryptographicOperations.ZeroMemory(proof);
        return claim;
    }

    private sealed class PairingWorkspace : IDisposable
    {
        private PairingWorkspace(string root, SharpClawInstancePaths paths)
        {
            Root = root;
            Paths = paths;
            Store = RemoteRuntimePairingStore.Create(paths);
        }

        public string Root { get; }
        public SharpClawInstancePaths Paths { get; }
        public RemoteRuntimePairingStore Store { get; }
        public string ActivePath => Path.Combine(Paths.RemoteRuntimePairingDirectory, ".env");

        public static PairingWorkspace Create()
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                "SharpClaw.Tests",
                "remote-runtime-pairing",
                Guid.NewGuid().ToString("N"));
            var paths = new SharpClawInstancePaths(
                SharpClawInstanceKind.Backend,
                Path.Combine(root, "instance"));
            return new PairingWorkspace(root, paths);
        }

        public RemoteRuntimePairingStore CreateStore(Func<DateTimeOffset>? utcNow = null)
        {
            if (utcNow is null)
                return Store;

            var packageStore = new Supprocom.Secrets.SupprocomSecretFileStore(CreateOptions(Paths));
            return new RemoteRuntimePairingStore(packageStore, packageStore, utcNow);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Root))
                    Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
            }
        }

        private static Supprocom.Secrets.SupprocomSecretsOptions CreateOptions(
            SharpClawInstancePaths paths)
        {
            var keyPath = paths.GetSecretFilePath("encryption-key");
            return new Supprocom.Secrets.SupprocomSecretsOptions
            {
                EnvironmentName = "Production",
                FileOverridesProcessEnvironment = true,
                File =
                {
                    Directory = paths.RemoteRuntimePairingDirectory,
                    ActiveName = ".env",
                    DevelopmentName = ".dev.env",
                    TemplateName = ".env.template",
                    DevelopmentTemplateName = ".dev.env.template",
                    Import = Supprocom.Secrets.SecretFileImport.JsonWithCommentsOnce,
                    DevelopmentComposition = Supprocom.Secrets.SecretFileComposition.Overlay,
                    Recovery = Supprocom.Secrets.SecretFileRecovery.QuarantineAndRestoreTemplate,
                    Protection = Supprocom.Secrets.SecretFileProtection.InstallationBoundAesGcm,
                    InstallationKeyPath = keyPath,
                    InstallationKeyStore = new SharpClaw.Shared.Security.SharpClawInstallationKeyStore(keyPath),
                },
            };
        }
    }
}
