using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using SharpClaw.Utils.Logging;

namespace SharpClaw.VS2026Extension.Services;

/// <summary>
/// One backend instance advertised under
/// <c>%LOCALAPPDATA%\SharpClaw\discovery\instances\backend-*.json</c>.
/// Carries everything the extension needs to authenticate and probe the
/// backend, plus diagnostic flags so the connector can narrate selection.
/// </summary>
internal sealed class SharpClawDiscoveryEntry
{
    public string? InstanceId { get; set; }
    public string? InstallFingerprint { get; set; }
    public string? BaseUrl { get; set; }
    public string? ApiKeyFilePath { get; set; }
    public string? GatewayTokenFilePath { get; set; }
    public int? ProcessId { get; set; }
    public DateTimeOffset? StartedAtUtc { get; set; }
    public DateTimeOffset? LastSeenUtc { get; set; }

    [JsonIgnore] public string? SourceFile { get; set; }
    [JsonIgnore] public DateTimeOffset SourceWriteUtc { get; set; }
    [JsonIgnore] public bool IsAlive { get; set; }
    [JsonIgnore] public bool HasApiKeyOnDisk { get; set; }
    [JsonIgnore] public bool HasGatewayTokenOnDisk { get; set; }
}

/// <summary>
/// Reads the SharpClaw discovery directory and produces a ranked list of
/// backend instances. Centralized so the HTTP client and the verbose
/// connector both consume the same selection logic.
/// </summary>
internal static class SharpClawDiscovery
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static string DiscoveryDirectory =>
        Path.Combine(SharpClawAppDataPaths.GetSharpClawRootDirectory(), "discovery");

    /// <summary>
    /// Returns every parseable <c>backend-*.json</c> entry under the discovery
    /// tree, ordered with the freshest, alive, API-key-bearing instance first.
    /// </summary>
    public static IReadOnlyList<SharpClawDiscoveryEntry> EnumerateRanked()
    {
        var dir = DiscoveryDirectory;
        if (!Directory.Exists(dir))
            return Array.Empty<SharpClawDiscoveryEntry>();

        var entries = new List<SharpClawDiscoveryEntry>();
        foreach (var file in Directory.EnumerateFiles(dir, "backend-*.json", SearchOption.AllDirectories))
        {
            SharpClawDiscoveryEntry? entry = null;
            DateTimeOffset writeTimeUtc = DateTimeOffset.MinValue;
            try
            {
                var text = File.ReadAllText(file);
                entry = JsonSerializer.Deserialize<SharpClawDiscoveryEntry>(text, JsonOptions);
                writeTimeUtc = new DateTimeOffset(File.GetLastWriteTimeUtc(file), TimeSpan.Zero);
            }
            catch { /* unreadable entry is just ignored */ }

            if (entry?.ApiKeyFilePath is null)
                continue;

            entry.SourceFile = file;
            entry.SourceWriteUtc = writeTimeUtc;
            entry.IsAlive = IsAlive(entry.ProcessId);
            entry.HasApiKeyOnDisk = File.Exists(entry.ApiKeyFilePath);
            entry.HasGatewayTokenOnDisk =
                !string.IsNullOrWhiteSpace(entry.GatewayTokenFilePath) &&
                File.Exists(entry.GatewayTokenFilePath!);
            entries.Add(entry);
        }

        // Best entry first: alive + API key on disk + most recently seen/written.
        return entries
            .OrderByDescending(e => e.IsAlive)
            .ThenByDescending(e => e.HasApiKeyOnDisk)
            .ThenByDescending(e => e.LastSeenUtc ?? e.StartedAtUtc ?? e.SourceWriteUtc)
            .ToList();
    }

    private static bool IsAlive(int? pid)
    {
        if (pid is null or <= 0) return true; // be permissive when unknown
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(pid.Value);
            return !p.HasExited;
        }
        catch
        {
            return false;
        }
    }
}
