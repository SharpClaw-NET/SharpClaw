using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using SharpClaw.Utils.Logging;

namespace SharpClaw.VS2026Extension.Services;

/// <summary>
/// Thin <see cref="HttpClient"/> wrapper targeting the SharpClaw backend.
/// Resolves the X-Api-Key from the same discovery logic as the bridge client.
/// One instance is created per connected session and reused across the tool window.
/// </summary>
internal sealed class SharpClawHttpClient : IDisposable
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly HttpClient _http;

    public SharpClawHttpClient(string baseUrl, string apiKey, string? gatewayToken = null)
    {
        _http = new HttpClient { BaseAddress = new Uri(baseUrl.TrimEnd('/') + '/') };
        _http.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
        // The VS extension is a trusted local editor process. The backend
        // requires either a user JWT or the gateway token on non-exempt
        // endpoints; without it, JwtSessionMiddleware returns 401 even when
        // the API key is correct. Forwarding the gateway token mirrors what
        // SharpClaw.Gateway does and proves we're a trusted local process.
        if (!string.IsNullOrWhiteSpace(gatewayToken))
            _http.DefaultRequestHeaders.Add("X-Gateway-Token", gatewayToken);
        _http.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));
        _http.Timeout = TimeSpan.FromSeconds(30);
    }

    // ── Factory ─────────────────────────────────────────────────

    /// <summary>The discovery entry used to build this client, if any.</summary>
    public SharpClawDiscoveryEntry? DiscoveryEntry { get; private set; }

    public Uri? BaseAddress => _http.BaseAddress;

    /// <summary>
    /// Creates an instance by discovering the running SharpClaw backend through
    /// the standard <c>%LOCALAPPDATA%\SharpClaw\discovery</c> entries and reading
    /// the matching API key file.
    /// </summary>
    public static SharpClawHttpClient FromDiscovery()
    {
        var entry = SharpClawDiscovery.EnumerateRanked().FirstOrDefault()
            ?? throw new InvalidOperationException(
                "SharpClaw backend not found. Ensure the SharpClaw API service is running.");
        return FromEntry(entry);
    }

    /// <summary>
    /// Creates a client from an already-selected discovery entry. This lets the
    /// verbose connector log every step of the selection process before we
    /// commit to building the underlying <see cref="HttpClient"/>.
    /// </summary>
    public static SharpClawHttpClient FromEntry(SharpClawDiscoveryEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.BaseUrl))
            throw new InvalidOperationException("SharpClaw discovery entry is missing BaseUrl.");
        if (string.IsNullOrWhiteSpace(entry.ApiKeyFilePath))
            throw new InvalidOperationException("SharpClaw discovery entry is missing ApiKeyFilePath.");

        var apiKey = ReadFile(entry.ApiKeyFilePath!, entry.SourceFile);
        string? gatewayToken = null;
        if (!string.IsNullOrWhiteSpace(entry.GatewayTokenFilePath) && File.Exists(entry.GatewayTokenFilePath))
        {
            try { gatewayToken = File.ReadAllText(entry.GatewayTokenFilePath!).Trim(); }
            catch { /* gateway token is best-effort; missing file just means we'll get 401 */ }
        }
        var client = new SharpClawHttpClient(entry.BaseUrl!, apiKey, gatewayToken)
        {
            DiscoveryEntry = entry,
        };
        return client;
    }

    // ── HTTP helpers ──────────────────────────────────────────────

    public async Task<T?> GetAsync<T>(string path, CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync(path, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(json, JsonOptions);
    }

    /// <summary>
    /// POSTs <paramref name="body"/> as JSON and returns a streaming
    /// <see cref="HttpResponseMessage"/> with <c>ResponseHeadersRead</c>.
    /// The caller is responsible for disposing the response.
    /// </summary>
    public async Task<HttpResponseMessage> PostStreamAsync(
        string path, object body, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(body, JsonOptions);
        using var req = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        return await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
    }

    public void Dispose() => _http.Dispose();

    /// <summary>Raw GET returning the response so the caller can inspect status codes.</summary>
    public Task<HttpResponseMessage> GetRawAsync(string path, CancellationToken ct = default)
        => _http.GetAsync(path, ct);

    /// <summary>
    /// GETs an SSE stream with <c>ResponseHeadersRead</c> so the response body
    /// can be read incrementally. The caller is responsible for disposing the
    /// returned <see cref="HttpResponseMessage"/>.
    /// </summary>
    public Task<HttpResponseMessage> GetStreamAsync(string path, CancellationToken ct = default)
        => _http.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, path),
            HttpCompletionOption.ResponseHeadersRead,
            ct);

    /// <summary>POSTs JSON and returns the deserialized response body.</summary>
    public async Task<T?> PostJsonAsync<T>(string path, object body, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(body, JsonOptions);
        using var req = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var responseJson = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(responseJson, JsonOptions);
    }

    // ── API key resolution ────────────────────────────────────────

    private static string ReadFile(string path, string? discoverySource)
    {
        if (!File.Exists(path))
        {
            var dir = Path.GetDirectoryName(path);
            var dirState = dir is null ? "<none>" :
                (Directory.Exists(dir) ? "exists" : "MISSING");
            throw new InvalidOperationException(
                $"API key file not found: {path} (runtime dir: {dirState}). " +
                $"Discovery source: {discoverySource ?? "<unknown>"}. " +
                "Ensure the SharpClaw backend has fully started and written its runtime files.");
        }
        return File.ReadAllText(path).Trim();
    }
}
