using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace SharpClaw.PublicAPI.Infrastructure;

/// <summary>
/// Typed <see cref="HttpClient"/> wrapper that forwards requests to the
/// internal SharpClaw Application API, automatically attaching the
/// <c>X-Api-Key</c> header.
/// </summary>
public sealed class InternalApiClient(HttpClient httpClient, IOptions<InternalApiOptions> options)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private string? _cachedApiKey;

    public async Task<T?> GetAsync<T>(string path, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        AttachApiKey(request);
        using var response = await httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<T>(json, JsonOptions);
    }

    public async Task<TResponse?> PostAsync<TRequest, TResponse>(
        string path, TRequest body, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        AttachApiKey(request);
        request.Content = new StringContent(
            JsonSerializer.Serialize(body, JsonOptions), Encoding.UTF8, "application/json");
        using var response = await httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<TResponse>(json, JsonOptions);
    }

    public async Task PostAsync<TRequest>(string path, TRequest body, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        AttachApiKey(request);
        request.Content = new StringContent(
            JsonSerializer.Serialize(body, JsonOptions), Encoding.UTF8, "application/json");
        using var response = await httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task<TResponse?> PutAsync<TRequest, TResponse>(
        string path, TRequest body, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, path);
        AttachApiKey(request);
        request.Content = new StringContent(
            JsonSerializer.Serialize(body, JsonOptions), Encoding.UTF8, "application/json");
        using var response = await httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<TResponse>(json, JsonOptions);
    }

    public async Task<bool> DeleteAsync(string path, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, path);
        AttachApiKey(request);
        using var response = await httpClient.SendAsync(request, ct);
        return response.IsSuccessStatusCode;
    }

    private void AttachApiKey(HttpRequestMessage request)
    {
        var key = ResolveApiKey();
        request.Headers.Add("X-Api-Key", key);
    }

    private string ResolveApiKey()
    {
        if (_cachedApiKey is not null)
            return _cachedApiKey;

        var opts = options.Value;

        if (!string.IsNullOrEmpty(opts.ApiKey))
        {
            _cachedApiKey = opts.ApiKey;
            return _cachedApiKey;
        }

        // Read from the well-known file written by the internal API
        var keyFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SharpClaw", ".api-key");

        if (!File.Exists(keyFilePath))
            throw new InvalidOperationException(
                $"Internal API key file not found at '{keyFilePath}'. " +
                "Ensure the internal SharpClaw API is running.");

        _cachedApiKey = File.ReadAllText(keyFilePath).Trim();
        return _cachedApiKey;
    }
}
