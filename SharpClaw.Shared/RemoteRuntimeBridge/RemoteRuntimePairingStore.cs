using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using SharpClaw.Shared.Instances;
using SharpClaw.Shared.Security;
using Supprocom.Secrets;

namespace SharpClaw.Shared.RemoteRuntimeBridge;

public sealed class RemoteRuntimePairingStore
{
    private const string PairPrefix = "Pairs:";
    private const string CertificateAuthorityPfxKey = "CertificateAuthority:Pfx";
    private const int CurrentBridgeProtocolMajor = 1;
    private static readonly TimeSpan MaximumInvitationLifetime = TimeSpan.FromMinutes(15);

    private readonly ISecretDocumentStore _documentStore;
    private readonly ISecretDocumentUpdater _documentUpdater;
    private readonly Func<DateTimeOffset> _utcNow;

    public RemoteRuntimePairingStore(
        ISecretDocumentStore documentStore,
        ISecretDocumentUpdater documentUpdater,
        Func<DateTimeOffset>? utcNow = null)
    {
        _documentStore = documentStore ?? throw new ArgumentNullException(nameof(documentStore));
        _documentUpdater = documentUpdater ?? throw new ArgumentNullException(nameof(documentUpdater));
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public static RemoteRuntimePairingStore Create(SharpClawInstancePaths instancePaths)
    {
        ArgumentNullException.ThrowIfNull(instancePaths);
        instancePaths.EnsureDirectories();

        var directory = instancePaths.RemoteRuntimePairingDirectory;
        Directory.CreateDirectory(directory);
        var templatePath = Path.Combine(directory, ".env.template");
        if (!File.Exists(templatePath))
            File.WriteAllText(templatePath, string.Empty, Encoding.UTF8);

        var installationKeyPath = instancePaths.GetSecretFilePath("encryption-key");
        var options = new SupprocomSecretsOptions
        {
            EnvironmentName = "Production",
            FileOverridesProcessEnvironment = true,
            File =
            {
                Directory = directory,
                ActiveName = ".env",
                DevelopmentName = ".dev.env",
                TemplateName = ".env.template",
                DevelopmentTemplateName = ".dev.env.template",
                Import = SecretFileImport.JsonWithCommentsOnce,
                DevelopmentComposition = SecretFileComposition.Overlay,
                Recovery = SecretFileRecovery.QuarantineAndRestoreTemplate,
                Protection = SecretFileProtection.InstallationBoundAesGcm,
                InstallationKeyPath = installationKeyPath,
                InstallationKeyStore = new SharpClawInstallationKeyStore(installationKeyPath),
            },
        };

        var store = new SupprocomSecretFileStore(options);
        return new RemoteRuntimePairingStore(store, store);
    }

    public async Task<RemoteRuntimePairingInvitation> CreateInvitationAsync(
        string gatewayInstanceId,
        string gatewayServerPublicKeyHash,
        string authoritativeRuntimeInstanceId,
        string authoritativeRuntimeInstallFingerprint,
        TimeSpan lifetime,
        CancellationToken cancellationToken = default)
    {
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

        var now = _utcNow();
        var pairId = Guid.NewGuid();
        var secretBytes = RandomNumberGenerator.GetBytes(32);
        var secret = Base64UrlEncode(secretBytes);
        var invitationHash = HashSecret(secret);
        CryptographicOperations.ZeroMemory(secretBytes);

        var record = new RemoteRuntimePairingRecord(
            pairId,
            RemoteRuntimePairStatus.InvitationIssued,
            gatewayInstanceId,
            gatewayServerPublicKeyHash,
            authoritativeRuntimeInstanceId,
            authoritativeRuntimeInstallFingerprint,
            invitationHash,
            CurrentBridgeProtocolMajor,
            now,
            now.Add(lifetime),
            null,
            null,
            null,
            null,
            null,
            null);

        await AddRecordAsync(record, cancellationToken);
        return new RemoteRuntimePairingInvitation(
            record.PairId,
            secret,
            record.GatewayInstanceId,
            record.GatewayServerPublicKeyHash,
            record.AuthoritativeRuntimeInstanceId,
            record.AuthoritativeRuntimeInstallFingerprint,
            record.BridgeProtocolMajor,
            record.ExpiresAtUtc);
    }

    public async Task<RemoteRuntimePairingRecord> ClaimInvitationAsync(
        Guid pairId,
        string secret,
        string proxyRuntimeInstanceId,
        string certificateSigningRequestBase64,
        string proofSignatureBase64,
        CancellationToken cancellationToken = default)
    {
        RequireText(secret, nameof(secret));
        RequireText(proxyRuntimeInstanceId, nameof(proxyRuntimeInstanceId));
        RequireText(certificateSigningRequestBase64, nameof(certificateSigningRequestBase64));
        RequireText(proofSignatureBase64, nameof(proofSignatureBase64));

        RemoteRuntimePairingRecord? claimed = null;
        await _documentUpdater.UpdateDocumentAsync(settings =>
        {
            var records = ReadRecords(settings);
            var record = FindRecord(records, pairId);
            var now = _utcNow();
            RequireStatus(record, RemoteRuntimePairStatus.InvitationIssued, now);

            var suppliedHash = Convert.FromBase64String(HashSecret(secret));
            var storedHash = Convert.FromBase64String(record.InvitationHash);
            var matches = CryptographicOperations.FixedTimeEquals(suppliedHash, storedHash);
            CryptographicOperations.ZeroMemory(suppliedHash);
            CryptographicOperations.ZeroMemory(storedHash);
            if (!matches)
                throw new RemoteRuntimePairingException("InvalidInvitation", "The pairing invitation is invalid.");

            var signingRequest = LoadSigningRequest(certificateSigningRequestBase64);
            var publicKey = signingRequest.PublicKey.ExportSubjectPublicKeyInfo();
            var publicKeyHash = Base64UrlEncode(SHA256.HashData(publicKey));
            var proof = DecodeBase64(proofSignatureBase64, "InvalidProof");
            using var verifier = ECDsa.Create();
            try
            {
                verifier.ImportSubjectPublicKeyInfo(publicKey, out _);
            }
            catch (CryptographicException)
            {
                CryptographicOperations.ZeroMemory(publicKey);
                CryptographicOperations.ZeroMemory(proof);
                throw new RemoteRuntimePairingException(
                    "InvalidProof",
                    "The pairing proof key is invalid.");
            }

            var proofPayload = CreateClaimProofPayload(record, secret, proxyRuntimeInstanceId, publicKeyHash);
            var proofMatches = verifier.VerifyData(proofPayload, proof, HashAlgorithmName.SHA256);
            CryptographicOperations.ZeroMemory(publicKey);
            CryptographicOperations.ZeroMemory(proof);
            CryptographicOperations.ZeroMemory(proofPayload);
            if (!proofMatches)
                throw new RemoteRuntimePairingException(
                    "InvalidProof",
                    "The pairing proof does not match the invitation claim.");

            if (records.Any(existing =>
                    existing.IsActive(now)
                    && string.Equals(
                        existing.ProxyRuntimeInstanceId,
                        proxyRuntimeInstanceId,
                        StringComparison.Ordinal)))
            {
                throw new RemoteRuntimePairingException(
                    "ProxyAlreadyPaired",
                    "The proxy Runtime already has an active pairing target.");
            }

            claimed = record with
            {
                Status = RemoteRuntimePairStatus.ClaimPending,
                ProxyRuntimeInstanceId = proxyRuntimeInstanceId,
                ProxyRuntimePublicKeyHash = publicKeyHash,
                ProxyRuntimeCertificateSigningRequest = certificateSigningRequestBase64,
                ClaimedAtUtc = now,
            };
            return ReplaceRecord(settings, claimed);
        }, cancellationToken);

        return claimed!;
    }

    public static byte[] CreateClaimProofPayload(
        RemoteRuntimePairingInvitation invitation,
        string proxyRuntimeInstanceId,
        string proxyRuntimePublicKeyHash)
    {
        ArgumentNullException.ThrowIfNull(invitation);
        RequireText(proxyRuntimeInstanceId, nameof(proxyRuntimeInstanceId));
        RequireText(proxyRuntimePublicKeyHash, nameof(proxyRuntimePublicKeyHash));

        return Encoding.UTF8.GetBytes(
            string.Join(
                '|',
                invitation.PairId.ToString("D", CultureInfo.InvariantCulture),
                invitation.GatewayInstanceId,
                invitation.AuthoritativeRuntimeInstanceId,
                proxyRuntimeInstanceId,
                proxyRuntimePublicKeyHash,
                invitation.Secret));
    }

    public async Task<X509Certificate2> GetOrCreateCertificateAuthorityAsync(
        CancellationToken cancellationToken = default)
    {
        X509Certificate2? authority = null;
        await _documentUpdater.UpdateDocumentAsync(settings =>
        {
            var existing = settings.SingleOrDefault(
                setting => string.Equals(
                    setting.Key,
                    CertificateAuthorityPfxKey,
                    StringComparison.Ordinal));
            if (existing is not null)
            {
                authority = LoadCertificate(existing.Value);
                return settings;
            }

            authority = CreateCertificateAuthority(_utcNow());
            var pfx = authority.Export(X509ContentType.Pfx);
            try
            {
                return [
                    ..settings,
                    new SupprocomSecretSetting(
                        CertificateAuthorityPfxKey,
                        Convert.ToBase64String(pfx)),
                ];
            }
            finally
            {
                CryptographicOperations.ZeroMemory(pfx);
            }
        }, cancellationToken);

        return authority!;
    }

    public async Task<RemoteRuntimeClientCertificate> IssueClientCertificateAsync(
        Guid pairId,
        CancellationToken cancellationToken = default)
    {
        var record = await FindAsync(pairId, cancellationToken)
            ?? throw new RemoteRuntimePairingException("PairNotFound", "The pairing record was not found.");
        if (!record.IsActive(_utcNow())
            || string.IsNullOrWhiteSpace(record.ProxyRuntimeCertificateSigningRequest)
            || string.IsNullOrWhiteSpace(record.ProxyRuntimePublicKeyHash))
        {
            throw new RemoteRuntimePairingException(
                "PairNotAuthorized",
                "The pairing must be active before certificate issue.");
        }

        var authority = await GetOrCreateCertificateAuthorityAsync(cancellationToken);
        var request = LoadSigningRequest(record.ProxyRuntimeCertificateSigningRequest);
        var publicKey = request.PublicKey.ExportSubjectPublicKeyInfo();
        var publicKeyHash = Base64UrlEncode(SHA256.HashData(publicKey));
        CryptographicOperations.ZeroMemory(publicKey);
        if (!string.Equals(
                publicKeyHash,
                record.ProxyRuntimePublicKeyHash,
                StringComparison.Ordinal))
        {
            throw new RemoteRuntimePairingException(
                "PairCredentialMismatch",
                "The pairing certificate request does not match the approved public key.");
        }

        var notBefore = _utcNow().AddMinutes(-1);
        var notAfter = notBefore.AddDays(30);
        var serial = RandomNumberGenerator.GetBytes(16);
        try
        {
            using var certificate = request.Create(
                authority,
                notBefore,
                notAfter,
                serial);
            return new RemoteRuntimeClientCertificate(
                certificate.Export(X509ContentType.Cert),
                publicKeyHash,
                certificate.Thumbprint ?? string.Empty,
                certificate.NotAfter.ToUniversalTime());
        }
        finally
        {
            CryptographicOperations.ZeroMemory(serial);
        }
    }

    public async Task<RemoteRuntimeClientCertificate> RenewClientCertificateAsync(
        Guid pairId,
        CancellationToken cancellationToken = default)
        => await IssueClientCertificateAsync(pairId, cancellationToken);

    public async Task<RemoteRuntimePairingRecord> ApproveClaimAsync(
        Guid pairId,
        string expectedProxyRuntimeInstanceId,
        string expectedAuthoritativeRuntimeInstanceId,
        CancellationToken cancellationToken = default)
    {
        RequireText(expectedProxyRuntimeInstanceId, nameof(expectedProxyRuntimeInstanceId));
        RequireText(expectedAuthoritativeRuntimeInstanceId, nameof(expectedAuthoritativeRuntimeInstanceId));

        RemoteRuntimePairingRecord? approved = null;
        await _documentUpdater.UpdateDocumentAsync(settings =>
        {
            var records = ReadRecords(settings);
            var record = FindRecord(records, pairId);
            var now = _utcNow();
            RequireStatus(record, RemoteRuntimePairStatus.ClaimPending, now);
            if (!string.Equals(
                    record.ProxyRuntimeInstanceId,
                    expectedProxyRuntimeInstanceId,
                    StringComparison.Ordinal)
                || !string.Equals(
                    record.AuthoritativeRuntimeInstanceId,
                    expectedAuthoritativeRuntimeInstanceId,
                    StringComparison.Ordinal))
            {
                throw new RemoteRuntimePairingException(
                    "PairTargetMismatch",
                    "The pairing claim does not match the selected Runtime target.");
            }

            approved = record with
            {
                Status = RemoteRuntimePairStatus.Active,
                ApprovedAtUtc = now,
            };
            return ReplaceRecord(settings, approved);
        }, cancellationToken);

        return approved!;
    }

    public async Task<RemoteRuntimePairingRecord> RevokeAsync(
        Guid pairId,
        CancellationToken cancellationToken = default)
    {
        RemoteRuntimePairingRecord? revoked = null;
        await _documentUpdater.UpdateDocumentAsync(settings =>
        {
            var records = ReadRecords(settings);
            var record = FindRecord(records, pairId);
            if (record.Status is RemoteRuntimePairStatus.Revoked)
                throw new RemoteRuntimePairingException("AlreadyRevoked", "The pairing is already revoked.");

            revoked = record with
            {
                Status = RemoteRuntimePairStatus.Revoked,
                RevokedAtUtc = _utcNow(),
            };
            return ReplaceRecord(settings, revoked);
        }, cancellationToken);

        return revoked!;
    }

    public async Task<RemoteRuntimePairingRecord?> FindAsync(
        Guid pairId,
        CancellationToken cancellationToken = default)
    {
        var records = await ReadRecordsAsync(cancellationToken);
        return records.FirstOrDefault(record => record.PairId == pairId);
    }

    public async Task<RemoteRuntimePairingRecord> RequireActiveAsync(
        Guid pairId,
        string gatewayInstanceId,
        string authoritativeRuntimeInstanceId,
        string proxyRuntimeInstanceId,
        CancellationToken cancellationToken = default)
    {
        RequireText(gatewayInstanceId, nameof(gatewayInstanceId));
        RequireText(authoritativeRuntimeInstanceId, nameof(authoritativeRuntimeInstanceId));
        RequireText(proxyRuntimeInstanceId, nameof(proxyRuntimeInstanceId));

        var record = await FindAsync(pairId, cancellationToken)
            ?? throw new RemoteRuntimePairingException("PairNotFound", "The pairing record was not found.");
        var now = _utcNow();
        if (!record.IsActive(now)
            || !string.Equals(record.GatewayInstanceId, gatewayInstanceId, StringComparison.Ordinal)
            || !string.Equals(
                record.AuthoritativeRuntimeInstanceId,
                authoritativeRuntimeInstanceId,
                StringComparison.Ordinal)
            || !string.Equals(
                record.ProxyRuntimeInstanceId,
                proxyRuntimeInstanceId,
                StringComparison.Ordinal))
        {
            throw new RemoteRuntimePairingException(
                "PairNotAuthorized",
                "The pairing is not active for this Runtime target.");
        }

        return record;
    }

    private async Task AddRecordAsync(
        RemoteRuntimePairingRecord record,
        CancellationToken cancellationToken)
    {
        await _documentUpdater.UpdateDocumentAsync(settings =>
        {
            var records = ReadRecords(settings);
            if (records.Any(existing => existing.PairId == record.PairId))
            {
                throw new RemoteRuntimePairingException(
                    "PairAlreadyExists",
                    "The pairing identifier already exists.");
            }

            return ReplaceRecords(settings, [.. records, record]);
        }, cancellationToken);
    }

    private async Task<IReadOnlyList<RemoteRuntimePairingRecord>> ReadRecordsAsync(
        CancellationToken cancellationToken)
    {
        var document = await _documentStore.ReadDocumentAsync(cancellationToken);
        return ReadRecords(SupprocomSecretDocument.Parse(document).Settings);
    }

    private static IReadOnlyList<RemoteRuntimePairingRecord> ReadRecords(
        IReadOnlyList<SupprocomSecretSetting> settings)
    {
        var groups = settings
            .Where(setting => setting.Key.StartsWith(PairPrefix, StringComparison.Ordinal))
            .GroupBy(setting => setting.Key[PairPrefix.Length..].Split(':')[0], StringComparer.Ordinal);
        var records = new List<RemoteRuntimePairingRecord>();
        foreach (var group in groups)
        {
            if (!Guid.TryParse(group.Key, out var pairId))
                throw InvalidDocument();

            var values = group.ToDictionary(
                setting => setting.Key[(PairPrefix.Length + group.Key.Length + 1)..],
                setting => setting.Value,
                StringComparer.Ordinal);
            records.Add(new RemoteRuntimePairingRecord(
                pairId,
                ParseEnum<RemoteRuntimePairStatus>(values, "Status"),
                RequireValue(values, "GatewayInstanceId"),
                RequireValue(values, "GatewayServerPublicKeyHash"),
                RequireValue(values, "AuthoritativeRuntimeInstanceId"),
                RequireValue(values, "AuthoritativeRuntimeInstallFingerprint"),
                RequireValue(values, "InvitationHash"),
                ParseInt(values, "BridgeProtocolMajor"),
                ParseTimestamp(values, "IssuedAtUtc"),
                ParseTimestamp(values, "ExpiresAtUtc"),
                OptionalValue(values, "ProxyRuntimeInstanceId"),
                OptionalValue(values, "ProxyRuntimePublicKeyHash"),
                OptionalValue(values, "ProxyRuntimeCertificateSigningRequest"),
                OptionalTimestamp(values, "ClaimedAtUtc"),
                OptionalTimestamp(values, "ApprovedAtUtc"),
                OptionalTimestamp(values, "RevokedAtUtc")));
        }

        return records;
    }

    private static IReadOnlyList<SupprocomSecretSetting> ReplaceRecord(
        IReadOnlyList<SupprocomSecretSetting> settings,
        RemoteRuntimePairingRecord record)
        => ReplaceRecords(settings, [.. ReadRecords(settings).Where(existing => existing.PairId != record.PairId), record]);

    private static IReadOnlyList<SupprocomSecretSetting> ReplaceRecords(
        IReadOnlyList<SupprocomSecretSetting> settings,
        IReadOnlyList<RemoteRuntimePairingRecord> records)
    {
        var result = settings
            .Where(setting => !setting.Key.StartsWith(PairPrefix, StringComparison.Ordinal))
            .ToList();
        foreach (var record in records.OrderBy(record => record.PairId))
        {
            var prefix = PairPrefix + record.PairId.ToString("D", CultureInfo.InvariantCulture);
            result.Add(new SupprocomSecretSetting(prefix + ":Status", record.Status.ToString()));
            result.Add(new SupprocomSecretSetting(prefix + ":GatewayInstanceId", record.GatewayInstanceId));
            result.Add(new SupprocomSecretSetting(prefix + ":GatewayServerPublicKeyHash", record.GatewayServerPublicKeyHash));
            result.Add(new SupprocomSecretSetting(prefix + ":AuthoritativeRuntimeInstanceId", record.AuthoritativeRuntimeInstanceId));
            result.Add(new SupprocomSecretSetting(prefix + ":AuthoritativeRuntimeInstallFingerprint", record.AuthoritativeRuntimeInstallFingerprint));
            result.Add(new SupprocomSecretSetting(prefix + ":InvitationHash", record.InvitationHash));
            result.Add(new SupprocomSecretSetting(prefix + ":BridgeProtocolMajor", record.BridgeProtocolMajor.ToString(CultureInfo.InvariantCulture)));
            result.Add(new SupprocomSecretSetting(prefix + ":IssuedAtUtc", RemoteRuntimePairingRecord.FormatTimestamp(record.IssuedAtUtc)));
            result.Add(new SupprocomSecretSetting(prefix + ":ExpiresAtUtc", RemoteRuntimePairingRecord.FormatTimestamp(record.ExpiresAtUtc)));
            AddOptional(result, prefix, "ProxyRuntimeInstanceId", record.ProxyRuntimeInstanceId);
            AddOptional(result, prefix, "ProxyRuntimePublicKeyHash", record.ProxyRuntimePublicKeyHash);
            AddOptional(result, prefix, "ProxyRuntimeCertificateSigningRequest", record.ProxyRuntimeCertificateSigningRequest);
            AddOptionalTimestamp(result, prefix, "ClaimedAtUtc", record.ClaimedAtUtc);
            AddOptionalTimestamp(result, prefix, "ApprovedAtUtc", record.ApprovedAtUtc);
            AddOptionalTimestamp(result, prefix, "RevokedAtUtc", record.RevokedAtUtc);
        }

        return SupprocomSecretDocument.Parse(SupprocomSecretDocument.Serialize(result)).Settings;
    }

    private static RemoteRuntimePairingRecord FindRecord(
        IReadOnlyList<RemoteRuntimePairingRecord> records,
        Guid pairId)
        => records.FirstOrDefault(record => record.PairId == pairId)
            ?? throw new RemoteRuntimePairingException("PairNotFound", "The pairing record was not found.");

    private static void RequireStatus(
        RemoteRuntimePairingRecord record,
        RemoteRuntimePairStatus required,
        DateTimeOffset now)
    {
        if (record.GetEffectiveStatus(now) != required)
        {
            throw new RemoteRuntimePairingException(
                "InvalidPairState",
                "The pairing is not in the required state.");
        }
    }

    private static void RequireText(string value, string parameterName)
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

    private static CertificateRequest LoadSigningRequest(string encodedRequest)
    {
        var requestBytes = DecodeBase64(encodedRequest, "InvalidProof");
        try
        {
            return CertificateRequest.LoadSigningRequest(
                requestBytes,
                HashAlgorithmName.SHA256);
        }
        catch (CryptographicException)
        {
            throw new RemoteRuntimePairingException(
                "InvalidProof",
                "The pairing certificate request is invalid.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(requestBytes);
        }
    }

    private static X509Certificate2 CreateCertificateAuthority(DateTimeOffset now)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(
            "CN=SharpClaw Remote Runtime Bridge CA",
            key,
            HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(
                X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign,
                true));
        request.CertificateExtensions.Add(
            new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        return request.CreateSelfSigned(
            now.AddMinutes(-1),
            now.AddYears(10));
    }

    private static X509Certificate2 LoadCertificate(string encodedPfx)
    {
        var pfx = DecodeBase64(encodedPfx, "InvalidCertificateAuthority");
        try
        {
            return X509CertificateLoader.LoadPkcs12(
                pfx,
                (string?)null,
                X509KeyStorageFlags.EphemeralKeySet | X509KeyStorageFlags.Exportable);
        }
        catch (CryptographicException)
        {
            throw new RemoteRuntimePairingException(
                "InvalidCertificateAuthority",
                "The protected certificate authority state is invalid.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pfx);
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
            throw new RemoteRuntimePairingException(
                errorCode,
                "The pairing credential encoding is invalid.");
        }
    }

    private static byte[] CreateClaimProofPayload(
        RemoteRuntimePairingRecord record,
        string secret,
        string proxyRuntimeInstanceId,
        string proxyRuntimePublicKeyHash)
        => Encoding.UTF8.GetBytes(
            string.Join(
                '|',
                record.PairId.ToString("D", CultureInfo.InvariantCulture),
                record.GatewayInstanceId,
                record.AuthoritativeRuntimeInstanceId,
                proxyRuntimeInstanceId,
                proxyRuntimePublicKeyHash,
                secret));

    private static RemoteRuntimePairingException InvalidDocument()
        => new("InvalidPairingDocument", "The protected pairing document is invalid.");

    private static string RequireValue(IReadOnlyDictionary<string, string> values, string key)
    {
        if (!values.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
            throw InvalidDocument();
        return value;
    }

    private static string? OptionalValue(IReadOnlyDictionary<string, string> values, string key)
        => values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;

    private static int ParseInt(IReadOnlyDictionary<string, string> values, string key)
        => int.TryParse(RequireValue(values, key), NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw InvalidDocument();

    private static T ParseEnum<T>(IReadOnlyDictionary<string, string> values, string key)
        where T : struct
        => Enum.TryParse<T>(RequireValue(values, key), ignoreCase: false, out var value)
            ? value
            : throw InvalidDocument();

    private static DateTimeOffset ParseTimestamp(
        IReadOnlyDictionary<string, string> values,
        string key)
        => DateTimeOffset.TryParse(
            RequireValue(values, key),
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out var value)
            ? value
            : throw InvalidDocument();

    private static DateTimeOffset? OptionalTimestamp(
        IReadOnlyDictionary<string, string> values,
        string key)
        => values.ContainsKey(key)
            ? ParseTimestamp(values, key)
            : null;

    private static void AddOptional(
        ICollection<SupprocomSecretSetting> settings,
        string prefix,
        string key,
        string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            settings.Add(new SupprocomSecretSetting(prefix + ":" + key, value));
    }

    private static void AddOptionalTimestamp(
        ICollection<SupprocomSecretSetting> settings,
        string prefix,
        string key,
        DateTimeOffset? value)
        => AddOptional(
            settings,
            prefix,
            key,
            value is { } timestamp
                ? RemoteRuntimePairingRecord.FormatTimestamp(timestamp)
                : null);
}
