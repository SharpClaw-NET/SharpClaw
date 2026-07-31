using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using SharpClaw.Runtime.INF.Configuration;
using SharpClaw.Shared.Instances;
using SharpClaw.Shared.RemoteRuntimeBridge;
using Supprocom.Secrets;

namespace SharpClaw.Runtime.Host;

internal sealed class RemoteRuntimeProxySessionSecrets
{
    private const string Prefix = "RemoteRuntime:Session:";
    private const string PairIdKey = Prefix + "PairId";
    private const string GatewayBridgeUrlKey = Prefix + "GatewayBridgeUrl";
    private const string GatewayServerPublicKeyHashKey = Prefix + "GatewayServerPublicKeyHash";
    private const string AuthoritativeRuntimeInstanceIdKey = Prefix + "AuthoritativeRuntimeInstanceId";
    private const string ProxyRuntimeInstanceIdKey = Prefix + "ProxyRuntimeInstanceId";
    private const string CertificateNotAfterUtcKey = Prefix + "CertificateNotAfterUtc";
    private const string AuthoritativeRuntimeInstallFingerprintKey = Prefix + "AuthoritativeRuntimeInstallFingerprint";
    private const string CertificateThumbprintKey = Prefix + "CertificateThumbprint";
    private const string BridgeProtocolMajorKey = Prefix + "BridgeProtocolMajor";

    private readonly ISecretDocumentStore _documentStore;
    private readonly ISecretDocumentUpdater _documentUpdater;
    private readonly string _privateKeySecret;
    private readonly string _clientCertificateSecret;

    private RemoteRuntimeProxySessionSecrets(
        ISecretDocumentStore documentStore,
        ISecretDocumentUpdater documentUpdater,
        string privateKeySecret,
        string clientCertificateSecret)
    {
        _documentStore = documentStore ?? throw new ArgumentNullException(nameof(documentStore));
        _documentUpdater = documentUpdater ?? throw new ArgumentNullException(nameof(documentUpdater));
        _privateKeySecret = RequireSelector(privateKeySecret, nameof(privateKeySecret));
        _clientCertificateSecret = RequireSelector(clientCertificateSecret, nameof(clientCertificateSecret));
        if (string.Equals(_privateKeySecret, _clientCertificateSecret, StringComparison.Ordinal))
            throw new ArgumentException("The proxy private-key and certificate selectors must differ.");
    }

    public static RemoteRuntimeProxySessionSecrets Create(SharpClawInstancePaths instancePaths)
    {
        ArgumentNullException.ThrowIfNull(instancePaths);
        var environmentDirectory = Path.Combine(
            Path.GetDirectoryName(typeof(LocalEnvironment).Assembly.Location)!,
            "Environment");
        return Create(
            environmentDirectory,
            instancePaths,
            Prefix + "PrivateKey",
            Prefix + "ClientCertificate");
    }

    public static RemoteRuntimeProxySessionSecrets Create(
        SharpClawInstancePaths instancePaths,
        string privateKeySecret,
        string clientCertificateSecret)
    {
        ArgumentNullException.ThrowIfNull(instancePaths);
        var environmentDirectory = Path.Combine(
            Path.GetDirectoryName(typeof(LocalEnvironment).Assembly.Location)!,
            "Environment");
        return Create(
            environmentDirectory,
            instancePaths,
            privateKeySecret,
            clientCertificateSecret);
    }

    internal static RemoteRuntimeProxySessionSecrets Create(
        string environmentDirectory,
        SharpClawInstancePaths instancePaths)
        => Create(
            environmentDirectory,
            instancePaths,
            Prefix + "PrivateKey",
            Prefix + "ClientCertificate");

    internal static RemoteRuntimeProxySessionSecrets Create(
        string environmentDirectory,
        SharpClawInstancePaths instancePaths,
        string privateKeySecret,
        string clientCertificateSecret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(environmentDirectory);
        ArgumentNullException.ThrowIfNull(instancePaths);
        instancePaths.EnsureDirectories();
        Directory.CreateDirectory(environmentDirectory);

        var options = LocalEnvironment.CreateSecretsOptions(
            environmentDirectory,
            isDevelopment: false,
            instancePaths);
        var store = new SupprocomSecretFileStore(options);
        return new RemoteRuntimeProxySessionSecrets(
            store,
            store,
            privateKeySecret,
            clientCertificateSecret);
    }

    public async Task SaveAsync(
        RemoteRuntimeProxySessionState state,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        ValidateState(state, requireUnexpired: true);
        var pfxBytes = Convert.FromBase64String(state.ClientCertificatePfxBase64);
        try
        {
            using var certificate = X509CertificateLoader.LoadPkcs12(
                pfxBytes,
                password: null,
                keyStorageFlags: X509KeyStorageFlags.EphemeralKeySet | X509KeyStorageFlags.Exportable);
            using var privateKey = certificate.GetECDsaPrivateKey()
                ?? throw new InvalidOperationException(
                    "The proxy client certificate does not contain an ECDSA private key.");
            var privateKeyBytes = privateKey.ExportPkcs8PrivateKey();
            try
            {
                await SaveAsync(state, privateKeyBytes, cancellationToken);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(privateKeyBytes);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pfxBytes);
        }
    }

    public async Task SaveAsync(
        RemoteRuntimeProxySessionState state,
        ReadOnlyMemory<byte> privateKeyPkcs8,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (privateKeyPkcs8.IsEmpty)
            throw new ArgumentException("The proxy private key must not be empty.", nameof(privateKeyPkcs8));
        ValidateState(state, requireUnexpired: true);
        var sessionSettings = ToSettings(state);
        var privateKeyBase64 = Convert.ToBase64String(privateKeyPkcs8.Span);
        await _documentUpdater.UpdateDocumentAsync(
            settings =>
            [
                ..settings.Where(setting =>
                    !setting.Key.StartsWith(Prefix, StringComparison.Ordinal)
                    && !string.Equals(setting.Key, _privateKeySecret, StringComparison.Ordinal)
                    && !string.Equals(setting.Key, _clientCertificateSecret, StringComparison.Ordinal)),
                new(_privateKeySecret, privateKeyBase64),
                new(_clientCertificateSecret, state.ClientCertificatePfxBase64),
                ..sessionSettings,
            ],
            cancellationToken);
    }

    public async Task<RemoteRuntimeProxySessionState?> ReadAsync(
        CancellationToken cancellationToken = default)
    {
        var document = await _documentStore.ReadDocumentAsync(cancellationToken);
        var settings = SupprocomSecretDocument.Parse(document).Settings
            .ToDictionary(
                setting => setting.Key,
                setting => setting.Value,
                StringComparer.Ordinal);
        if (!settings.ContainsKey(PairIdKey)
            && !settings.ContainsKey(_privateKeySecret)
            && !settings.ContainsKey(_clientCertificateSecret))
            return null;

        _ = RequireValue(settings, _privateKeySecret);

        var state = new RemoteRuntimeProxySessionState(
            ParseGuid(settings, PairIdKey),
            RequireValue(settings, GatewayBridgeUrlKey),
            RequireValue(settings, GatewayServerPublicKeyHashKey),
            RequireValue(settings, AuthoritativeRuntimeInstanceIdKey),
            RequireValue(settings, ProxyRuntimeInstanceIdKey),
            RequireValue(settings, _clientCertificateSecret),
            ParseTimestamp(settings, CertificateNotAfterUtcKey),
            RequireValue(settings, AuthoritativeRuntimeInstallFingerprintKey),
            RequireValue(settings, CertificateThumbprintKey),
            ParseProtocolMajor(settings, BridgeProtocolMajorKey));
        ValidateState(state, requireUnexpired: false);
        return state;
    }

    public async Task<ECDsa> LoadPrivateKeyAsync(
        RemoteRuntimeProxySessionState state,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        cancellationToken.ThrowIfCancellationRequested();
        var document = await _documentStore.ReadDocumentAsync(cancellationToken);
        var settings = SupprocomSecretDocument.Parse(document).Settings
            .ToDictionary(setting => setting.Key, setting => setting.Value, StringComparer.Ordinal);
        var encoded = RequireValue(settings, _privateKeySecret);
        var bytes = Convert.FromBase64String(encoded);
        try
        {
            var key = ECDsa.Create();
            try
            {
                key.ImportPkcs8PrivateKey(bytes, out _);
                return key;
            }
            catch
            {
                key.Dispose();
                throw new InvalidOperationException("The protected proxy private key is invalid.");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public async Task<X509Certificate2> LoadClientCertificateAsync(
        RemoteRuntimeProxySessionState state,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        ValidateState(state, requireUnexpired: false);
        cancellationToken.ThrowIfCancellationRequested();

        var pfxBytes = Convert.FromBase64String(state.ClientCertificatePfxBase64);
        try
        {
            return X509CertificateLoader.LoadPkcs12(
                pfxBytes,
                password: null,
                keyStorageFlags: X509KeyStorageFlags.UserKeySet
                    | X509KeyStorageFlags.PersistKeySet
                    | X509KeyStorageFlags.Exportable);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pfxBytes);
        }
    }

    private static IReadOnlyList<SupprocomSecretSetting> ToSettings(
        RemoteRuntimeProxySessionState state)
        =>
        [
            new(PairIdKey, state.PairId.ToString("D", CultureInfo.InvariantCulture)),
            new(GatewayBridgeUrlKey, state.GatewayBridgeUrl),
            new(GatewayServerPublicKeyHashKey, state.GatewayServerPublicKeyHash),
            new(AuthoritativeRuntimeInstanceIdKey, state.AuthoritativeRuntimeInstanceId),
            new(ProxyRuntimeInstanceIdKey, state.ProxyRuntimeInstanceId),
            new(CertificateNotAfterUtcKey, state.CertificateNotAfterUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)),
            new(AuthoritativeRuntimeInstallFingerprintKey, state.AuthoritativeRuntimeInstallFingerprint ?? string.Empty),
            new(CertificateThumbprintKey, state.CertificateThumbprint ?? string.Empty),
            new(BridgeProtocolMajorKey, state.BridgeProtocolMajor.ToString(CultureInfo.InvariantCulture)),
        ];

    private static Guid ParseGuid(
        IReadOnlyDictionary<string, string> values,
        string key)
        => Guid.TryParse(RequireValue(values, key), out var value)
            ? value
            : throw InvalidValue(key);

    private static DateTimeOffset ParseTimestamp(
        IReadOnlyDictionary<string, string> values,
        string key)
        => DateTimeOffset.TryParse(
                RequireValue(values, key),
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var value)
            ? value
            : throw InvalidValue(key);

    private static int ParseProtocolMajor(
        IReadOnlyDictionary<string, string> values,
        string key)
        => int.TryParse(
                RequireValue(values, key),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var value)
            && value == RemoteRuntimeBridgePaths.CurrentProtocolMajor
            ? value
            : throw InvalidValue(key);

    private static string RequireValue(
        IReadOnlyDictionary<string, string> values,
        string key)
        => values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw InvalidValue(key);

    private static InvalidOperationException InvalidValue(string key)
        => new($"The protected proxy session value '{key}' is invalid.");

    private static void ValidateState(
        RemoteRuntimeProxySessionState state,
        bool requireUnexpired)
    {
        if (state.PairId == Guid.Empty)
            throw new InvalidOperationException("The protected proxy session pair identifier is invalid.");

        if (!Uri.TryCreate(state.GatewayBridgeUrl, UriKind.Absolute, out var gatewayUri)
            || !string.Equals(gatewayUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The protected proxy session Gateway URL is invalid.");
        }

        RequireText(state.GatewayServerPublicKeyHash, nameof(state.GatewayServerPublicKeyHash));
        RequireText(state.AuthoritativeRuntimeInstanceId, nameof(state.AuthoritativeRuntimeInstanceId));
        RequireText(state.ProxyRuntimeInstanceId, nameof(state.ProxyRuntimeInstanceId));
        RequireText(state.AuthoritativeRuntimeInstallFingerprint, nameof(state.AuthoritativeRuntimeInstallFingerprint));
        RequireText(state.CertificateThumbprint, nameof(state.CertificateThumbprint));
        RequireText(state.ClientCertificatePfxBase64, nameof(state.ClientCertificatePfxBase64));
        if (state.BridgeProtocolMajor != RemoteRuntimeBridgePaths.CurrentProtocolMajor)
            throw new InvalidOperationException("The protected proxy session protocol major is unsupported.");
        if (requireUnexpired && state.CertificateNotAfterUtc <= DateTimeOffset.UtcNow)
            throw new InvalidOperationException("The protected proxy session certificate has expired.");
    }

    private static string RequireSelector(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("A protected secret selector is required.", name);
        return value.Trim();
    }

    private static void RequireText(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"The proxy session value '{name}' is required.", name);
    }
}
