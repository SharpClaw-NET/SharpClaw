using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SharpClaw.Utils.Logging;

namespace SharpClaw.Utils.Instances;

/// <summary>
/// Resolves install-scoped SharpClaw instance paths and manifests.
/// </summary>
public sealed class SharpClawInstancePaths
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters =
        {
            new JsonStringEnumConverter(),
        },
    };

    private readonly Lazy<SharpClawInstanceManifest> _manifest;

    public SharpClawInstancePaths(
        SharpClawInstanceKind instanceKind,
        string? explicitInstanceRoot = null,
        string? sharedRootOverride = null,
        string? installAnchorOverride = null)
    {
        InstanceKind = instanceKind;
        SharedRoot = string.IsNullOrWhiteSpace(sharedRootOverride)
            ? SharpClawAppDataPaths.GetSharpClawRootDirectory()
            : Path.GetFullPath(sharedRootOverride);
        InstallAnchor = string.IsNullOrWhiteSpace(installAnchorOverride)
            ? ResolveInstallAnchor()
            : Path.GetFullPath(installAnchorOverride);
        InstallFingerprint = ComputeInstallFingerprint(InstallAnchor);
        InstanceRoot = ResolveInstanceRoot(instanceKind, explicitInstanceRoot);
        ManifestPath = Path.Combine(InstanceRoot, "instance.json");
        DataDirectory = Path.Combine(InstanceRoot, "Data");
        SecretsDirectory = Path.Combine(InstanceRoot, "secrets");
        RuntimeDirectory = Path.Combine(InstanceRoot, "runtime");
        LogsDirectory = Path.Combine(InstanceRoot, "logs");
        ConfigDirectory = Path.Combine(InstanceRoot, "config");
        DiscoveryDirectory = Path.Combine(SharedRoot, "discovery", "instances");
        DiscoveryEntryPath = Path.Combine(DiscoveryDirectory, $"{instanceKind.ToString().ToLowerInvariant()}-{InstallFingerprint}.json");
        _manifest = new Lazy<SharpClawInstanceManifest>(LoadOrCreateManifest, isThreadSafe: true);
    }

    public SharpClawInstanceKind InstanceKind { get; }

    public string SharedRoot { get; }

    public string InstallAnchor { get; }

    public string InstallFingerprint { get; }

    public string InstanceRoot { get; }

    public string ManifestPath { get; }

    public string DataDirectory { get; }

    public string SecretsDirectory { get; }

    public string RuntimeDirectory { get; }

    public string LogsDirectory { get; }

    public string ConfigDirectory { get; }

    public string DiscoveryDirectory { get; }

    public string DiscoveryEntryPath { get; }

    public string ApiKeyFilePath => Path.Combine(RuntimeDirectory, ".api-key");

    public string GatewayTokenFilePath => Path.Combine(RuntimeDirectory, ".gateway-token");

    public SharpClawInstanceManifest Manifest => _manifest.Value;

    public string GetSecretFilePath(string keyName)
    {
        if (string.IsNullOrWhiteSpace(keyName))
            throw new ArgumentException("Key name is required.", nameof(keyName));

        return Path.Combine(SecretsDirectory, $".{keyName}");
    }

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(InstanceRoot);
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(SecretsDirectory);
        Directory.CreateDirectory(RuntimeDirectory);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(ConfigDirectory);
        Directory.CreateDirectory(DiscoveryDirectory);
    }

    public void SaveManifest(SharpClawInstanceManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        manifest.SchemaVersion = SharpClawInstanceManifest.CurrentSchemaVersion;
        manifest.InstanceKind = InstanceKind;
        manifest.InstallFingerprint = InstallFingerprint;
        manifest.InstanceRoot = InstanceRoot;
        manifest.UpdatedAtUtc = DateTimeOffset.UtcNow;

        EnsureDirectories();
        WriteJsonAtomically(ManifestPath, manifest);
    }

    public void PublishDiscoveryEntry(string baseUrl)
        => PublishDiscoveryEntry(
            baseUrl,
            Process.GetCurrentProcess().StartTime.ToUniversalTime(),
            Environment.ProcessId);

    /// <summary>
    /// Writes the current discovery entry with explicit process metadata.
    /// </summary>
    public void PublishDiscoveryEntry(string baseUrl, DateTimeOffset startedAtUtc, int processId)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
            throw new ArgumentException("Base URL is required.", nameof(baseUrl));

        if (processId <= 0)
            throw new ArgumentOutOfRangeException(nameof(processId), "Process ID must be greater than zero.");

        var entry = new SharpClawDiscoveryEntry
        {
            InstanceKind = InstanceKind,
            InstanceId = Manifest.InstanceId,
            InstallFingerprint = InstallFingerprint,
            InstanceRoot = InstanceRoot,
            BaseUrl = baseUrl,
            RuntimeDirectory = RuntimeDirectory,
            ApiKeyFilePath = ApiKeyFilePath,
            GatewayTokenFilePath = GatewayTokenFilePath,
            ProcessId = processId,
            StartedAtUtc = startedAtUtc,
            LastSeenUtc = DateTimeOffset.UtcNow,
        };

        EnsureDirectories();
        WriteJsonAtomically(DiscoveryEntryPath, entry);
    }

    /// <summary>
    /// Removes stale discovery entries for the current instance kind.
    /// </summary>
    public void CleanupStaleDiscoveryEntries(TimeSpan maxAge)
    {
        if (maxAge <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(maxAge), "Max age must be greater than zero.");

        EnsureDirectories();

        foreach (var discoveryEntryPath in Directory.EnumerateFiles(
                     DiscoveryDirectory,
                     $"{InstanceKind.ToString().ToLowerInvariant()}-*.json"))
        {
            if (!TryReadDiscoveryEntry(discoveryEntryPath, out var entry) || entry is null)
            {
                DeleteFileIfExists(discoveryEntryPath);
                continue;
            }

            if (IsDiscoveryEntryStale(entry, maxAge))
                DeleteFileIfExists(discoveryEntryPath);
        }
    }

    public void DeleteDiscoveryEntry()
    {
        try
        {
            if (File.Exists(DiscoveryEntryPath))
                File.Delete(DiscoveryEntryPath);
        }
        catch
        {
        }
    }

    public void DeleteRuntimeFiles()
    {
        DeleteFileIfExists(ApiKeyFilePath);
        DeleteFileIfExists(GatewayTokenFilePath);
    }

    private SharpClawInstanceManifest LoadOrCreateManifest()
    {
        EnsureDirectories();

        if (File.Exists(ManifestPath))
        {
            using var stream = File.OpenRead(ManifestPath);
            var manifest = JsonSerializer.Deserialize<SharpClawInstanceManifest>(stream, JsonOptions)
                ?? throw new InvalidOperationException($"Failed to read instance manifest '{ManifestPath}'.");

            if (manifest.InstanceKind != InstanceKind)
            {
                throw new InvalidOperationException(
                    $"Instance manifest '{ManifestPath}' is for kind '{manifest.InstanceKind}', not '{InstanceKind}'.");
            }

            manifest.InstanceRoot = InstanceRoot;
            manifest.InstallFingerprint = InstallFingerprint;
            manifest.DataDirectory = DataDirectory;
            return manifest;
        }

        var now = DateTimeOffset.UtcNow;
        var created = new SharpClawInstanceManifest
        {
            InstanceKind = InstanceKind,
            InstanceId = Guid.NewGuid().ToString("D"),
            InstallFingerprint = InstallFingerprint,
            InstanceRoot = InstanceRoot,
            DataDirectory = DataDirectory,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };

        SaveManifest(created);
        return created;
    }

    private string ResolveInstanceRoot(SharpClawInstanceKind instanceKind, string? explicitInstanceRoot)
    {
        if (!string.IsNullOrWhiteSpace(explicitInstanceRoot))
            return Path.GetFullPath(explicitInstanceRoot);

        var instanceKindDirectory = instanceKind.ToString().ToLowerInvariant();
        return Path.Combine(SharedRoot, "instances", instanceKindDirectory, InstallFingerprint);
    }

    private static string ResolveInstallAnchor()
    {
        return Path.GetFullPath(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
    }

    private static string ComputeInstallFingerprint(string installAnchor)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(installAnchor));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static void WriteJsonAtomically<T>(string path, T value)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException($"Could not resolve directory for '{path}'.");
        Directory.CreateDirectory(directory);

        var tempPath = path + ".tmp";
        var json = JsonSerializer.Serialize(value, JsonOptions);
        File.WriteAllText(tempPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        if (File.Exists(path))
            File.Delete(path);

        File.Move(tempPath, path);
    }

    private static void DeleteFileIfExists(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
        }
    }

    private bool TryReadDiscoveryEntry(string discoveryEntryPath, out SharpClawDiscoveryEntry? entry)
    {
        try
        {
            using var stream = File.OpenRead(discoveryEntryPath);
            entry = JsonSerializer.Deserialize<SharpClawDiscoveryEntry>(stream, JsonOptions);
            return entry is not null;
        }
        catch
        {
            entry = null;
            return false;
        }
    }

    private static bool IsDiscoveryEntryStale(SharpClawDiscoveryEntry entry, TimeSpan maxAge)
    {
        if (string.IsNullOrWhiteSpace(entry.InstanceRoot))
            return true;

        if (DateTimeOffset.UtcNow - entry.LastSeenUtc > maxAge)
            return true;

        if (!File.Exists(Path.Combine(entry.InstanceRoot, "instance.json")))
            return true;

        return entry.ProcessId > 0 && !IsProcessAlive(entry.ProcessId);
    }

    private static bool IsProcessAlive(int processId)
    {
        try
        {
            var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
