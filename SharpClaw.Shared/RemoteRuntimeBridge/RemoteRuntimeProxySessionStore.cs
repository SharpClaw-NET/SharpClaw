using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using SharpClaw.Shared.Instances;
using SharpClaw.Shared.Security;
using Supprocom.Secrets;

namespace SharpClaw.Shared.RemoteRuntimeBridge;

public sealed record RemoteRuntimeProxySessionState(
    Guid PairId,
    string GatewayBridgeUrl,
    string GatewayServerPublicKeyHash,
    string AuthoritativeRuntimeInstanceId,
    string ProxyRuntimeInstanceId,
    string ClientCertificatePfxBase64,
    DateTimeOffset CertificateNotAfterUtc);

public sealed class RemoteRuntimeProxySessionStore
{
    private const string PairIdKey = "PairId";
    private const string GatewayBridgeUrlKey = "GatewayBridgeUrl";
    private const string GatewayServerPublicKeyHashKey = "GatewayServerPublicKeyHash";
    private const string AuthoritativeRuntimeInstanceIdKey = "AuthoritativeRuntimeInstanceId";
    private const string ProxyRuntimeInstanceIdKey = "ProxyRuntimeInstanceId";
    private const string ClientCertificatePfxKey = "ClientCertificatePfx";
    private const string CertificateNotAfterUtcKey = "CertificateNotAfterUtc";

    private readonly ISecretDocumentStore _documentStore;
    private readonly ISecretDocumentUpdater _documentUpdater;

    private RemoteRuntimeProxySessionStore(
        ISecretDocumentStore documentStore,
        ISecretDocumentUpdater documentUpdater)
    {
        _documentStore = documentStore ?? throw new ArgumentNullException(nameof(documentStore));
        _documentUpdater = documentUpdater ?? throw new ArgumentNullException(nameof(documentUpdater));
    }

    public static RemoteRuntimeProxySessionStore Create(SharpClawInstancePaths instancePaths)
    {
        ArgumentNullException.ThrowIfNull(instancePaths);
        instancePaths.EnsureDirectories();

        var directory = instancePaths.RemoteRuntimeProxyStateDirectory;
        Directory.CreateDirectory(directory);
        var templatePath = Path.Combine(directory, ".env.template");
        if (!File.Exists(templatePath))
            File.WriteAllText(
                templatePath,
                "# Remote Runtime proxy session state is written after pairing.\n");

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
        return new RemoteRuntimeProxySessionStore(store, store);
    }

    public async Task SaveAsync(
        RemoteRuntimeProxySessionState state,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        ValidateState(state);
        var settings = ToSettings(state);
        await _documentUpdater.UpdateDocumentAsync(_ => settings, cancellationToken);
    }

    public async Task<RemoteRuntimeProxySessionState?> ReadAsync(
        CancellationToken cancellationToken = default)
    {
        var rawDocument = await _documentStore.ReadDocumentAsync(cancellationToken);
        var settings = SupprocomSecretDocument.Parse(rawDocument).Settings;
        if (settings.Count == 0)
            return null;

        var values = settings.ToDictionary(
            setting => setting.Key,
            setting => setting.Value,
            StringComparer.Ordinal);
        var state = new RemoteRuntimeProxySessionState(
            ParseGuid(values, PairIdKey),
            RequireValue(values, GatewayBridgeUrlKey),
            RequireValue(values, GatewayServerPublicKeyHashKey),
            RequireValue(values, AuthoritativeRuntimeInstanceIdKey),
            RequireValue(values, ProxyRuntimeInstanceIdKey),
            RequireValue(values, ClientCertificatePfxKey),
            ParseTimestamp(values, CertificateNotAfterUtcKey));
        ValidateState(state);
        return state;
    }

    public async Task<X509Certificate2> LoadClientCertificateAsync(
        RemoteRuntimeProxySessionState state,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        ValidateState(state);
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
            new(ClientCertificatePfxKey, state.ClientCertificatePfxBase64),
            new(CertificateNotAfterUtcKey, state.CertificateNotAfterUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)),
        ];

    private static Guid ParseGuid(
        IReadOnlyDictionary<string, string> values,
        string key)
        => Guid.TryParse(RequireValue(values, key), out var value)
            ? value
            : throw new InvalidOperationException($"The protected proxy session value '{key}' is invalid.");

    private static DateTimeOffset ParseTimestamp(
        IReadOnlyDictionary<string, string> values,
        string key)
        => DateTimeOffset.TryParse(
                RequireValue(values, key),
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var value)
            ? value
            : throw new InvalidOperationException($"The protected proxy session value '{key}' is invalid.");

    private static string RequireValue(
        IReadOnlyDictionary<string, string> values,
        string key)
        => values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidOperationException($"The protected proxy session value '{key}' is missing.");

    private static void ValidateState(RemoteRuntimeProxySessionState state)
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
        RequireText(state.ClientCertificatePfxBase64, nameof(state.ClientCertificatePfxBase64));
        if (state.CertificateNotAfterUtc <= DateTimeOffset.UtcNow)
            throw new InvalidOperationException("The protected proxy session certificate has expired.");
    }

    private static void RequireText(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"The proxy session value '{name}' is required.", name);
    }
}
