using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

using SharpClaw.Application.Infrastructure.Models.Resources;
using SharpClaw.Contracts.Modules;
using SharpClaw.Infrastructure.Persistence;

namespace SharpClaw.Modules.WebAccess.Services;

/// <summary>
/// Handles external website access (browser + CLI modes) with SSRF protection,
/// content-type allow-listing, redirect origin pinning, and response size capping.
/// </summary>
public sealed class WebsiteAccessService(
    SharpClawDbContext db,
    IConfiguration configuration,
    ILogger<WebsiteAccessService> logger)
{
    private static readonly JsonSerializerOptions PayloadJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>Maximum response body size (2 MB).</summary>
    private const int MaxWebsiteResponseBytes = 2 * 1024 * 1024;

    /// <summary>Content-Type prefixes considered safe to return as text.</summary>
    private static readonly string[] SafeContentTypePrefixes =
    [
        "text/",
        "application/json",
        "application/xml",
        "application/xhtml+xml",
        "application/javascript",
        "application/x-javascript",
    ];

    // ═══════════════════════════════════════════════════════════════
    // Payloads
    // ═══════════════════════════════════════════════════════════════

    private sealed class AccessWebsitePayload
    {
        public string? ResourceId { get; set; }
        public string? TargetId { get; set; }
        public string? Mode { get; set; }
        public string? Path { get; set; }
    }

    // ═══════════════════════════════════════════════════════════════
    // Main entry point
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Fetches an external website registered in <see cref="WebsiteDB"/>
    /// using either a headless browser or a direct HTTP GET.
    /// </summary>
    public async Task<string> AccessAsync(
        JsonElement parameters, AgentJobContext job, CancellationToken ct)
    {
        if (!job.ResourceId.HasValue)
            throw new InvalidOperationException(
                "AccessWebsite requires a ResourceId (Website).");

        var website = await db.Websites
            .Include(w => w.Skill)
            .FirstOrDefaultAsync(w => w.Id == job.ResourceId.Value, ct)
            ?? throw new InvalidOperationException(
                $"Website {job.ResourceId} not found.");

        var payload = DeserializePayload<AccessWebsitePayload>(parameters, "AccessWebsite");
        var mode = (payload.Mode ?? "cli").ToLowerInvariant();

        var url = BuildWebsiteUrl(website.Url, payload.Path);
        ValidateExternalUrl(url, website.Url);

        logger.LogDebug("Website '{Name}' ({Mode}): {Url}", website.Name, mode, url);

        string? result = mode switch
        {
            "html" or "screenshot"
                => await AccessBrowserAsync(job, url, mode, ct),
            _ => await AccessCliAsync(url, ct),
        };

        if (website.Skill is { SkillText.Length: > 0 } skill)
            result = $"[Website Skill: {skill.Name}]\n{skill.SkillText}\n\n---\n\n{result}";

        return result ?? "(empty response)";
    }

    // ═══════════════════════════════════════════════════════════════
    // Browser mode
    // ═══════════════════════════════════════════════════════════════

    private async Task<string?> AccessBrowserAsync(
        AgentJobContext job, string url, string mode, CancellationToken ct)
    {
        var executable = configuration["Browser:Executable"]
            ?? LocalhostAccessService.ResolveChromiumExecutable();
        var extraArgs = configuration["Browser:Arguments"] ?? "--incognito";

        var tempFile = mode == "screenshot"
            ? Path.Combine(Path.GetTempPath(), $"sc_web_{job.JobId:N}.png")
            : null;

        var securityFlags = "--disable-downloads --disable-extensions --disable-plugins " +
                            "--no-first-run --disable-background-networking " +
                            "--disable-default-apps --disable-sync";

        var headlessArgs = mode switch
        {
            "screenshot" =>
                $"--headless --disable-gpu --no-sandbox --virtual-time-budget=10000 " +
                $"{securityFlags} {extraArgs} --screenshot=\"{tempFile}\" \"{url}\"",
            _ =>
                $"--headless --disable-gpu --no-sandbox --virtual-time-budget=10000 " +
                $"{securityFlags} {extraArgs} --dump-dom \"{url}\"",
        };

        var psi = new ProcessStartInfo
        {
            FileName = executable,
            Arguments = headlessArgs,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        using var process = new Process { StartInfo = psi };
        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
            throw new InvalidOperationException(
                $"Browser timed out after 30 seconds for URL: {url}");
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"Browser exited with code {process.ExitCode}.\nstderr: {stderr}");

        if (mode == "screenshot" && tempFile is not null && File.Exists(tempFile))
        {
            var bytes = await File.ReadAllBytesAsync(tempFile, ct);
            File.Delete(tempFile);
            return $"Screenshot captured ({bytes.Length} bytes) of {url}\n[SCREENSHOT_BASE64]{Convert.ToBase64String(bytes)}";
        }

        return string.IsNullOrWhiteSpace(stdout) ? "(empty page)" : stdout;
    }

    // ═══════════════════════════════════════════════════════════════
    // CLI (HTTP GET) mode
    // ═══════════════════════════════════════════════════════════════

    private async Task<string?> AccessCliAsync(string url, CancellationToken ct)
    {
        var allowedOrigin = new Uri(url).GetLeftPart(UriPartial.Authority);

        using var handler = new HttpClientHandler
        {
            MaxAutomaticRedirections = 10,
            AllowAutoRedirect = false,
        };

        using var httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(30),
            MaxResponseContentBufferSize = MaxWebsiteResponseBytes,
        };

        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (compatible; SharpClaw/1.0; +https://github.com/mkn8rn/SharpClaw)");

        var currentUrl = url;
        HttpResponseMessage response;
        var redirectCount = 0;
        const int maxRedirects = 10;

        do
        {
            var requestUri = new Uri(currentUrl);
            RejectPrivateAddress(requestUri);

            response = await httpClient.GetAsync(
                requestUri, HttpCompletionOption.ResponseHeadersRead, ct);

            if ((int)response.StatusCode is >= 300 and < 400
                && response.Headers.Location is { } location)
            {
                var redirectUri = location.IsAbsoluteUri
                    ? location
                    : new Uri(requestUri, location);

                if (!string.Equals(
                        redirectUri.GetLeftPart(UriPartial.Authority),
                        allowedOrigin, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        $"Redirect to a different origin is blocked: {redirectUri}");

                currentUrl = redirectUri.AbsoluteUri;
                response.Dispose();
                redirectCount++;
            }
            else
            {
                break;
            }
        } while (redirectCount < maxRedirects);

        if (redirectCount >= maxRedirects)
            throw new InvalidOperationException(
                $"Too many redirects ({maxRedirects}) for URL: {url}");

        var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
        var isSafeContent = SafeContentTypePrefixes.Any(
            prefix => contentType.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

        if (!isSafeContent)
        {
            response.Dispose();
            throw new InvalidOperationException(
                $"Blocked: content type '{contentType}' is not text-based. " +
                "Binary downloads are not permitted.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);
        var buffer = new char[MaxWebsiteResponseBytes / sizeof(char)];
        var charsRead = await reader.ReadBlockAsync(buffer, 0, buffer.Length);
        var body = new string(buffer, 0, charsRead);

        var sb = new StringBuilder();
        sb.AppendLine($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
        foreach (var header in response.Headers)
            sb.AppendLine($"{header.Key}: {string.Join(", ", header.Value)}");
        foreach (var header in response.Content.Headers)
            sb.AppendLine($"{header.Key}: {string.Join(", ", header.Value)}");
        sb.AppendLine();
        sb.Append(body);

        if (!reader.EndOfStream)
            sb.AppendLine("\n\n[TRUNCATED — response exceeded 2 MB limit]");

        return sb.ToString();
    }

    // ═══════════════════════════════════════════════════════════════
    // URL & security helpers
    // ═══════════════════════════════════════════════════════════════

    internal static string BuildWebsiteUrl(string baseUrl, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return baseUrl;

        if (path.Contains("..", StringComparison.Ordinal))
            throw new InvalidOperationException(
                "Path traversal ('..') is not permitted.");

        var trimmedBase = baseUrl.TrimEnd('/');
        var trimmedPath = path.TrimStart('/');

        return $"{trimmedBase}/{trimmedPath}";
    }

    internal static void ValidateExternalUrl(string url, string registeredBaseUrl)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            throw new InvalidOperationException($"Invalid URL: '{url}'.");

        if (uri.Scheme is not ("http" or "https"))
            throw new InvalidOperationException(
                $"Only http/https schemes are allowed. Got: '{uri.Scheme}'.");

        if (!Uri.TryCreate(registeredBaseUrl, UriKind.Absolute, out var baseUri))
            throw new InvalidOperationException(
                $"Registered website has an invalid base URL: '{registeredBaseUrl}'.");

        if (!string.Equals(
                uri.GetLeftPart(UriPartial.Authority),
                baseUri.GetLeftPart(UriPartial.Authority),
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"URL origin does not match the registered website. " +
                $"Expected: {baseUri.GetLeftPart(UriPartial.Authority)}, " +
                $"got: {uri.GetLeftPart(UriPartial.Authority)}.");

        RejectPrivateAddress(uri);
    }

    internal static void RejectPrivateAddress(Uri uri)
    {
        if (uri.Host is "localhost" or "127.0.0.1" or "[::1]")
            throw new InvalidOperationException(
                "External website access cannot target localhost. " +
                "Use the localhost tools instead.");

        if (IPAddress.TryParse(uri.Host, out var ip))
        {
            if (IPAddress.IsLoopback(ip))
                throw new InvalidOperationException(
                    $"Blocked: loopback address '{uri.Host}'.");

            var bytes = ip.GetAddressBytes();
            var isPrivate = bytes switch
            {
                [10, ..] => true,
                [172, >= 16 and <= 31, ..] => true,
                [192, 168, ..] => true,
                [169, 254, ..] => true,
                [0, ..] => true,
                _ => false,
            };

            if (isPrivate)
                throw new InvalidOperationException(
                    $"Blocked: private/reserved IP address '{uri.Host}'.");
        }
    }

    private static T DeserializePayload<T>(JsonElement parameters, string actionName) where T : class
    {
        return parameters.Deserialize<T>(PayloadJsonOptions)
            ?? throw new InvalidOperationException(
                $"Failed to deserialise {actionName} payload.");
    }
}
