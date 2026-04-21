using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace SharpClaw.Application.Core.LocalInference;

/// <summary>
/// Resolves HuggingFace model URLs to direct GGUF download links.
/// Supports:
///   - Direct file links: https://huggingface.co/{org}/{repo}/resolve/{branch}/{file}.gguf
///   - Repo links: https://huggingface.co/{org}/{repo} → lists GGUF files via API
///   - Repo tree links: /tree/{branch} → lists GGUF files for that branch
///   - Any direct URL to a .gguf file
/// <para>
/// L-010 hardening: sets a descriptive User-Agent, optionally attaches
/// the <c>Local:HuggingFaceToken</c> bearer for gated repos, retries on
/// 429 honouring <c>Retry-After</c>, treats a missing repo (404) as an
/// empty result, and tightens the quantization regex so it doesn't
/// match arbitrary substrings.
/// </para>
/// </summary>
public sealed partial class HuggingFaceUrlResolver
{
    private const string HfApiBase = "https://huggingface.co/api/models";
    private const string DefaultBranch = "main";
    private const int MaxRetries = 3;
    private static readonly TimeSpan FallbackRetryDelay = TimeSpan.FromSeconds(2);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly string? _hfToken;
    private readonly ILogger<HuggingFaceUrlResolver> _logger;

    public HuggingFaceUrlResolver(
        IHttpClientFactory httpClientFactory,
        IConfiguration? configuration = null,
        ILogger<HuggingFaceUrlResolver>? logger = null)
    {
        _httpClientFactory = httpClientFactory;
        _hfToken = configuration?["Local:HuggingFaceToken"];
        _logger = logger ?? NullLogger<HuggingFaceUrlResolver>.Instance;
    }

    public async Task<IReadOnlyList<ResolvedModelFile>> ResolveAsync(
        string url, CancellationToken ct = default)
    {
        if (!url.Contains("huggingface.co", StringComparison.OrdinalIgnoreCase))
            return [new ResolvedModelFile(url, Path.GetFileName(new Uri(url).AbsolutePath), null)];

        var uri = new Uri(url);
        var segments = uri.AbsolutePath.Trim('/').Split('/');

        // Direct file link: /org/repo/resolve/{branch}/file.gguf
        if (segments.Length >= 5 && segments[2] == "resolve")
        {
            var filename = segments[^1];
            var quant = ParseQuantization(filename);
            return [new ResolvedModelFile(url, filename, quant)];
        }

        // Repo tree link: /org/repo/tree/{branch}
        string branch = DefaultBranch;
        if (segments.Length >= 4 && segments[2] == "tree")
            branch = string.Join('/', segments.Skip(3));

        // Repo link: /org/repo → query API for GGUF siblings
        if (segments.Length >= 2)
        {
            var repoId = $"{segments[0]}/{segments[1]}";
            return await ListGgufFilesAsync(repoId, branch, ct);
        }

        return [new ResolvedModelFile(url, "model.gguf", null)];
    }

    private async Task<IReadOnlyList<ResolvedModelFile>> ListGgufFilesAsync(
        string repoId, string branch, CancellationToken ct)
    {
        using var http = _httpClientFactory.CreateClient();
        var apiUrl = $"{HfApiBase}/{repoId}";

        for (var attempt = 1; attempt <= MaxRetries; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, apiUrl);
            request.Headers.UserAgent.ParseAdd("SharpClaw/1.0 (+https://github.com/mkn8rn/SharpClaw)");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (!string.IsNullOrWhiteSpace(_hfToken))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _hfToken);

            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                _logger.LogInformation("HF repo {RepoId} returned 404; treating as empty.", repoId);
                return [];
            }

            if ((int)response.StatusCode == 429 && attempt < MaxRetries)
            {
                var delay = response.Headers.RetryAfter?.Delta
                    ?? (response.Headers.RetryAfter?.Date is { } dt
                        ? dt - DateTimeOffset.UtcNow
                        : (TimeSpan?)null)
                    ?? FallbackRetryDelay;
                if (delay < TimeSpan.Zero) delay = FallbackRetryDelay;
                _logger.LogWarning("HF rate-limited on {RepoId}; retrying in {Delay}.", repoId, delay);
                await Task.Delay(delay, ct);
                continue;
            }

            response.EnsureSuccessStatusCode();

            var info = await response.Content.ReadFromJsonAsync<HfModelInfo>(cancellationToken: ct);
            return info?.Siblings?
                .Where(s => s.Rfilename?.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase) == true)
                .Select(s => new ResolvedModelFile(
                    $"https://huggingface.co/{repoId}/resolve/{branch}/{s.Rfilename}",
                    s.Rfilename!,
                    ParseQuantization(s.Rfilename!)))
                .ToList() ?? [];
        }

        _logger.LogWarning("HF list exhausted retries for {RepoId}; returning empty.", repoId);
        return [];
    }

    private static string? ParseQuantization(string filename)
    {
        var match = QuantizationPattern().Match(filename);
        return match.Success ? match.Value : null;
    }

    // L-010 — anchor on a word boundary and require a leading I?Q so a
    // filename like "FooQ4_K_M-v1.gguf" resolves to "Q4_K_M" but
    // unrelated tokens are not matched.
    [GeneratedRegex(@"\b(?:IQ|Q)\d(?:_[A-Za-z0-9]+)*\b", RegexOptions.IgnoreCase)]
    private static partial Regex QuantizationPattern();

    private sealed record HfModelInfo(
        [property: JsonPropertyName("siblings")] List<HfSibling>? Siblings);

    private sealed record HfSibling(
        [property: JsonPropertyName("rfilename")] string? Rfilename);
}

public sealed record ResolvedModelFile(string DownloadUrl, string Filename, string? Quantization);
