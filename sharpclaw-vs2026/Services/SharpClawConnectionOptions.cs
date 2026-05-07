using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using SharpClaw.Utils.Logging;

namespace SharpClaw.VS2026Extension.Services;

/// <summary>
/// Persisted connection preferences for the VS2026 extension. Values are kept
/// deliberately small and local: discovery remains the default, while every
/// override here is opt-in.
/// </summary>
internal sealed class SharpClawConnectionOptions
{
    public string? SelectedInstanceId { get; set; }
    public string? BaseUrlOverride { get; set; }
    public int? PortOverride { get; set; }
    public string? ApiKeyOverride { get; set; }
    public string? ApiKeyFileOverride { get; set; }
    public string? GatewayTokenOverride { get; set; }
    public string? GatewayTokenFileOverride { get; set; }
    public bool PreferAliveInstances { get; set; } = true;
}

internal sealed class SharpClawResolvedConnection
{
    public SharpClawDiscoveryEntry? Entry { get; init; }
    public SharpClawHttpClient Client { get; init; } = null!;
    public string BaseUrl { get; init; } = string.Empty;
    public string SelectionSummary { get; init; } = string.Empty;
    public string ApiKeySource { get; init; } = string.Empty;
    public string GatewayTokenSource { get; init; } = string.Empty;
}

/// <summary>
/// Loads/saves the VS2026 connection options and resolves them against the
/// current discovery snapshot.
/// </summary>
internal sealed class SharpClawConnectionOptionsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly object _gate = new();

    public string OptionsPath { get; } = Path.Combine(
        SharpClawAppDataPaths.GetSharpClawRootDirectory(),
        "config",
        "vs2026-extension-options.json");

    public SharpClawConnectionOptions Load()
    {
        lock (_gate)
        {
            if (!File.Exists(OptionsPath))
                return new SharpClawConnectionOptions();

            try
            {
                var json = File.ReadAllText(OptionsPath);
                return JsonSerializer.Deserialize<SharpClawConnectionOptions>(json, JsonOptions)
                    ?? new SharpClawConnectionOptions();
            }
            catch
            {
                return new SharpClawConnectionOptions();
            }
        }
    }

    public void Save(SharpClawConnectionOptions options)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(OptionsPath)!);
            File.WriteAllText(OptionsPath, JsonSerializer.Serialize(options, JsonOptions));
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            if (File.Exists(OptionsPath))
                File.Delete(OptionsPath);
        }
    }

    public IReadOnlyList<SharpClawDiscoveryEntry> EnumerateDetectedInstances(bool preferAlive)
    {
        var entries = SharpClawDiscovery.EnumerateRanked();
        if (preferAlive)
            return entries;

        return entries
            .OrderByDescending(e => e.LastSeenUtc ?? e.StartedAtUtc ?? e.SourceWriteUtc)
            .ThenByDescending(e => e.IsAlive)
            .ToList();
    }

    public SharpClawDiscoveryEntry? SelectEntry(
        SharpClawConnectionOptions options,
        IReadOnlyList<SharpClawDiscoveryEntry> entries)
    {
        var selectedId = TrimToNull(options.SelectedInstanceId);
        if (selectedId is not null)
        {
            var match = entries.FirstOrDefault(e =>
                string.Equals(EntryKey(e), selectedId, StringComparison.OrdinalIgnoreCase)
                || string.Equals(e.InstanceId, selectedId, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
                return match;
        }

        return entries.FirstOrDefault();
    }

    public bool HasManualEndpoint(SharpClawConnectionOptions options)
        => TrimToNull(options.BaseUrlOverride) is not null
            || options.PortOverride is int port && port > 0;

    public SharpClawResolvedConnection BuildClient(
        SharpClawConnectionOptions options,
        SharpClawDiscoveryEntry? entry)
    {
        var baseUrl = ResolveBaseUrl(options, entry);
        var (apiKey, apiKeySource) = ResolveApiKey(options, entry);
        var (gatewayToken, gatewayTokenSource) = ResolveGatewayToken(options, entry);

        var client = SharpClawHttpClient.FromResolved(baseUrl, apiKey, gatewayToken, entry);
        return new SharpClawResolvedConnection
        {
            Entry = entry,
            Client = client,
            BaseUrl = baseUrl,
            SelectionSummary = entry is null
                ? "manual endpoint"
                : $"instance={Short(entry.InstanceId)} baseUrl={entry.BaseUrl}",
            ApiKeySource = apiKeySource,
            GatewayTokenSource = gatewayTokenSource,
        };
    }

    public string DescribeOverrides(SharpClawConnectionOptions options)
    {
        var parts = new List<string>
        {
            $"selectedInstance={TrimToNull(options.SelectedInstanceId) ?? "<auto>"}",
            $"baseUrlOverride={(TrimToNull(options.BaseUrlOverride) is null ? "no" : "yes")}",
            $"portOverride={(options.PortOverride is int port && port > 0 ? port.ToString() : "no")}",
            $"apiKey={SecretSourceLabel(options.ApiKeyOverride, options.ApiKeyFileOverride, "discovery")}",
            $"gateway={SecretSourceLabel(options.GatewayTokenOverride, options.GatewayTokenFileOverride, "discovery/optional")}",
            $"preferAlive={options.PreferAliveInstances}",
        };
        return string.Join(", ", parts);
    }

    public static string EntryKey(SharpClawDiscoveryEntry entry)
        => TrimToNull(entry.InstanceId)
            ?? TrimToNull(entry.SourceFile)
            ?? TrimToNull(entry.BaseUrl)
            ?? string.Empty;

    public static string Short(string? value)
        => string.IsNullOrEmpty(value) ? "?" : value!.Length > 8 ? value[..8] : value;

    private static string ResolveBaseUrl(SharpClawConnectionOptions options, SharpClawDiscoveryEntry? entry)
    {
        var baseUrl = TrimToNull(options.BaseUrlOverride)
            ?? TrimToNull(entry?.BaseUrl);

        if (baseUrl is null && options.PortOverride is int manualPort && manualPort > 0)
            baseUrl = $"http://127.0.0.1:{manualPort}";

        if (baseUrl is null)
            throw new InvalidOperationException(
                "No SharpClaw backend endpoint is available. Select a detected instance or set a Base URL / Port override.");

        if (options.PortOverride is int port && port > 0)
        {
            var builder = new UriBuilder(baseUrl) { Port = port };
            baseUrl = builder.Uri.ToString();
        }

        _ = new Uri(baseUrl.TrimEnd('/') + '/', UriKind.Absolute);
        return baseUrl.TrimEnd('/') + "/";
    }

    private static (string Secret, string Source) ResolveApiKey(
        SharpClawConnectionOptions options,
        SharpClawDiscoveryEntry? entry)
    {
        var direct = TrimToNull(options.ApiKeyOverride);
        if (direct is not null)
            return (direct, "direct override");

        var file = TrimToNull(options.ApiKeyFileOverride);
        if (file is not null)
            return (ReadRequiredSecret(file, "API key override file"), $"file override: {file}");

        var discoveryFile = TrimToNull(entry?.ApiKeyFilePath);
        if (discoveryFile is not null)
            return (ReadRequiredSecret(discoveryFile, "discovery API key file"), $"discovery file: {discoveryFile}");

        throw new InvalidOperationException(
            "No API key is available. Select a detected instance with an API key file, or set an API key override.");
    }

    private static (string? Secret, string Source) ResolveGatewayToken(
        SharpClawConnectionOptions options,
        SharpClawDiscoveryEntry? entry)
    {
        var direct = TrimToNull(options.GatewayTokenOverride);
        if (direct is not null)
            return (direct, "direct override");

        var file = TrimToNull(options.GatewayTokenFileOverride);
        if (file is not null)
            return (ReadRequiredSecret(file, "gateway token override file"), $"file override: {file}");

        var discoveryFile = TrimToNull(entry?.GatewayTokenFilePath);
        if (discoveryFile is null)
            return (null, "not configured");

        if (!File.Exists(discoveryFile))
            return (null, $"discovery file missing: {discoveryFile}");

        try
        {
            return (File.ReadAllText(discoveryFile).Trim(), $"discovery file: {discoveryFile}");
        }
        catch
        {
            return (null, $"discovery file unreadable: {discoveryFile}");
        }
    }

    private static string ReadRequiredSecret(string path, string label)
    {
        if (!File.Exists(path))
            throw new InvalidOperationException($"{label} not found: {path}");

        var value = File.ReadAllText(path).Trim();
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"{label} is empty: {path}");

        return value;
    }

    private static string SecretSourceLabel(string? direct, string? file, string fallback)
    {
        if (TrimToNull(direct) is not null)
            return "direct override";
        if (TrimToNull(file) is not null)
            return "file override";
        return fallback;
    }

    private static string? TrimToNull(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }
}
