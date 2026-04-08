using System.Diagnostics;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

using SharpClaw.Contracts.Modules;

namespace SharpClaw.Modules.WebAccess.Services;

/// <summary>
/// Handles localhost access via headless browser (Chrome/Edge auto-detection)
/// and direct HTTP GET. Localhost URLs use self-signed cert acceptance.
/// </summary>
public sealed class LocalhostAccessService(
    IConfiguration configuration,
    ILogger<LocalhostAccessService> logger)
{
    private static readonly JsonSerializerOptions PayloadJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    // ═══════════════════════════════════════════════════════════════
    // Payloads
    // ═══════════════════════════════════════════════════════════════

    private sealed class AccessLocalhostPayload
    {
        public string? Url { get; set; }
        public string? Mode { get; set; }
    }

    // ═══════════════════════════════════════════════════════════════
    // Browser access
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Launches a headless browser against a localhost URL and returns
    /// either a screenshot (base64) or the page HTML.
    /// </summary>
    public async Task<string> AccessBrowserAsync(
        JsonElement parameters, AgentJobContext job, CancellationToken ct)
    {
        var payload = DeserializePayload<AccessLocalhostPayload>(parameters,
            "AccessLocalhostInBrowser");

        var url = ValidateLocalhostUrl(payload.Url);
        var mode = (payload.Mode ?? "html").ToLowerInvariant();

        var executable = configuration["Browser:Executable"] ?? ResolveChromiumExecutable();
        var extraArgs = configuration["Browser:Arguments"] ?? "--incognito";

        var tempFile = mode == "screenshot"
            ? Path.Combine(Path.GetTempPath(), $"sc_{job.JobId:N}.png")
            : null;

        var headlessArgs = mode switch
        {
            "screenshot" => $"--headless --disable-gpu --no-sandbox --ignore-certificate-errors --virtual-time-budget=10000 {extraArgs} --screenshot=\"{tempFile}\" \"{url}\"",
            _ => $"--headless --disable-gpu --no-sandbox --ignore-certificate-errors --virtual-time-budget=10000 {extraArgs} --dump-dom \"{url}\"",
        };

        logger.LogDebug("Browser ({Mode}): {Executable} → {Url}", mode, executable, url);

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
    // CLI (HTTP GET) access
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Makes a direct HTTP request to a localhost URL and returns
    /// the status code, headers, and response body.
    /// </summary>
    public async Task<string> AccessCliAsync(
        JsonElement parameters, AgentJobContext job, CancellationToken ct)
    {
        var payload = DeserializePayload<AccessLocalhostPayload>(parameters,
            "AccessLocalhostCli");

        var url = ValidateLocalhostUrl(payload.Url);

        logger.LogDebug("HTTP GET → {Url}", url);

        using var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback =
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        };
        using var httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        var response = await httpClient.GetAsync(url, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        var sb = new StringBuilder();
        sb.AppendLine($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
        foreach (var header in response.Headers)
            sb.AppendLine($"{header.Key}: {string.Join(", ", header.Value)}");
        foreach (var header in response.Content.Headers)
            sb.AppendLine($"{header.Key}: {string.Join(", ", header.Value)}");
        sb.AppendLine();
        sb.Append(body);

        return sb.ToString();
    }

    // ═══════════════════════════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Probes well-known installation paths for Chromium-based browsers
    /// on Windows (Chrome, Edge) and returns the first existing path.
    /// Falls back to "chrome" on non-Windows or if nothing is found.
    /// </summary>
    internal static string ResolveChromiumExecutable()
    {
        if (!OperatingSystem.IsWindows())
            return "google-chrome";

        ReadOnlySpan<string> candidates =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Microsoft", "Edge", "Application", "msedge.exe"),
        ];

        foreach (var path in candidates)
        {
            if (File.Exists(path))
                return path;
        }

        return "chrome";
    }

    private static string ValidateLocalhostUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            throw new InvalidOperationException(
                "Localhost access requires a 'url' field.");

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            throw new InvalidOperationException(
                $"Invalid URL: '{url}'.");

        if (uri.Host is not ("localhost" or "127.0.0.1" or "[::1]"))
            throw new InvalidOperationException(
                $"URL host must be localhost, 127.0.0.1, or [::1]. Got: '{uri.Host}'.");

        return url;
    }

    private static T DeserializePayload<T>(JsonElement parameters, string actionName) where T : class
    {
        return parameters.Deserialize<T>(PayloadJsonOptions)
            ?? throw new InvalidOperationException(
                $"Failed to deserialise {actionName} payload.");
    }
}
