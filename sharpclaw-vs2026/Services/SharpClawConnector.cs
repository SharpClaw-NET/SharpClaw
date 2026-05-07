using System;
using System.Collections.Generic;
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
/// The same routine is used by the manual Tools &gt; SharpClaw &gt; Connect
/// command and by the auto-connect path that runs at extension startup, so the
/// SharpClaw Output pane always tells the same story.
/// </summary>
internal sealed class SharpClawConnector
{
    private readonly SharpClawBackend _backend;
    private readonly SharpClawOutputLog _log;
    private readonly SharpClawConnectionOptionsStore _optionsStore;

    public SharpClawConnector(
        SharpClawBackend backend,
        SharpClawOutputLog log,
        SharpClawConnectionOptionsStore optionsStore)
    {
        _backend = backend;
        _log = log;
        _optionsStore = optionsStore;
    }

    /// <summary>
    /// Connects to SharpClaw and verifies authentication. Every step is logged
    /// to the SharpClaw output pane with descriptive interpretation of failures.
    /// </summary>
    /// <param name="trigger">Where the connect was initiated from.</param>
    public async Task<SharpClawConnectionResult> ConnectAsync(string trigger, CancellationToken ct)
    {
        await _log.WriteLineAsync($"---------- Connect started ({trigger}) ----------").ConfigureAwait(false);

        var options = _optionsStore.Load();
        await _log.WriteLineAsync($"Connection options: {_optionsStore.DescribeOverrides(options)}").ConfigureAwait(false);
        await _log.WriteLineAsync($"Connection options file: {_optionsStore.OptionsPath}").ConfigureAwait(false);

        await _log.WriteLineAsync($"[1/5] Scanning discovery directory: {SharpClawDiscovery.DiscoveryDirectory}").ConfigureAwait(false);
        var ranked = _optionsStore.EnumerateDetectedInstances(options.PreferAliveInstances);
        if (ranked.Count == 0 && !_optionsStore.HasManualEndpoint(options))
        {
            const string msg = "No SharpClaw backend was found. Looked for backend-*.json under %LOCALAPPDATA%\\SharpClaw\\discovery. Is the SharpClaw API service running?";
            await _log.WriteLineAsync($"   x {msg}").ConfigureAwait(false);
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

        var chosen = _optionsStore.SelectEntry(options, ranked);
        if (chosen is not null)
        {
            var chosenIndex = FindIndex(ranked, chosen);
            await _log.WriteLineAsync(
                $"   Selected entry [{chosenIndex}]: instance={Short(chosen.InstanceId)} baseUrl={chosen.BaseUrl}")
                .ConfigureAwait(false);

            if (!chosen.IsAlive)
                await _log.WriteLineAsync(
                    $"   ! Selected backend's process (pid={chosen.ProcessId}) does not appear to be alive. Probes will likely fail; proceeding so we can confirm the failure mode.")
                    .ConfigureAwait(false);

            if (!chosen.HasApiKeyOnDisk)
                await _log.WriteLineAsync(
                    $"   ! API key file is missing at {chosen.ApiKeyFilePath}. The backend may not have finished writing its runtime files yet, or this discovery entry is stale.")
                    .ConfigureAwait(false);
        }
        else
        {
            await _log.WriteLineAsync("   No discovery entry selected; using manual endpoint overrides.").ConfigureAwait(false);
        }

        SharpClawHttpClient? client = null;
        var published = false;
        try
        {
            await _log.WriteLineAsync("[2/5] Resolving endpoint and authentication material...").ConfigureAwait(false);
            var resolved = _optionsStore.BuildClient(options, chosen);
            client = resolved.Client;
            await _log.WriteLineAsync($"      Endpoint: {resolved.BaseUrl} ({resolved.SelectionSummary})").ConfigureAwait(false);
            await _log.WriteLineAsync($"      API key source: {resolved.ApiKeySource}").ConfigureAwait(false);
            await _log.WriteLineAsync($"      Gateway token source: {resolved.GatewayTokenSource}").ConfigureAwait(false);
            await _log.WriteLineAsync($"      OK HTTP client built. BaseAddress={client.BaseAddress}").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var msg = $"Failed to build HTTP client: {ex.Message}";
            await _log.WriteLineAsync($"   x {msg}").ConfigureAwait(false);
            return Fail(msg, ex);
        }

        try
        {
            await _log.WriteLineAsync("[3/5] Probing /echo (no auth required)...").ConfigureAwait(false);
            using (var echoResp = await client.GetRawAsync("echo", ct).ConfigureAwait(false))
            {
                var status = (int)echoResp.StatusCode;
                await _log.WriteLineAsync($"      <- HTTP {status} {echoResp.ReasonPhrase}").ConfigureAwait(false);
                if (!echoResp.IsSuccessStatusCode)
                {
                    var msg = $"/echo returned HTTP {status}. The backend is reachable but not healthy. This usually means the API process started but hasn't finished initializing.";
                    await _log.WriteLineAsync($"   x {msg}").ConfigureAwait(false);
                    return Fail(msg);
                }
            }

            await _log.WriteLineAsync("[4/5] Probing /ping (validates X-Api-Key + X-Gateway-Token)...").ConfigureAwait(false);
            using (var pingResp = await client.GetRawAsync("ping", ct).ConfigureAwait(false))
            {
                var status = (int)pingResp.StatusCode;
                await _log.WriteLineAsync($"      <- HTTP {status} {pingResp.ReasonPhrase}").ConfigureAwait(false);
                if (!pingResp.IsSuccessStatusCode)
                {
                    var hint = InterpretAuthFailure(pingResp.StatusCode, chosen);
                    var msg = $"/ping failed with HTTP {status}. {hint}";
                    await _log.WriteLineAsync($"   x {msg}").ConfigureAwait(false);
                    return Fail(msg);
                }
            }

            await _log.WriteLineAsync("[5/5] Loading /channel-contexts to confirm session is usable...").ConfigureAwait(false);
            try
            {
                var contexts = await client.GetAsync<List<ContextDto>>("channel-contexts", ct).ConfigureAwait(false)
                    ?? new List<ContextDto>();
                await _log.WriteLineAsync($"      OK Loaded {contexts.Count} context(s).").ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                var msg = $"Loading contexts failed: {ex.Message}. Authentication succeeded but a domain endpoint is failing - the backend may be in a partially-initialized state.";
                await _log.WriteLineAsync($"   x {msg}").ConfigureAwait(false);
                return Fail(msg, ex);
            }
            catch (Exception ex)
            {
                await _log.WriteLineAsync($"   x {ex.Message}").ConfigureAwait(false);
                return Fail(ex.Message, ex);
            }

            _backend.SetClient(client);
            published = true;

            await _log.WriteLineAsync("OK Connected. SharpClaw backend is reachable and authenticated.").ConfigureAwait(false);
            await _log.WriteLineAsync("------------------------------------------------").ConfigureAwait(false);
            return new SharpClawConnectionResult { Success = true, Summary = "Connected" };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var msg = $"Connect probe threw an exception: {ex.Message}";
            await _log.WriteLineAsync($"   x {msg}").ConfigureAwait(false);
            return Fail(msg, ex);
        }
        finally
        {
            if (!published)
                client?.Dispose();
        }
    }

    private SharpClawConnectionResult Fail(string summary, Exception? ex = null) => new()
    {
        Success = false,
        Summary = summary,
        Error = ex,
    };

    private static int FindIndex(IReadOnlyList<SharpClawDiscoveryEntry> entries, SharpClawDiscoveryEntry chosen)
    {
        for (var i = 0; i < entries.Count; i++)
        {
            if (ReferenceEquals(entries[i], chosen))
                return i;
        }

        return -1;
    }

    private static string Short(string? id) =>
        SharpClawConnectionOptionsStore.Short(id);

    private static string InterpretAuthFailure(HttpStatusCode status, SharpClawDiscoveryEntry? entry) => status switch
    {
        HttpStatusCode.Unauthorized =>
            "The X-Api-Key was rejected (401). The selected API key may not match what the " +
            "running backend currently considers valid. Try reconnecting or checking the options-page key overrides. " +
            $"(instance={Short(entry?.InstanceId)}, key file={entry?.ApiKeyFilePath ?? "<override/manual>"})",
        HttpStatusCode.Forbidden =>
            "The API key was accepted but the request was forbidden (403). Check whether the gateway token " +
            $"({entry?.GatewayTokenFilePath ?? "<override/manual/none>"}) matches what the backend expects.",
        HttpStatusCode.Locked =>
            "The endpoint returned 423 Locked. Your instance id may not have saved the correct API key, or " +
            "the wrong backend instance was selected. Verify the selected discovery entry or manual overrides.",
        HttpStatusCode.TooManyRequests =>
            "The endpoint returned 429 Too Many Requests. The local rate limiter tripped; wait a minute and retry.",
        HttpStatusCode.ServiceUnavailable =>
            "The endpoint returned 503 Service Unavailable. The backend is up but not yet ready to serve traffic.",
        _ => $"Unexpected status {(int)status} ({status}). Check the backend logs for details.",
    };
}
