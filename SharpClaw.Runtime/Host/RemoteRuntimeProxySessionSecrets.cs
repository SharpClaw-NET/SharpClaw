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
    private const string ClientCertificatePfxKey = Prefix + "ClientCertificatePfx";
    private const string CertificateNotAfterUtcKey = Prefix + "CertificateNotAfterUtc";

    private readonly ISecretDocumentStore _documentStore;
    private readonly ISecretDocumentUpdater _documentUpdater;

    private RemoteRuntimeProxySessionSecrets(
        ISecretDocumentStore documentStore,
        ISecretDocumentUpdater documentUpdater)
    {
        _documentStore = documentStore ?? throw new ArgumentNullException(nameof(documentStore));
        _documentUpdater = documentUpdater ?? throw new ArgumentNullException(nameof(documentUpdater));
    }

    public static RemoteRuntimeProxySessionSecrets Create(SharpClawInstancePaths instancePaths)
    {
        ArgumentNullException.ThrowIfNull(instancePaths);
        var environmentDirectory = Path.Combine(
            Path.GetDirectoryName(typeof(LocalEnvironment).Assembly.Location)!,
            "Environment");
        return Create(environmentDirectory, instancePaths);
    }

    internal static RemoteRuntimeProxySessionSecrets Create(
        string environmentDirectory,
        SharpClawInstancePaths instancePaths)
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
        return new RemoteRuntimeProxySessionSecrets(store, store);
    }

    public async Task SaveAsync(
        RemoteRuntimeProxySessionState state,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        ValidateState(state);
        var sessionSettings = ToSettings(state);
        await _documentUpdater.UpdateDocumentAsync(
            settings =>
            [
                ..settings.Where(setting => !setting.Key.StartsWith(Prefix, StringComparison.Ordinal)),
                ..sessionSettings,
            ],
            cancellationToken);
    }

    public async Task<RemoteRuntimeProxySessionState?> ReadAsync(
        CancellationToken cancellationToken = default)
    {
        var document = await _documentStore.ReadDocumentAsync(cancellationToken);
        var settings = SupprocomSecretDocument.Parse(document).Settings
            .Where(setting => setting.Key.StartsWith(Prefix, StringComparison.Ordinal))
            .ToDictionary(
                setting => setting.Key,
                setting => setting.Value,
                StringComparer.Ordinal);
        if (settings.Count == 0)
            return null;

        var state = new RemoteRuntimeProxySessionState(
            ParseGuid(settings, PairIdKey),
            RequireValue(settings, GatewayBridgeUrlKey),
            RequireValue(settings, GatewayServerPublicKeyHashKey),
            RequireValue(settings, AuthoritativeRuntimeInstanceIdKey),
            RequireValue(settings, ProxyRuntimeInstanceIdKey),
            RequireValue(settings, ClientCertificatePfxKey),
            ParseTimestamp(settings, CertificateNotAfterUtcKey));
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

    private static string RequireValue(
        IReadOnlyDictionary<string, string> values,
        string key)
        => values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw InvalidValue(key);

    private static InvalidOperationException InvalidValue(string key)
        => new($"The protected proxy session value '{key}' is invalid.");

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
