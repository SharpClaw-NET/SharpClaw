using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace SharpClaw.VS2026Extension.Services;

/// <summary>
/// Lazily resolves and shares a single <see cref="SharpClawHttpClient"/> across
/// the extension's commands and tool windows. The backend is rediscovered on demand
/// so the extension remains usable across SharpClaw process restarts.
/// </summary>
internal sealed class SharpClawBackend : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SharpClawOutputLog _log;
    private readonly SharpClawConnectionOptionsStore _optionsStore;
    private readonly List<SharpClawHttpClient> _retiredClients = new();
    private SharpClawHttpClient? _client;
    private bool _validatedConnection;

    public SharpClawBackend(SharpClawOutputLog log, SharpClawConnectionOptionsStore optionsStore)
    {
        _log = log;
        _optionsStore = optionsStore;
    }

    public async Task<SharpClawHttpClient> GetClientAsync(CancellationToken ct = default)
    {
        await _log.WriteLineAsync("Backend.GetClientAsync: acquiring client.").ConfigureAwait(false);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_client is not null)
            {
                await _log.WriteLineAsync($"Backend.GetClientAsync: using cached client BaseAddress={_client.BaseAddress}.").ConfigureAwait(false);
                return _client;
            }

            await _log.WriteLineAsync("Backend.GetClientAsync: resolving backend client from saved options/discovery.").ConfigureAwait(false);
            var options = _optionsStore.Load();
            var entries = _optionsStore.EnumerateDetectedInstances(options.PreferAliveInstances);
            var entry = _optionsStore.SelectEntry(options, entries);
            var resolved = _optionsStore.BuildClient(options, entry);
            _client = resolved.Client;
            await _log.WriteLineAsync(
                $"Backend.GetClientAsync: resolved BaseAddress={_client.BaseAddress} ({resolved.SelectionSummary}).")
                .ConfigureAwait(false);
            return _client;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Reset()
    {
        _ = _log.WriteLineAsync("Backend.Reset: resetting cached client.");
        var wasConnected = IsConnected;
        _gate.Wait();
        try
        {
            if (_client is not null)
                _retiredClients.Add(_client);
            _client = null;
            _validatedConnection = false;
        }
        finally
        {
            _gate.Release();
        }

        if (wasConnected)
        {
            try { Disconnected?.Invoke(this, EventArgs.Empty); }
            catch { /* never let a UI handler break disconnect */ }
        }
    }

    /// <summary>
    /// Installs an already-built client (used by the verbose connector so it
    /// can narrate every discovery step before publishing the client).
    /// </summary>
    public SharpClawHttpClient SetClient(SharpClawHttpClient client)
    {
        _ = _log.WriteLineAsync($"Backend.SetClient: publishing client BaseAddress={client.BaseAddress}.");
        _gate.Wait();
        try
        {
            if (_client is not null)
                _retiredClients.Add(_client);
            _client = client;
            _validatedConnection = true;
        }
        finally
        {
            _gate.Release();
        }

        // Notify subscribers (chat tool window) outside the lock so the
        // chat view model can refresh its selectors immediately when the
        // verbose Connect flow finishes â€” without waiting on its own
        // periodic refresh tick or a manual Refresh click.
        try { Connected?.Invoke(this, EventArgs.Empty); }
        catch { /* never let a UI handler break connect */ }

        return client;
    }

    /// <summary>True after the verbose connector has successfully validated and published a client.</summary>
    public bool IsConnected => _validatedConnection && _client is not null;

    /// <summary>
    /// Raised on the thread that completed <see cref="SetClient"/> after a new
    /// HTTP client has been installed (i.e. after a successful Connect).
    /// Subscribers are expected to be cheap â€” heavy work should be queued
    /// onto a background task by the handler.
    /// </summary>
    public event EventHandler? Connected;

    /// <summary>Raised after the cached validated client has been cleared.</summary>
    public event EventHandler? Disconnected;

    // ── High-level chat operations ────────────────────────────────

    public async Task<IReadOnlyList<ContextDto>> GetContextsAsync(CancellationToken ct)
    {
        await _log.WriteLineAsync("Backend.GetContextsAsync: GET channel-contexts.").ConfigureAwait(false);
        var c = await GetClientAsync(ct).ConfigureAwait(false);
        var result = await c.GetAsync<List<ContextDto>>("channel-contexts", ct).ConfigureAwait(false)
            ?? new List<ContextDto>();
        await _log.WriteLineAsync($"Backend.GetContextsAsync: returned {result.Count}.").ConfigureAwait(false);
        return result;
    }

    public async Task<IReadOnlyList<ChannelDto>> GetChannelsAsync(CancellationToken ct)
    {
        await _log.WriteLineAsync("Backend.GetChannelsAsync: GET channels.").ConfigureAwait(false);
        var c = await GetClientAsync(ct).ConfigureAwait(false);
        var result = await c.GetAsync<List<ChannelDto>>("channels", ct).ConfigureAwait(false)
            ?? new List<ChannelDto>();
        await _log.WriteLineAsync($"Backend.GetChannelsAsync: returned {result.Count}.").ConfigureAwait(false);
        return result;
    }

    public async Task<IReadOnlyList<ThreadDto>> GetThreadsAsync(Guid channelId, CancellationToken ct)
    {
        await _log.WriteLineAsync($"Backend.GetThreadsAsync: GET channels/{channelId}/threads.").ConfigureAwait(false);
        var c = await GetClientAsync(ct).ConfigureAwait(false);
        var result = await c.GetAsync<List<ThreadDto>>($"channels/{channelId}/threads", ct).ConfigureAwait(false)
            ?? new List<ThreadDto>();
        await _log.WriteLineAsync($"Backend.GetThreadsAsync: returned {result.Count} for channel={channelId}.").ConfigureAwait(false);
        return result;
    }

    /// <summary>Creates a new thread on the given channel. Only a name is required.</summary>
    public async Task<ThreadDto?> CreateThreadAsync(Guid channelId, string name, CancellationToken ct)
    {
        await _log.WriteLineAsync($"Backend.CreateThreadAsync: POST channels/{channelId}/threads name='{name}'.").ConfigureAwait(false);
        var c = await GetClientAsync(ct).ConfigureAwait(false);
        // Backend's CreateThreadRequest uses { Name, MaxMessages?, MaxCharacters?, CustomId? }.
        var result = await c.PostJsonAsync<ThreadDto>(
            $"channels/{channelId}/threads",
            new { name },
            ct).ConfigureAwait(false);
        await _log.WriteLineAsync($"Backend.CreateThreadAsync: returned id={result?.Id.ToString() ?? "<null>"}.").ConfigureAwait(false);
        return result;
    }

    public async Task<IReadOnlyList<ChatMessageDto>> GetHistoryAsync(
        Guid channelId, Guid? threadId, CancellationToken ct)
    {
        await _log.WriteLineAsync($"Backend.GetHistoryAsync: GET history channel={channelId} thread={threadId?.ToString() ?? "<none>"}.").ConfigureAwait(false);
        var c = await GetClientAsync(ct).ConfigureAwait(false);
        // Backend route group is /channels/{id}/chat with [MapGet] returning
        // the channel-level history, and a thread-scoped variant under
        // /channels/{id}/chat/threads/{threadId}.
        var path = threadId is null
            ? $"channels/{channelId}/chat"
            : $"channels/{channelId}/chat/threads/{threadId}";
        var result = await c.GetAsync<List<ChatMessageDto>>(path, ct).ConfigureAwait(false)
            ?? new List<ChatMessageDto>();
        await _log.WriteLineAsync($"Backend.GetHistoryAsync: returned {result.Count} from {path}.").ConfigureAwait(false);
        return result;
    }

    public async Task<HttpResponseMessage> StartChatStreamAsync(
        Guid channelId, Guid? threadId, string message, CancellationToken ct)
    {
        await _log.WriteLineAsync($"Backend.StartChatStreamAsync: POST stream channel={channelId} thread={threadId?.ToString() ?? "<none>"} messageLength={message?.Length ?? 0}.").ConfigureAwait(false);
        var c = await GetClientAsync(ct).ConfigureAwait(false);
        // SSE endpoints live under the /chat route group:
        //   POST /channels/{id}/chat/stream
        //   POST /channels/{id}/chat/threads/{threadId}/stream
        var path = threadId is null
            ? $"channels/{channelId}/chat/stream"
            : $"channels/{channelId}/chat/threads/{threadId}/stream";
        var body = new ChatRequestDto { Message = message ?? string.Empty };
        return await c.PostStreamAsync(path, body, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Opens the SSE thread-activity watch. Emits <c>Processing</c> when any
    /// client (including this one) acquires the per-thread send lock, and
    /// <c>NewMessages</c> when new messages have been persisted. Used to
    /// keep the active thread in sync with edits coming from other clients
    /// and to gate Send while another client is mid-stream.
    /// </summary>
    public async Task<HttpResponseMessage> StartThreadWatchAsync(
        Guid channelId, Guid threadId, CancellationToken ct)
    {
        await _log.WriteLineAsync($"Backend.StartThreadWatchAsync: GET watch channel={channelId} thread={threadId}.").ConfigureAwait(false);
        var c = await GetClientAsync(ct).ConfigureAwait(false);
        return await c.GetStreamAsync(
            $"channels/{channelId}/chat/threads/{threadId}/watch", ct).ConfigureAwait(false);
    }

    public void Dispose()
    {
        _client?.Dispose();
        foreach (var client in _retiredClients)
            client.Dispose();
        _retiredClients.Clear();
        _gate.Dispose();
    }
}
