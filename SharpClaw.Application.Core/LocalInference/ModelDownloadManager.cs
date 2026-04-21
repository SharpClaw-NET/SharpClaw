namespace SharpClaw.Application.Core.LocalInference;

using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SharpClaw.Utils.Security;

/// <summary>
/// Downloads model files to the local models directory with progress
/// tracking and HTTP Range resume support.
/// </summary>
public sealed class ModelDownloadManager
{
    private static readonly string DefaultModelsDirectory =
        Path.Combine(AppContext.BaseDirectory, "LocalModels");

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ModelDownloadManager> _logger;
    private readonly string _modelsDirectory;

    /// <summary>
    /// Root directory for locally downloaded model files.
    /// <para>
    /// Resolves in order of preference: the <c>Local:ModelsDirectory</c>
    /// configuration value (if set), otherwise
    /// <see cref="AppContext.BaseDirectory"/>
    /// + <c>LocalModels</c>. <see cref="AppContext.BaseDirectory"/> is
    /// preferred over <c>Assembly.GetExecutingAssembly().Location</c>
    /// because the latter is empty under single-file publish.
    /// See finding <c>L-011</c> in
    /// <c>docs/internal/llamasharp-pipeline-audit-and-remediation-plan.md</c>.
    /// </para>
    /// </summary>
    public static string ModelsDirectoryPath => DefaultModelsDirectory;

    /// <summary>
    /// Creates a downloader bound to the configured
    /// <c>Local:ModelsDirectory</c> (if any).
    /// </summary>
    public ModelDownloadManager(
        IHttpClientFactory httpClientFactory,
        IConfiguration? configuration = null,
        ILogger<ModelDownloadManager>? logger = null)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger ?? NullLogger<ModelDownloadManager>.Instance;
        var configured = configuration?["Local:ModelsDirectory"];
        _modelsDirectory = string.IsNullOrWhiteSpace(configured)
            ? DefaultModelsDirectory
            : configured;
    }

    /// <summary>Parameterless constructor intended for tests.</summary>
    internal ModelDownloadManager() : this(
        new SingleHttpClientFactory(new HttpClient()), configuration: null, logger: null)
    { }

    /// <summary>Instance-level access to the resolved models directory.</summary>
    public string ModelsDirectory => _modelsDirectory;

    public string GetModelPath(string filename)
    {
        PathGuard.EnsureFileName(filename, nameof(filename));
        return PathGuard.EnsureContainedIn(
            Path.Combine(_modelsDirectory, filename), _modelsDirectory);
    }

    public string GetModelPath(string subfolder, string filename)
    {
        PathGuard.EnsureFileName(subfolder, nameof(subfolder));
        PathGuard.EnsureFileName(filename, nameof(filename));
        return PathGuard.EnsureContainedIn(
            Path.Combine(_modelsDirectory, subfolder, filename), _modelsDirectory);
    }

    /// <summary>
    /// Determines the source subfolder from a download URL.
    /// HuggingFace URLs → "HuggingFace", otherwise "Direct".
    /// </summary>
    public static string ResolveSourceFolder(string url) =>
        url.Contains("huggingface.co", StringComparison.OrdinalIgnoreCase)
            ? "HuggingFace"
            : "Direct";

    public async Task DownloadAsync(
        string url,
        string destinationPath,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);

        using var http = _httpClientFactory.CreateClient();
        http.Timeout = TimeSpan.FromHours(12);

        var existingLength = File.Exists(destinationPath)
            ? new FileInfo(destinationPath).Length : 0L;

        var request = new HttpRequestMessage(HttpMethod.Get, url);
        var attemptedResume = existingLength > 0;
        if (attemptedResume)
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(existingLength, null);

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        // L-002 — if we asked for a partial response but the server
        // returned the full body (200 OK, common for CDN redirects and
        // cold HF blobs that strip Range), restart the file from scratch
        // instead of appending the full body to the existing partial
        // which would silently corrupt the GGUF.
        var resumeHonored = attemptedResume
            && response.StatusCode == HttpStatusCode.PartialContent;
        if (attemptedResume && !resumeHonored)
        {
            _logger.LogWarning(
                "Server ignored Range request for {Url} (returned {Status}); " +
                "restarting download from byte 0 to avoid corrupting the existing partial file.",
                url, (int)response.StatusCode);
            existingLength = 0L;
        }

        var totalBytes = response.Content.Headers.ContentLength.GetValueOrDefault() + existingLength;

        await using var contentStream = await response.Content.ReadAsStreamAsync(ct);
        await using var fileStream = new FileStream(
            destinationPath,
            resumeHonored ? FileMode.Append : FileMode.Create,
            FileAccess.Write, FileShare.None,
            bufferSize: 81920, useAsync: true);

        var buffer = new byte[81920];
        var totalRead = existingLength;
        int bytesRead;

        while ((bytesRead = await contentStream.ReadAsync(buffer, ct)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
            totalRead += bytesRead;
            if (totalBytes > 0)
                progress?.Report((double)totalRead / totalBytes);
        }
    }

    private sealed class SingleHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }
}
