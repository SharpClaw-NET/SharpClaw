using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.EntityFrameworkCore;
using SharpClaw.Contracts.Enums;
using SharpClaw.Contracts.Persistence;
using SharpClaw.Shared.RemoteRuntimeBridge;
using SharpClaw.Shared.Security;

namespace SharpClaw.Runtime.INF.Persistence;

public sealed class RemoteRuntimePairingRegistry(
    SharpClawDbContext db,
    DatabaseProviderOptions databaseOptions,
    EncryptionOptions encryptionOptions)
{
    private const int CurrentBridgeProtocolMajor = 1;
    private static readonly TimeSpan MaximumInvitationLifetime = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan MaximumRenewalLifetime = TimeSpan.FromDays(365);

    public async Task<RemoteRuntimePairingInvitation> CreateInvitationAsync(
        string gatewayInstanceId,
        string gatewayServerPublicKeyHash,
        string authoritativeRuntimeInstanceId,
        string authoritativeRuntimeInstallFingerprint,
        TimeSpan lifetime,
        string? displayName = null,
        string? description = null,
        byte[]? certificateAuthorityPfx = null,
        CancellationToken cancellationToken = default)
    {
        RequireJsonColdStore();
        RequireText(gatewayInstanceId, nameof(gatewayInstanceId));
        RequireText(gatewayServerPublicKeyHash, nameof(gatewayServerPublicKeyHash));
        RequireText(authoritativeRuntimeInstanceId, nameof(authoritativeRuntimeInstanceId));
        RequireText(authoritativeRuntimeInstallFingerprint, nameof(authoritativeRuntimeInstallFingerprint));
        if (lifetime <= TimeSpan.Zero || lifetime > MaximumInvitationLifetime)
        {
            throw new ArgumentOutOfRangeException(
                nameof(lifetime),
                "The pairing invitation lifetime must be positive and no longer than fifteen minutes.");
        }

        var now = DateTimeOffset.UtcNow;
        var pairId = Guid.NewGuid();
        var secretBytes = RandomNumberGenerator.GetBytes(32);
        var secret = Base64UrlEncode(secretBytes);
        var invitationHash = HashSecret(secret);
        CryptographicOperations.ZeroMemory(secretBytes);

        var entity = new RemoteRuntimePairingDB
        {
            Id = Guid.NewGuid(),
            PairId = pairId,
            Status = RemoteRuntimePairStatus.InvitationIssued,
            GatewayInstanceId = gatewayInstanceId,
            GatewayServerPublicKeyHash = gatewayServerPublicKeyHash,
            AuthoritativeRuntimeInstanceId = authoritativeRuntimeInstanceId,
            AuthoritativeRuntimeInstallFingerprint = authoritativeRuntimeInstallFingerprint,
            InvitationHash = invitationHash,
            BridgeProtocolMajor = CurrentBridgeProtocolMajor,
            EncryptedCertificateAuthorityPfx = certificateAuthorityPfx is null
                ? null
                : EncryptSecret(Convert.ToBase64String(certificateAuthorityPfx)),
            DisplayName = NormalizeOptional(displayName),
            Description = NormalizeOptional(description),
            CreatedAtUtc = now,
            ExpiresAtUtc = now.Add(lifetime),
            UpdatedAtUtc = now,
            Revision = 1,
        };

        db.RemoteRuntimePairings.Add(entity);
        await db.SaveChangesAsync(cancellationToken);

        return new RemoteRuntimePairingInvitation(
            pairId,
            secret,
            gatewayInstanceId,
            gatewayServerPublicKeyHash,
            authoritativeRuntimeInstanceId,
            authoritativeRuntimeInstallFingerprint,
            CurrentBridgeProtocolMajor,
            entity.ExpiresAtUtc);
    }

    public async Task<RemoteRuntimePairingRegistryEntry?> FindAsync(
        Guid pairId,
        CancellationToken cancellationToken = default)
    {
        RequireJsonColdStore();
        var entity = await db.RemoteRuntimePairings
            .AsNoTracking()
            .SingleOrDefaultAsync(pairing => pairing.PairId == pairId, cancellationToken);
        return entity is null ? null : ToEntry(entity);
    }

    public async Task<RemoteRuntimePairingRegistryPage> ListAsync(
        RemoteRuntimePairingRegistryFilter filter,
        int take,
        RemoteRuntimePairingPageCursor? cursor = null,
        CancellationToken cancellationToken = default)
    {
        RequireJsonColdStore();
        if (take is < 1 or > 200)
            throw new ArgumentOutOfRangeException(nameof(take), "Pairing page size must be between one and two hundred.");

        var query = db.RemoteRuntimePairings.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(filter.GatewayInstanceId))
            query = query.Where(pairing => pairing.GatewayInstanceId == filter.GatewayInstanceId);
        if (!string.IsNullOrWhiteSpace(filter.AuthoritativeRuntimeInstanceId))
            query = query.Where(pairing => pairing.AuthoritativeRuntimeInstanceId == filter.AuthoritativeRuntimeInstanceId);
        if (!string.IsNullOrWhiteSpace(filter.ProxyRuntimeInstanceId))
            query = query.Where(pairing => pairing.ProxyRuntimeInstanceId == filter.ProxyRuntimeInstanceId);
        if (filter.Status is { } status)
            query = query.Where(pairing => pairing.Status == status);
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var search = filter.Search.Trim();
            query = query.Where(pairing =>
                (pairing.DisplayName != null && pairing.DisplayName.Contains(search))
                || (pairing.Description != null && pairing.Description.Contains(search))
                || pairing.PairId.ToString().Contains(search));
        }

        if (cursor is { } pageCursor)
        {
            query = query.Where(pairing =>
                pairing.CreatedAtUtc > pageCursor.CreatedAtUtc
                || (pairing.CreatedAtUtc == pageCursor.CreatedAtUtc && pairing.Id.CompareTo(pageCursor.Id) > 0));
        }

        var rows = await query
            .OrderBy(pairing => pairing.CreatedAtUtc)
            .ThenBy(pairing => pairing.Id)
            .Take(take + 1)
            .ToListAsync(cancellationToken);
        var hasMore = rows.Count > take;
        if (hasMore)
            rows.RemoveAt(rows.Count - 1);

        var next = rows.Count == 0 || !hasMore
            ? null
            : new RemoteRuntimePairingPageCursor(rows[^1].CreatedAtUtc, rows[^1].Id);
        return new RemoteRuntimePairingRegistryPage(rows.Select(ToEntry).ToArray(), hasMore, next);
    }

    public async Task<RemoteRuntimePairingRegistryEntry> ClaimAsync(
        RemoteRuntimePairingClaim claim,
        CancellationToken cancellationToken = default)
    {
        RequireJsonColdStore();
        RequireText(claim.InvitationSecret, nameof(claim.InvitationSecret));
        RequireText(claim.ProxyRuntimeInstanceId, nameof(claim.ProxyRuntimeInstanceId));
        RequireText(claim.ProxyRuntimePublicKeyHash, nameof(claim.ProxyRuntimePublicKeyHash));
        RequireText(claim.CertificateSigningRequestBase64, nameof(claim.CertificateSigningRequestBase64));

        var entity = await db.RemoteRuntimePairings
            .SingleOrDefaultAsync(pairing => pairing.PairId == claim.PairId, cancellationToken)
            ?? throw Error("PairNotFound", "The pairing record was not found.");
        var now = DateTimeOffset.UtcNow;
        RequireStatus(entity, RemoteRuntimePairStatus.InvitationIssued, now);
        VerifyInvitationSecret(entity.InvitationHash, claim.InvitationSecret);
        var proxyRuntimePublicKeyHash = VerifyClaimProof(entity, claim);

        entity.Status = RemoteRuntimePairStatus.ClaimPending;
        entity.ProxyRuntimeInstanceId = claim.ProxyRuntimeInstanceId;
        entity.ProxyRuntimePublicKeyHash = proxyRuntimePublicKeyHash;
        entity.ProxyRuntimeCertificateSigningRequest = claim.CertificateSigningRequestBase64;
        entity.ClaimedAtUtc = now;
        entity.StatusReason = null;
        Touch(entity, now);
        await db.SaveChangesAsync(cancellationToken);
        return ToEntry(entity);
    }

    public async Task<RemoteRuntimePairingRegistryEntry> ApproveAsync(
        Guid pairId,
        string expectedProxyRuntimeInstanceId,
        string expectedAuthoritativeRuntimeInstanceId,
        string clientCertificateIdentity,
        CancellationToken cancellationToken = default)
    {
        RequireJsonColdStore();
        RequireText(expectedProxyRuntimeInstanceId, nameof(expectedProxyRuntimeInstanceId));
        RequireText(expectedAuthoritativeRuntimeInstanceId, nameof(expectedAuthoritativeRuntimeInstanceId));
        RequireText(clientCertificateIdentity, nameof(clientCertificateIdentity));

        var entity = await RequireEntityAsync(pairId, cancellationToken);
        RequireStatus(entity, RemoteRuntimePairStatus.ClaimPending, DateTimeOffset.UtcNow);
        if (!string.Equals(entity.ProxyRuntimeInstanceId, expectedProxyRuntimeInstanceId, StringComparison.Ordinal)
            || !string.Equals(entity.AuthoritativeRuntimeInstanceId, expectedAuthoritativeRuntimeInstanceId, StringComparison.Ordinal))
        {
            throw Error("PairTargetMismatch", "The pairing claim does not match the selected Runtime target.");
        }

        var activeTargetRows = await db.RemoteRuntimePairings
            .AsNoTracking()
            .ToListAsync(cancellationToken);
        var targetAlreadyActive = activeTargetRows.Any(pairing =>
            pairing.PairId != pairId
            && pairing.Status == RemoteRuntimePairStatus.Active
            && string.Equals(pairing.GatewayInstanceId, entity.GatewayInstanceId, StringComparison.Ordinal)
            && string.Equals(
                pairing.AuthoritativeRuntimeInstanceId,
                entity.AuthoritativeRuntimeInstanceId,
                StringComparison.Ordinal)
            && pairing.ExpiresAtUtc > DateTimeOffset.UtcNow);
        if (targetAlreadyActive)
            throw Error("PairTargetAlreadyActive", "The Runtime target already has an active pairing.");

        var now = DateTimeOffset.UtcNow;
        entity.Status = RemoteRuntimePairStatus.Active;
        entity.ClientCertificateIdentity = clientCertificateIdentity;
        entity.ApprovedAtUtc = now;
        entity.StatusReason = null;
        Touch(entity, now);
        await db.SaveChangesAsync(cancellationToken);
        return ToEntry(entity);
    }

    public Task<RemoteRuntimePairingRegistryEntry> RejectAsync(
        Guid pairId,
        string reason,
        CancellationToken cancellationToken = default)
        => TransitionAsync(
            pairId,
            RemoteRuntimePairStatus.ClaimPending,
            entity =>
            {
                RequireText(reason, nameof(reason));
                entity.Status = RemoteRuntimePairStatus.Rejected;
                entity.StatusReason = reason;
            },
            cancellationToken);

    public Task<RemoteRuntimePairingRegistryEntry> RevokeAsync(
        Guid pairId,
        string reason,
        CancellationToken cancellationToken = default)
        => TransitionAsync(
            pairId,
            RemoteRuntimePairStatus.Active,
            entity =>
            {
                RequireText(reason, nameof(reason));
                entity.Status = RemoteRuntimePairStatus.Revoked;
                entity.RevokedAtUtc = DateTimeOffset.UtcNow;
                entity.StatusReason = reason;
            },
            cancellationToken);

    public async Task<RemoteRuntimePairingRegistryEntry> RenewAsync(
        Guid pairId,
        DateTimeOffset expiresAtUtc,
        CancellationToken cancellationToken = default)
    {
        RequireJsonColdStore();
        var entity = await RequireEntityAsync(pairId, cancellationToken);
        var now = DateTimeOffset.UtcNow;
        if (entity.Status != RemoteRuntimePairStatus.Active || expiresAtUtc <= now || expiresAtUtc - now > MaximumRenewalLifetime)
            throw Error("InvalidRenewal", "The pairing renewal is invalid.");

        entity.ExpiresAtUtc = expiresAtUtc;
        entity.RenewedAtUtc = now;
        entity.StatusReason = null;
        Touch(entity, now);
        await db.SaveChangesAsync(cancellationToken);
        return ToEntry(entity);
    }

    public async Task<RemoteRuntimePairingRegistryEntry> UpdateDetailsAsync(
        Guid pairId,
        string? displayName,
        string? description,
        CancellationToken cancellationToken = default)
    {
        RequireJsonColdStore();
        var entity = await RequireEntityAsync(pairId, cancellationToken);
        entity.DisplayName = NormalizeOptional(displayName);
        entity.Description = NormalizeOptional(description);
        Touch(entity, DateTimeOffset.UtcNow);
        await db.SaveChangesAsync(cancellationToken);
        return ToEntry(entity);
    }

    public async Task DeleteAsync(Guid pairId, CancellationToken cancellationToken = default)
    {
        RequireJsonColdStore();
        var entity = await RequireEntityAsync(pairId, cancellationToken);
        db.RemoteRuntimePairings.Remove(entity);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<RemoteRuntimePairingRegistryEntry?> FindActiveTargetAsync(
        string gatewayInstanceId,
        string authoritativeRuntimeInstanceId,
        CancellationToken cancellationToken = default)
    {
        RequireJsonColdStore();
        var now = DateTimeOffset.UtcNow;
        var entity = await db.RemoteRuntimePairings
            .AsNoTracking()
            .Where(pairing => pairing.Status == RemoteRuntimePairStatus.Active
                && pairing.GatewayInstanceId == gatewayInstanceId
                && pairing.AuthoritativeRuntimeInstanceId == authoritativeRuntimeInstanceId
                && pairing.ExpiresAtUtc > now)
            .OrderByDescending(pairing => pairing.UpdatedAtUtc)
            .ThenBy(pairing => pairing.Id)
            .FirstOrDefaultAsync(cancellationToken);
        return entity is null ? null : ToEntry(entity);
    }

    public async Task<RemoteRuntimePairingRegistryEntry> TouchLastSeenAsync(
        Guid pairId,
        CancellationToken cancellationToken = default)
    {
        RequireJsonColdStore();
        var entity = await RequireEntityAsync(pairId, cancellationToken);
        entity.LastSeenAtUtc = DateTimeOffset.UtcNow;
        Touch(entity, entity.LastSeenAtUtc.Value);
        await db.SaveChangesAsync(cancellationToken);
        return ToEntry(entity);
    }

    public async Task SetCertificateAuthorityPfxAsync(
        Guid pairId,
        ReadOnlyMemory<byte> certificateAuthorityPfx,
        CancellationToken cancellationToken = default)
    {
        RequireJsonColdStore();
        if (certificateAuthorityPfx.IsEmpty)
            throw new ArgumentException("The certificate authority value must not be empty.", nameof(certificateAuthorityPfx));

        var entity = await RequireEntityAsync(pairId, cancellationToken);
        entity.EncryptedCertificateAuthorityPfx = EncryptSecret(Convert.ToBase64String(certificateAuthorityPfx.Span));
        Touch(entity, DateTimeOffset.UtcNow);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<byte[]?> GetCertificateAuthorityPfxAsync(
        Guid pairId,
        CancellationToken cancellationToken = default)
    {
        RequireJsonColdStore();
        var entity = await db.RemoteRuntimePairings
            .AsNoTracking()
            .SingleOrDefaultAsync(pairing => pairing.PairId == pairId, cancellationToken)
            ?? throw Error("PairNotFound", "The pairing record was not found.");
        if (string.IsNullOrWhiteSpace(entity.EncryptedCertificateAuthorityPfx))
            return null;

        var encoded = ApiKeyEncryptor.Decrypt(entity.EncryptedCertificateAuthorityPfx, encryptionOptions.Key);
        return Convert.FromBase64String(encoded);
    }

    private async Task<RemoteRuntimePairingRegistryEntry> TransitionAsync(
        Guid pairId,
        RemoteRuntimePairStatus requiredStatus,
        Action<RemoteRuntimePairingDB> transition,
        CancellationToken cancellationToken)
    {
        RequireJsonColdStore();
        var entity = await RequireEntityAsync(pairId, cancellationToken);
        RequireStatus(entity, requiredStatus, DateTimeOffset.UtcNow);
        transition(entity);
        Touch(entity, DateTimeOffset.UtcNow);
        await db.SaveChangesAsync(cancellationToken);
        return ToEntry(entity);
    }

    private async Task<RemoteRuntimePairingDB> RequireEntityAsync(
        Guid pairId,
        CancellationToken cancellationToken)
        => await db.RemoteRuntimePairings
            .SingleOrDefaultAsync(pairing => pairing.PairId == pairId, cancellationToken)
            ?? throw Error("PairNotFound", "The pairing record was not found.");

    private void RequireJsonColdStore()
    {
        if (databaseOptions.Provider != StorageMode.JsonFile)
        {
            throw Error(
                "PairingRegistryProviderUnsupported",
                "The remote Runtime pairing registry requires the selected JSONColdStore provider.");
        }
    }

    private string EncryptSecret(string value) => ApiKeyEncryptor.Encrypt(value, encryptionOptions.Key);

    private static void RequireStatus(
        RemoteRuntimePairingDB entity,
        RemoteRuntimePairStatus requiredStatus,
        DateTimeOffset now)
    {
        var effectiveStatus = entity.Status is (RemoteRuntimePairStatus.InvitationIssued
            or RemoteRuntimePairStatus.ClaimPending
            or RemoteRuntimePairStatus.Active)
            && entity.ExpiresAtUtc <= now
            ? RemoteRuntimePairStatus.Expired
            : entity.Status;
        if (effectiveStatus != requiredStatus)
            throw Error("InvalidPairState", "The pairing is not in the required state.");
    }

    private static void VerifyInvitationSecret(string storedHash, string secret)
    {
        var supplied = Convert.FromBase64String(HashSecret(secret));
        var expected = Convert.FromBase64String(storedHash);
        try
        {
            if (!CryptographicOperations.FixedTimeEquals(supplied, expected))
                throw Error("InvalidInvitation", "The pairing invitation is invalid.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(supplied);
            CryptographicOperations.ZeroMemory(expected);
        }
    }

    private static string VerifyClaimProof(
        RemoteRuntimePairingDB entity,
        RemoteRuntimePairingClaim claim)
    {
        RequireText(claim.ProofSignatureBase64, nameof(claim.ProofSignatureBase64));
        var requestBytes = DecodeBase64(claim.CertificateSigningRequestBase64, "InvalidProof");
        var proof = DecodeBase64(claim.ProofSignatureBase64, "InvalidProof");
        try
        {
            CertificateRequest request;
            try
            {
                request = CertificateRequest.LoadSigningRequest(
                    requestBytes,
                    HashAlgorithmName.SHA256);
            }
            catch (CryptographicException)
            {
                throw Error("InvalidProof", "The pairing certificate request is invalid.");
            }

            var publicKey = request.PublicKey.ExportSubjectPublicKeyInfo();
            var publicKeyHash = Convert.ToBase64String(SHA256.HashData(publicKey))
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
            try
            {
                if (!string.IsNullOrWhiteSpace(claim.ProxyRuntimePublicKeyHash)
                    && !string.Equals(publicKeyHash, claim.ProxyRuntimePublicKeyHash, StringComparison.Ordinal))
                    throw Error("PairCredentialMismatch", "The pairing proof key does not match the claimed public key.");

                using var verifier = ECDsa.Create();
                verifier.ImportSubjectPublicKeyInfo(publicKey, out _);
                var payload = RemoteRuntimePairingProof.CreateClaimProofPayload(
                    entity.PairId,
                    entity.GatewayInstanceId,
                    entity.AuthoritativeRuntimeInstanceId,
                    claim.ProxyRuntimeInstanceId,
                    publicKeyHash,
                    claim.InvitationSecret);
                try
                {
                    if (!verifier.VerifyData(payload, proof, HashAlgorithmName.SHA256))
                        throw Error("InvalidProof", "The pairing proof does not match the invitation claim.");
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(payload);
                }
            }
            catch (CryptographicException)
            {
                throw Error("InvalidProof", "The pairing proof key is invalid.");
            }
            finally
            {
                CryptographicOperations.ZeroMemory(publicKey);
            }

            return publicKeyHash;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(requestBytes);
            CryptographicOperations.ZeroMemory(proof);
        }
    }

    private static byte[] DecodeBase64(string value, string errorCode)
    {
        try
        {
            return Convert.FromBase64String(value);
        }
        catch (FormatException)
        {
            throw Error(errorCode, "The pairing credential encoding is invalid.");
        }
    }

    private static RemoteRuntimePairingRegistryEntry ToEntry(RemoteRuntimePairingDB entity)
        => new(
            entity.Id,
            entity.PairId,
            entity.Status,
            entity.GatewayInstanceId,
            entity.GatewayServerPublicKeyHash,
            entity.AuthoritativeRuntimeInstanceId,
            entity.AuthoritativeRuntimeInstallFingerprint,
            entity.BridgeProtocolMajor,
            entity.ProxyRuntimeInstanceId,
            entity.ProxyRuntimePublicKeyHash,
            entity.ClientCertificateIdentity,
            entity.DisplayName,
            entity.Description,
            entity.StatusReason,
            entity.CreatedAtUtc,
            entity.ClaimedAtUtc,
            entity.ApprovedAtUtc,
            entity.RenewedAtUtc,
            entity.RevokedAtUtc,
            entity.ExpiresAtUtc,
            entity.LastSeenAtUtc,
            entity.UpdatedAtUtc,
            entity.Revision);

    private static void Touch(RemoteRuntimePairingDB entity, DateTimeOffset now)
    {
        entity.UpdatedAtUtc = now;
        entity.Revision++;
    }

    private static string? NormalizeOptional(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static void RequireText(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("A nonblank value is required.", parameterName);
    }

    private static string HashSecret(string secret)
        => Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));

    private static string Base64UrlEncode(ReadOnlySpan<byte> bytes)
        => Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static RemoteRuntimePairingRegistryException Error(string code, string message)
        => new(code, message);
}
