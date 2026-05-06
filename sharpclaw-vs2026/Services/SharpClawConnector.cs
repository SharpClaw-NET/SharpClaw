using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace SharpClaw.VS2026Extension.Services;

/// <summary>
/// Result of a verbose connection attempt to the SharpClaw backend.
/// Carries the live client when successful and the failure reason otherwise.
/// </summary>
internal sealed class SharpClawConnectionResult
{
    public bool Success { get; init; }
    public string Summary { get; init; } = string.Empty;
    public Exception? Error { get; init; }
}

/// <summary>
/// Performs and narrates every step of connecting to the SharpClaw backend.
///
/// The same routine is used by the manual <c>Tools &gt; SharpClaw &gt; Connect</c>
/// command and by the auto-connect path that runs at extension startup, so the
/// "SharpClaw" Output pane always tells the same story.
/// </summary>
internal sealed class SharpClawConnector
{
    private readonly SharpClawBackend _backend;
    private readonly SharpClawOutputLog _log;

    public SharpClawConnector(SharpClawBackend backend, SharpClawOutputLog log)
    {
        _backend = backend;
        _log = log;
    }

    /// <summary>
    /// Connects to SharpClaw and verifies authentication. Every step is logged
    /// to the SharpClaw output pane with descriptive interpretation of failures.
    /// </summary>
    /// <param name="trigger">Where the connect was initiated from (e.g. "auto-connect", "Tools menu").</param>
    public async Task<SharpClawConnectionResult> ConnectAsync(string trigger, CancellationToken ct)
    {
        await _log.WriteLineAsync($"────────── Connect started ({trigger}) ──────────").ConfigureAwait(false);

        // ── Step 1: Discovery ─────────────────────────────────────
        await _log.WriteLineAsync($"[1/5] Scanning discovery directory: {SharpClawDiscovery.DiscoveryDirectory}").ConfigureAwait(false);
        var ranked = SharpClawDiscovery.EnumerateRanked();
        if (ranked.Count == 0)
        {
            const string msg = "No SharpClaw backend was found. Looked for backend-*.json under " +
                               "%LOCALAPPDATA%\\SharpClaw\\discovery. Is the SharpClaw API service running?";
            await _log.WriteLineAsync($"   ✗ {msg}").ConfigureAwait(false);
            return Fail(msg);
        }

        await _log.WriteLineAsync($"   Found {ranked.Count} discovery entr{(ranked.Count == 1 ? "y" : "ies")}:").ConfigureAwait(false);
        for (var i = 0; i < ranked.Count; i++)
        {
            var e = ranked[i];
            await _log.WriteLineAsync(
                $"     [{i}] instance={Short(e.InstanceId)} pid={e.ProcessId?.ToString() ?? "?"} " +
                $"alive={e.IsAlive} apiKey={(e.HasApiKeyOnDisk ? "present" : "MISSING")} " +
                $"gateway={(e.HasGatewayTokenOnDisk ? "present" : "absent")} " +
                $"baseUrl={e.BaseUrl ?? "?"} src={System.IO.Path.GetFileName(e.SourceFile) ?? "?"}")
                .ConfigureAwait(false);
        }

        var chosen = ranked[0];
        await _log.WriteLineAsync(
            $"   Selected entry [0]: instance={Short(chosen.InstanceId)} baseUrl={chosen.BaseUrl}")
            .ConfigureAwait(false);

        if (!chosen.IsAlive)
            await _log.WriteLineAsync(
                $"   ⚠ Selected backend's process (pid={chosen.ProcessId}) does not appear to be alive. " +
                "Probes will likely fail; proceeding so we can confirm the failure mode.").ConfigureAwait(false);

        if (!chosen.HasApiKeyOnDisk)
            await _log.WriteLineAsync(
                $"   ⚠ API key file is missing at {chosen.ApiKeyFilePath}. The backend may not have finished " +
                "writing its runtime files yet, or this discovery entry is stale.").ConfigureAwait(false);

        // ── Step 2: Build client (reads API key + gateway token) ──
        SharpClawHttpClient client;
        try
        {
            await _log.WriteLineAsync(
                $"[2/5] Reading API key from: {chosen.ApiKeyFilePath}").ConfigureAwait(false);
            if (chosen.HasGatewayTokenOnDisk)
                await _log.WriteLineAsync(
                    $"      Reading gateway token from: {chosen.GatewayTokenFilePath}").ConfigureAwait(false);
            else
                await _log.WriteLineAsync(
                    "      No gateway token on disk; the backend may reject requests with 401 " +
                    "if it requires the X-Gateway-Token header for trusted local processes.").ConfigureAwait(false);

            _backend.Reset();
            client = _backend.SetClient(SharpClawHttpClient.FromEntry(chosen));
            await _log.WriteLineAsync($"      ✓ HTTP client built. BaseAddress={client.BaseAddress}").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var msg = $"Failed to build HTTP client: {ex.Message}";
            await _log.WriteLineAsync($"   ✗ {msg}").ConfigureAwait(false);
            return Fail(msg, ex);
        }

        // ── Step 3: /echo ─────────────────────────────────────────
        await _log.WriteLineAsync("[3/5] Probing /echo (no auth required)…").ConfigureAwait(false);
        try
        {
            using var echoResp = await client.GetRawAsync("echo", ct).ConfigureAwait(false);
            var status = (int)echoResp.StatusCode;
            await _log.WriteLineAsync($"      ← HTTP {status} {echoResp.ReasonPhrase}").ConfigureAwait(false);
            if (!echoResp.IsSuccessStatusCode)
            {
                var msg = $"/echo returned HTTP {status}. The backend is reachable but not healthy. " +
                          "This usually means the API process started but hasn't finished initializing.";
                await _log.WriteLineAsync($"   ✗ {msg}").ConfigureAwait(false);
                return Fail(msg);
            }
        }
        catch (Exception ex)
        {
            var msg = $"/echo unreachable: {ex.Message}. The backend URL ({chosen.BaseUrl}) is not " +
                      "responding. Confirm the SharpClaw API service is actually listening on this port.";
            await _log.WriteLineAsync($"   ✗ {msg}").ConfigureAwait(false);
            return Fail(msg, ex);
        }

        // ── Step 4: /ping (validates X-Api-Key + gateway token) ───
        await _log.WriteLineAsync("[4/5] Probing /ping (validates X-Api-Key + X-Gateway-Token)…").ConfigureAwait(false);
        try
        {
            using var pingResp = await client.GetRawAsync("ping", ct).ConfigureAwait(false);
            var status = (int)pingResp.StatusCode;
            await _log.WriteLineAsync($"      ← HTTP {status} {pingResp.ReasonPhrase}").ConfigureAwait(false);
            if (!pingResp.IsSuccessStatusCode)
            {
                var hint = InterpretAuthFailure(pingResp.StatusCode, chosen);
                var msg = $"/ping failed with HTTP {status}. {hint}";
                await _log.WriteLineAsync($"   ✗ {msg}").ConfigureAwait(false);
                return Fail(msg);
            }
        }
        catch (Exception ex)
        {
            var msg = $"/ping threw an exception: {ex.Message}";
            await _log.WriteLineAsync($"   ✗ {msg}").ConfigureAwait(false);
            return Fail(msg, ex);
        }

        // ── Step 5: First domain call (contexts) ──────────────────
        await _log.WriteLineAsync("[5/5] Loading /channel-contexts to confirm session is usable…").ConfigureAwait(false);
        try
        {
            var contexts = await _backend.GetContextsAsync(ct).ConfigureAwait(false);
            await _log.WriteLineAsync($"      ✓ Loaded {contexts.Count} context(s).").ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            var msg = $"Loading contexts failed: {ex.Message}. Authentication succeeded but a domain " +
                      "endpoint is failing — the backend may be in a partially-initialized state.";
            await _log.WriteLineAsync($"   ✗ {msg}").ConfigureAwait(false);
            return Fail(msg, ex);
        }
        catch (Exception ex)
        {
            await _log.WriteLineAsync($"   ✗ {ex.Message}").ConfigureAwait(false);
            return Fail(ex.Message, ex);
        }

        await _log.WriteLineAsync("✓ Connected. SharpClaw backend is reachable and authenticated.").ConfigureAwait(false);
        await _log.WriteLineAsync("───────────────────────────────────────────────").ConfigureAwait(false);
        return new SharpClawConnectionResult { Success = true, Summary = "Connected" };
    }

    private SharpClawConnectionResult Fail(string summary, Exception? ex = null) => new()
    {
        Success = false,
        Summary = summary,
        Error = ex,
    };

    private static string Short(string? id) =>
        string.IsNullOrEmpty(id) ? "?" : (id!.Length > 8 ? id[..8] : id);

    private static string InterpretAuthFailure(HttpStatusCode status, SharpClawDiscoveryEntry entry) => status switch
    {
        HttpStatusCode.Unauthorized =>
            "The X-Api-Key was rejected (401). The discovery entry's API key may not match what the " +
            "running backend currently considers valid — usually because a stale discovery file points at " +
            "an older instance, or the backend was restarted and rotated its key. Try restarting SharpClaw " +
            $"and reconnecting. (instance={Short(entry.InstanceId)}, key file={entry.ApiKeyFilePath})",
        HttpStatusCode.Forbidden =>
            "The API key was accepted but the request was forbidden (403). Check whether the gateway token " +
            $"on disk ({entry.GatewayTokenFilePath ?? "<none>"}) matches what the backend expects.",
        HttpStatusCode.Locked =>
            "The endpoint returned 423 Locked. Your instance id may not have saved the correct API key, or " +
            "the wrong backend instance was selected — verify the chosen discovery entry above is the one " +
            "you expect.",
        HttpStatusCode.TooManyRequests =>
            "The endpoint returned 429 Too Many Requests. The local rate limiter tripped; wait a minute and retry.",
        HttpStatusCode.ServiceUnavailable =>
            "The endpoint returned 503 Service Unavailable. The backend is up but not yet ready to serve traffic.",
        _ => $"Unexpected status {(int)status} ({status}). Check the backend logs for details.",
    };
}
