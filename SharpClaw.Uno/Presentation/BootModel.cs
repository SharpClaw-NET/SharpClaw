using System.Collections.Immutable;
using SharpClaw.Services;

namespace SharpClaw.Presentation;

public sealed record BootState(
    string Icon,
    Windows.UI.Color IconColor,
    string Text,
    Windows.UI.Color TextColor,
    bool IsRetryVisible,
    ImmutableArray<DiagnosticLine> DiagnosticLog = default);

/// <summary>A single diagnostic probe result shown on the boot page.</summary>
public sealed record DiagnosticLine(
    string Label,
    string Result,
    bool IsError);

/// <summary>Result of a single diagnostic step.</summary>
public sealed record StepResult(bool Ok, DiagnosticLine Line);

public sealed class BootModel
{
    private readonly BackendProcessManager _backend;
    private readonly GatewayProcessManager _gateway;
    private readonly SharpClawApiClient _api;

    public BootModel(
        BackendProcessManager backend,
        GatewayProcessManager gateway,
        SharpClawApiClient api)
    {
        _backend = backend;
        _gateway = gateway;
        _api = api;
    }

    public bool IsAwaitingInput { get; set; }

    /// <summary>Current API base URL (for display / editing).</summary>
    public string ApiUrl => _backend.ApiUrl;

    internal const int MaxRetries = 3;
    internal static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(3);

    public void ApplyCustomUrl(string? customUrl)
    {
        if (!string.IsNullOrWhiteSpace(customUrl))
        {
            var url = customUrl.Trim();
            _backend.UpdateApiUrl(url);
            _api.UpdateBaseUrl(url);
        }
    }

    /// <summary>Step 1 (silent): ensure the backend process is available.</summary>
    public async Task<StepResult> RunBackendStepAsync(CancellationToken ct)
    {
        try
        {
            await _backend.EnsureStartedAsync(ct);

            var mode = _backend.IsExternal ? "external" : "bundled";
            var running = _backend.IsRunning ? "running" : (_backend.IsExternal ? "detected" : "started");
            return new(true, new("Backend", $"{running} ({mode})", false));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new(false, new("Backend", ex.Message, true));
        }
    }

    /// <summary>
    /// Step 2: Echo probe — unauthenticated GET /echo.
    /// Polls with retries for bundled backends that need startup time.
    /// </summary>
    public async Task<StepResult> RunEchoStepAsync(CancellationToken ct)
    {
        var maxWait = _backend.IsExternal ? TimeSpan.FromSeconds(5) : TimeSpan.FromSeconds(20);
        var deadline = DateTime.UtcNow + maxWait;
        Exception? lastEx = null;
        int lastStatus = 0;
        string? lastReason = null;

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            // Early exit: if the bundled process has already crashed,
            // stop polling and surface the actual error.
            if (!_backend.IsExternal && !_backend.IsRunning)
            {
                var output = _backend.ProcessOutput;
                var tail = output.Count > 0
                    ? string.Join(" | ", output.TakeLast(3))
                    : "no output captured";
                var code = _backend.ExitCode;
                return new(false, new("Echo",
                    $"Backend process exited (code {code}) — {tail}", true));
            }

            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
                var response = await http.GetAsync($"{_backend.ApiUrl}/echo", ct);

                if (response.IsSuccessStatusCode)
                    return new(true, new("Echo", $"{(int)response.StatusCode} OK", false));

                lastStatus = (int)response.StatusCode;
                lastReason = response.ReasonPhrase;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { lastEx = ex; }

            try { await Task.Delay(500, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        }

        if (lastEx is HttpRequestException httpEx)
            return new(false, new("Echo", $"HTTP error — {httpEx.Message}", true));
        if (lastEx is not null)
            return new(false, new("Echo", lastEx.Message, true));
        if (lastStatus != 0)
            return new(false, new("Echo", $"{lastStatus} {lastReason}", true));
        return new(false, new("Echo", $"No response within {(int)maxWait.TotalSeconds}s", true));
    }

    /// <summary>
    /// Step 3: Ping probe — authenticated GET /ping.
    /// Returns an extra API-key diagnostic line via <paramref name="extraDiag"/>.
    /// </summary>
    public async Task<(StepResult Result, DiagnosticLine? ApiKeyLine)> RunPingStepAsync(CancellationToken ct)
    {
        var keyPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SharpClaw", ".api-key");

        DiagnosticLine? apiKeyLine = File.Exists(keyPath)
            ? new("API Key", "file present", false)
            : new("API Key", $"file not found at {keyPath}", true);

        _api.InvalidateApiKey();
        try
        {
            await _api.WaitForReadyAsync(
                TimeSpan.FromSeconds(_backend.IsExternal ? 5 : 15), ct);

            return (new(true, new("Ping", "authenticated OK", false)), apiKeyLine);
        }
        catch (OperationCanceledException) { throw; }
        catch (TimeoutException)
        {
            return (new(false, new("Ping", "timed out — API key may be invalid or stale", true)), apiKeyLine);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("API key file"))
        {
            return (new(false, new("Ping", $"API key unavailable — {ex.Message}", true)), apiKeyLine);
        }
        catch (Exception ex)
        {
            return (new(false, new("Ping", ex.Message, true)), apiKeyLine);
        }
    }

    /// <summary>
    /// Optional step: start the public gateway when enabled.
    /// Non-fatal — the gateway is opt-in and not required for core functionality.
    /// Returns <c>null</c> when the gateway is disabled (skip launch).
    /// </summary>
    public async Task<StepResult?> RunGatewayStepAsync(CancellationToken ct)
    {
        if (_gateway.SkipLaunch)
            return null;

        try
        {
            // Forward the verified API key so the gateway can authenticate
            // with the core API without relying on file I/O (MSIX VFS safe).
            _gateway.ApiKey = _api.CachedApiKey;

            await _gateway.EnsureStartedAsync(ct);

            var mode = _gateway.IsExternal ? "external" : "bundled";
            var running = _gateway.IsRunning ? "running" : (_gateway.IsExternal ? "detected" : "started");
            return new(true, new("Gateway", $"{running} ({mode}) on {_gateway.GatewayUrl}", false));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // Non-fatal — the core API still works without the gateway.
            return new(false, new("Gateway", ex.Message, true));
        }
    }

    /// <summary>
    /// Produces a single-line summary from the diagnostic log,
    /// picking the first error entry or a generic fallback.
    /// </summary>
    internal static string SummariseDiagnostic(ImmutableArray<DiagnosticLine> log)
    {
        if (log.IsDefaultOrEmpty)
            return "Unable to reach the SharpClaw service.";

        foreach (var line in log)
        {
            if (line.IsError)
                return $"{line.Label} failed: {line.Result}";
        }

        return "Unable to reach the SharpClaw service.";
    }

    /// <summary>
    /// Builds a full-text diagnostic report for clipboard copy.
    /// </summary>
    internal string BuildDiagnosticReport(ImmutableArray<DiagnosticLine> log)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("SharpClaw Boot Diagnostics");
        sb.AppendLine(new string('─', 40));
        sb.AppendLine($"URL:  {_backend.ApiUrl}");
        sb.AppendLine($"Mode: {(_backend.IsExternal ? "external" : "bundled")}");
        sb.AppendLine($"Exe:  {(_backend.IsAvailable ? "found" : "NOT FOUND")}");
        sb.AppendLine();

        if (!log.IsDefaultOrEmpty)
        {
            sb.AppendLine("Probe Results:");
            foreach (var line in log)
                sb.AppendLine($"  {(line.IsError ? "✗" : "✓")} {line.Label}: {line.Result}");
            sb.AppendLine();
        }

        var output = _backend.ProcessOutput;
        if (output.Count > 0)
        {
            sb.AppendLine($"Process Output ({output.Count} lines):");
            foreach (var line in output)
                sb.AppendLine($"  {line}");
        }
        else
        {
            sb.AppendLine("Process Output: (none)");
        }

        return sb.ToString();
    }
}
