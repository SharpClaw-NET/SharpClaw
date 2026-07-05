using System.Text.Json;
using System.Text.Json.Serialization;
using SharpClaw.Configuration;
using SharpClaw.Utils.Instances;

namespace SharpClaw.Services;

/// <summary>
/// Resolves and owns frontend instance-scoped paths and manifest state.
/// </summary>
public sealed class FrontendInstanceService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters =
        {
            new JsonStringEnumConverter(),
        },
    };

    public FrontendInstanceService(
        string? explicitInstanceRoot = null,
        string? sharedRootOverride = null,
        string? installAnchorOverride = null)
    {
        var resolvedInstanceRoot = string.IsNullOrWhiteSpace(explicitInstanceRoot)
            ? Environment.GetEnvironmentVariable("SHARPCLAW_INSTANCE_ROOT")
            : explicitInstanceRoot;

        Paths = new SharpClawInstancePaths(
            SharpClawInstanceKind.Frontend,
            resolvedInstanceRoot,
            sharedRootOverride,
            installAnchorOverride);
        Paths.EnsureDirectories();
        _ = Paths.Manifest;
    }

    public SharpClawInstancePaths Paths { get; }

    public string AccountsPath => Path.Combine(Paths.ConfigDirectory, "accounts.json");

    public string ClientSettingsPath => Path.Combine(Paths.ConfigDirectory, "client-settings.json");

    public string UsersSettingsDirectory => Path.Combine(Paths.ConfigDirectory, "users");

    public string SetupMarkerPath => Path.Combine(Paths.ConfigDirectory, ".setup-complete");

    public string BundledBackendInstanceRoot => Path.Combine(Paths.InstanceRoot, "stack", "backend");

    public string ResolvePreferredBackendBaseUrl(string configuredBaseUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configuredBaseUrl);

        if (!string.Equals(configuredBaseUrl, LocalEnvironment.DefaultApiUrl, StringComparison.OrdinalIgnoreCase))
        {
            RememberBackendBinding(Paths.Manifest.SelectedBackendInstanceId, configuredBaseUrl, "configured");
            return configuredBaseUrl;
        }

        if (!string.IsNullOrWhiteSpace(Paths.Manifest.SelectedBackendBaseUrl))
            return Paths.Manifest.SelectedBackendBaseUrl!;

        var discovered = EnumerateBackendDiscoveryEntries().ToList();
        if (discovered.Count == 1)
        {
            var entry = discovered[0];
            RememberBackendBinding(entry.InstanceId, entry.BaseUrl, "discovered");
            return entry.BaseUrl;
        }

        return configuredBaseUrl;
    }

    public string? ResolveBackendApiKeyPath(string? requestedBaseUrl = null)
    {
        var entry = ResolveSelectedBackendDiscoveryEntry(requestedBaseUrl);
        if (entry is null)
            return null;

        RememberBackendBinding(entry.InstanceId, entry.BaseUrl, "discovered");
        return entry.ApiKeyFilePath;
    }

    public string? ResolveBackendGatewayTokenPath(string? requestedBaseUrl = null)
    {
        var entry = ResolveSelectedBackendDiscoveryEntry(requestedBaseUrl);
        if (entry is null)
            return null;

        RememberBackendBinding(entry.InstanceId, entry.BaseUrl, "discovered");
        return entry.GatewayTokenFilePath;
    }

    public void RememberBackendBinding(string? backendInstanceId, string? baseUrl, string bindingKind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bindingKind);

        if (string.IsNullOrWhiteSpace(baseUrl))
            return;

        var manifest = Paths.Manifest;
        var changed = false;

        if (!string.Equals(manifest.SelectedBackendInstanceId, backendInstanceId, StringComparison.Ordinal))
        {
            manifest.SelectedBackendInstanceId = backendInstanceId;
            changed = true;
        }

        if (!string.Equals(manifest.SelectedBackendBaseUrl, baseUrl, StringComparison.Ordinal))
        {
            manifest.SelectedBackendBaseUrl = baseUrl;
            changed = true;
        }

        if (!string.Equals(manifest.SelectedBackendBindingKind, bindingKind, StringComparison.Ordinal))
        {
            manifest.SelectedBackendBindingKind = bindingKind;
            changed = true;
        }

        if (changed)
            Paths.SaveManifest(manifest);
    }

    public string GetUserSettingsPath(Guid userId)
        => Path.Combine(UsersSettingsDirectory, userId.ToString("N"), "settings.json");

    private SharpClawDiscoveryEntry? ResolveSelectedBackendDiscoveryEntry(string? requestedBaseUrl)
    {
        var manifest = Paths.Manifest;
        var entries = EnumerateBackendDiscoveryEntries().ToList();

        if (!string.IsNullOrWhiteSpace(manifest.SelectedBackendInstanceId))
        {
            var byInstanceId = entries.FirstOrDefault(e => string.Equals(e.InstanceId, manifest.SelectedBackendInstanceId, StringComparison.Ordinal));
            if (byInstanceId is not null)
                return byInstanceId;
        }

        if (!string.IsNullOrWhiteSpace(manifest.SelectedBackendBaseUrl))
        {
            var byManifestUrl = entries.FirstOrDefault(e => string.Equals(e.BaseUrl, manifest.SelectedBackendBaseUrl, StringComparison.OrdinalIgnoreCase));
            if (byManifestUrl is not null)
                return byManifestUrl;
        }

        if (!string.IsNullOrWhiteSpace(requestedBaseUrl))
            return entries.FirstOrDefault(e => string.Equals(e.BaseUrl, requestedBaseUrl, StringComparison.OrdinalIgnoreCase));

        return entries.Count == 1 ? entries[0] : null;
    }

    private IEnumerable<SharpClawDiscoveryEntry> EnumerateBackendDiscoveryEntries()
    {
        var discoveryDirectory = Path.Combine(Paths.SharedRoot, "discovery", "instances");
        if (!Directory.Exists(discoveryDirectory))
            yield break;

        foreach (var filePath in Directory.EnumerateFiles(discoveryDirectory, "backend-*.json"))
        {
            SharpClawDiscoveryEntry? entry;
            try
            {
                using var stream = File.OpenRead(filePath);
                entry = JsonSerializer.Deserialize<SharpClawDiscoveryEntry>(stream, JsonOptions);
            }
            catch
            {
                continue;
            }

            if (entry is not null)
                yield return entry;
        }
    }
}
