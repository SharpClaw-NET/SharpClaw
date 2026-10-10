using System.Text.Json;
using System.Text.Json.Serialization;
using SharpClaw.Configuration;
using SharpClaw.Shared.Instances;
using SharpClaw.Shared.Logging;

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

        var installedRoot = ResolveInstalledFrontendRoot(
            AppContext.BaseDirectory,
            string.IsNullOrWhiteSpace(sharedRootOverride)
                ? SharpClawAppDataPaths.GetSharpClawRootDirectory()
                : Path.GetFullPath(sharedRootOverride),
            OperatingSystem.IsWindows());
        if (string.IsNullOrWhiteSpace(resolvedInstanceRoot) && installedRoot is not null)
        {
            resolvedInstanceRoot = installedRoot;
            // MSIX's physical directory contains the version. Use a stable identity
            // anchor so an upgrade retains instance IDs, keys, configuration, and data.
            installAnchorOverride ??= installedRoot;
        }

        Paths = new SharpClawInstancePaths(
            SharpClawInstanceKind.Frontend,
            resolvedInstanceRoot,
            sharedRootOverride,
            installAnchorOverride);
        Paths.EnsureDirectories();
        _ = Paths.Manifest;
    }

    public SharpClawInstancePaths Paths { get; }

    internal static string? ResolveInstalledFrontendRoot(string installDirectory, string sharedRoot, bool isWindows)
        => isWindows && File.Exists(Path.Combine(installDirectory, "sharpclaw-installation.json"))
            ? Path.Combine(sharedRoot, "installed", "com.mkn8rn.SharpClaw", "frontend")
            : null;

    public string BundledBackendInstanceRoot => Path.Combine(Paths.InstanceRoot, "stack", "backend");

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1054",
        Justification = "This existing string contract carries editable or persisted endpoint text, including bind addresses; retaining its exact representation and null-literal source compatibility is required. URI construction happens at the HTTP boundary.")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1055",
        Justification = "This existing string contract carries editable or persisted endpoint text, including bind addresses; retaining its exact representation and null-literal source compatibility is required. URI construction happens at the HTTP boundary.")]
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

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1054",
        Justification = "This existing string contract carries editable or persisted endpoint text, including bind addresses; retaining its exact representation and null-literal source compatibility is required. URI construction happens at the HTTP boundary.")]
    public string? ResolveBackendApiKeyPath(string? requestedBaseUrl = null)
    {
        var entry = string.IsNullOrWhiteSpace(requestedBaseUrl)
            ? ResolveSelectedBackendDiscoveryEntry()
            : ResolveBackendDiscoveryEntryForTarget(requestedBaseUrl);
        if (entry is null)
            return null;

        if (string.IsNullOrWhiteSpace(requestedBaseUrl))
            RememberBackendBinding(entry.InstanceId, entry.BaseUrl, "discovered");
        return entry.ApiKeyFilePath;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1054",
        Justification = "This existing string contract carries editable or persisted endpoint text, including bind addresses; retaining its exact representation and null-literal source compatibility is required. URI construction happens at the HTTP boundary.")]
    public string? ResolveBackendGatewayTokenPath(string? requestedBaseUrl = null)
    {
        var entry = string.IsNullOrWhiteSpace(requestedBaseUrl)
            ? ResolveSelectedBackendDiscoveryEntry()
            : ResolveBackendDiscoveryEntryForTarget(requestedBaseUrl);
        if (entry is null)
            return null;

        if (string.IsNullOrWhiteSpace(requestedBaseUrl))
            RememberBackendBinding(entry.InstanceId, entry.BaseUrl, "discovered");
        return entry.GatewayTokenFilePath;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1054",
        Justification = "This existing string contract carries editable or persisted endpoint text, including bind addresses; retaining its exact representation and null-literal source compatibility is required. URI construction happens at the HTTP boundary.")]
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

    private SharpClawDiscoveryEntry? ResolveSelectedBackendDiscoveryEntry()
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

        return entries.Count == 1 ? entries[0] : null;
    }

    private SharpClawDiscoveryEntry? ResolveBackendDiscoveryEntryForTarget(string requestedBaseUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestedBaseUrl);
        if (!Uri.TryCreate(requestedBaseUrl, UriKind.Absolute, out var requestedUri))
            return null;

        return EnumerateBackendDiscoveryEntries().FirstOrDefault(entry =>
            Uri.TryCreate(entry.BaseUrl, UriKind.Absolute, out var entryUri) &&
            BaseUrisMatch(requestedUri, entryUri));
    }

    private static bool BaseUrisMatch(Uri requested, Uri discovered) =>
        string.Equals(requested.Scheme, discovered.Scheme, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(requested.IdnHost, discovered.IdnHost, StringComparison.OrdinalIgnoreCase) &&
        requested.Port == discovered.Port &&
        string.Equals(requested.UserInfo, discovered.UserInfo, StringComparison.Ordinal) &&
        string.Equals(NormalizeBasePath(requested), NormalizeBasePath(discovered), StringComparison.Ordinal) &&
        string.Equals(requested.Query, discovered.Query, StringComparison.Ordinal);

    private static string NormalizeBasePath(Uri uri) =>
        uri.AbsolutePath.Length > 1
            ? uri.AbsolutePath.TrimEnd('/')
            : uri.AbsolutePath;

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
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                continue;
            }

            if (entry is not null)
                yield return entry;
        }
    }
}
