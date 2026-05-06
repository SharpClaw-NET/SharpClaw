using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.Serialization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Extensibility.UI;
using SharpClaw.VS2026Extension.Services;

namespace SharpClaw.VS2026Extension.ToolWindows;

/// <summary>
/// Selectable item in the Context / Channel / Thread strip. Wraps an optional
/// backend identifier; <see cref="Id"/> is <see langword="null"/> for sentinel
/// rows like <c>[No Context]</c> or <c>[No Thread]</c>.
/// </summary>
[DataContract]
internal sealed class SharpClawSelectorItem
{
    public SharpClawSelectorItem(Guid? id, string displayName)
    {
        Id = id;
        DisplayName = displayName;
    }

    [DataMember] public Guid? Id { get; }
    [DataMember] public string DisplayName { get; }

    public override string ToString() => DisplayName;
}

/// <summary>
/// Identifies how a <see cref="SharpClawChatTurn"/> should be rendered.
/// Mirrors the role/sender-aware bubbles used by the Uno frontend so the
/// XAML template selector can right-align user turns, dim system turns,
/// and visually mark tool/approval activity.
/// </summary>
internal enum SharpClawTurnKind
{
    User,
    Assistant,
    System,
    Tool,
}

/// <summary>
/// One entry in the chat transcript shown in the tool window. Carries enough
/// metadata to drive the bubble template (alignment, label color) and to
/// support live-updating assistant streaming via <see cref="Body"/>.
/// </summary>
[DataContract]
internal sealed class SharpClawChatTurn : NotifyPropertyChangedObject
{
    private string _body;

    public SharpClawChatTurn(SharpClawTurnKind kind, string sender, string body, string? timestamp = null, bool isRemoteUser = false)
    {
        Kind = kind;
        Sender = sender;
        _body = body ?? string.Empty;
        Timestamp = timestamp ?? string.Empty;
        IsRemoteUser = isRemoteUser;
    }

    [DataMember] public SharpClawTurnKind Kind { get; }
    [DataMember] public string Sender { get; }
    [DataMember] public string Timestamp { get; }
    /// <summary>True for user messages that originated from a different client (e.g. CLI/Uno).</summary>
    [DataMember] public bool IsRemoteUser { get; }
    /// <summary>True for user messages from this VS2026 instance specifically.</summary>
    [DataMember] public bool IsLocalUser => Kind == SharpClawTurnKind.User && !IsRemoteUser;

    [DataMember]
    public string Body
    {
        get => _body;
        set => SetProperty(ref _body, value ?? string.Empty);
    }

    // Convenience flags for XAML triggers â€” Remote UI bindings can't run
    // converters, so we surface the discriminator as boolean properties.
    [DataMember] public bool IsUser => Kind == SharpClawTurnKind.User;
    [DataMember] public bool IsAssistant => Kind == SharpClawTurnKind.Assistant;
    [DataMember] public bool IsSystem => Kind == SharpClawTurnKind.System;
    [DataMember] public bool IsTool => Kind == SharpClawTurnKind.Tool;
}

/// <summary>
/// Stable identifier this VS2026 instance writes into <c>ChatRequest.ClientType</c>.
/// Used both when sending and when classifying history into local-vs-remote
/// user bubbles for sender-aware coloring.
/// </summary>
internal static class SharpClawClientType
{
    public const string Value = "VS2026";
}

/// <summary>
/// Data context backing <see cref="SharpClawChatControl"/>. Owns the
/// Context â†’ Channel â†’ Thread cascade, the transcript, the composer text,
/// and the async commands the XAML binds to.
/// </summary>
[DataContract]
internal sealed class SharpClawChatViewModel : NotifyPropertyChangedObject
{
    private static readonly SharpClawSelectorItem NoContext = new(null, "[No Context]");
    private static readonly SharpClawSelectorItem NoChannels = new(null, "[No Channel]");
    private static readonly SharpClawSelectorItem NoThread = new(null, "[No Thread]");

    private readonly SharpClawBackend _backend;
    private readonly SharpClawOutputLog _log;

    private SharpClawSelectorItem? _selectedContext;
    private SharpClawSelectorItem? _selectedChannel;
    private SharpClawSelectorItem? _selectedThread;
    private string _composer = string.Empty;
    private string _status = "Idle";
    private bool _isBusy;
    private string _newThreadName = string.Empty;
    private CancellationTokenSource? _periodicCts;
    private CancellationTokenSource? _watchCts;
    private Guid? _watchedChannelId;
    private Guid? _watchedThreadId;
    private bool _isThreadBusy;
    private bool _isSending;
    private bool _historyStaleAfterSend;
    private bool _initialized;

    // Serializes all selector loads (Refresh + cascading Reload*). Without
    // this, a periodic refresh racing with a user-triggered selection change
    // mutates the same ObservableList from two threads → ComboBox virtualization
    // crashes Visual Studio (notably opening the Thread dropdown after a
    // channel changes).
    private readonly SemaphoreSlim _loadGate = new(1, 1);

    // When >0, the SelectedContext/Channel/Thread setters skip their cascading
    // reload. We bump this around programmatic re-selection (e.g. after
    // Clear()+restore in RefreshAllAsync) so WPF's automatic SelectedItem=null
    // (raised when the bound item leaves the collection) doesn't trigger a
    // second, racing reload that fights the one we're already inside of —
    // which is what was leaving "[No X]" rendered as a blank entry and
    // crashing the thread dropdown.
    private int _suppressCascade;

    public SharpClawChatViewModel(SharpClawBackend backend, SharpClawOutputLog log)
    {
        _backend = backend;
        _log = log;

        RefreshCommand = new AsyncCommand(async (_, ct) => await RefreshAllAsync(preserveSelection: false, ct).ConfigureAwait(false));
        SendCommand = new AsyncCommand(async (_, ct) => await SendAsync(ct).ConfigureAwait(false));
        CreateThreadCommand = new AsyncCommand(async (_, ct) => await CreateThreadAsync(ct).ConfigureAwait(false));

        // Seed sentinel-only state so the picker is populated on first paint.
        Contexts.Add(NoContext);
        _selectedContext = NoContext;
        Channels.Add(NoChannels);
        Threads.Add(NoThread);
        _selectedThread = NoThread;

        // When the verbose connector (auto-connect or Tools menu) finishes
        // installing a fresh HTTP client, reload selectors immediately so
        // the chat window doesn't appear "empty" until the user clicks
        // Refresh. Without this, the chat tool window and the connect
        // command effectively maintain two independent connection states.
        _backend.Connected += OnBackendConnected;
    }

    private void OnBackendConnected(object? sender, EventArgs e)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await _log.WriteLineAsync("Backend connected â€” refreshing chat selectors.").ConfigureAwait(false);
                await RefreshAllAsync(preserveSelection: true, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await _log.WriteLineAsync($"Connected-refresh failed: {ex.Message}").ConfigureAwait(false);
            }
        });
    }

    [DataMember] public ObservableList<SharpClawSelectorItem> Contexts { get; } = new();
    [DataMember] public ObservableList<SharpClawSelectorItem> Channels { get; } = new();
    [DataMember] public ObservableList<SharpClawSelectorItem> Threads { get; } = new();
    [DataMember] public ObservableList<SharpClawChatTurn> Transcript { get; } = new();

    /// <summary>Subtitle shown beneath the Context dropdown.</summary>
    [DataMember] public string ContextHint => "Created in SharpClaw (permissions only configurable there)";

    /// <summary>Subtitle shown beneath the Channel dropdown.</summary>
    [DataMember] public string ChannelHint => "Created in SharpClaw (permissions only configurable there)";

    /// <summary>Subtitle shown beneath the Thread dropdown.</summary>
    [DataMember] public string ThreadHint => "Threads can be added here — just give them a name.";

    [DataMember]
    public SharpClawSelectorItem? SelectedContext
    {
        get => _selectedContext;
        set
        {
            if (SetProperty(ref _selectedContext, value) && _suppressCascade == 0)
                _ = ReloadChannelsAsync(CancellationToken.None);
        }
    }

    [DataMember]
    public SharpClawSelectorItem? SelectedChannel
    {
        get => _selectedChannel;
        set
        {
            if (SetProperty(ref _selectedChannel, value) && _suppressCascade == 0)
                _ = ReloadThreadsAsync(CancellationToken.None);
        }
    }

    [DataMember]
    public SharpClawSelectorItem? SelectedThread
    {
        get => _selectedThread;
        set
        {
            if (SetProperty(ref _selectedThread, value) && _suppressCascade == 0)
            {
                _ = ReloadHistoryAsync(CancellationToken.None);
                ReconnectThreadWatch();
            }
        }
    }

    [DataMember]
    public string Composer
    {
        get => _composer;
        set => SetProperty(ref _composer, value ?? string.Empty);
    }

    [DataMember]
    public string Status
    {
        get => _status;
        set => SetProperty(ref _status, value ?? string.Empty);
    }

    [DataMember]
    public bool IsBusy
    {
        get => _isBusy;
        set => SetProperty(ref _isBusy, value);
    }

    /// <summary>
    /// True while the currently-selected thread is being processed by some
    /// client (this one or another). Mirrors the Uno frontend's
    /// <c>_isThreadBusy</c> flag and is driven by the SSE watch endpoint so
    /// the composer disables sending whenever any client is mid-stream.
    /// </summary>
    [DataMember]
    public bool IsThreadBusy
    {
        get => _isThreadBusy;
        private set
        {
            if (SetProperty(ref _isThreadBusy, value))
                SendCommand.CanExecute = ComputeCanSend();
        }
    }

    [DataMember] public IAsyncCommand RefreshCommand { get; }
    // Send is exposed as the concrete AsyncCommand so we can flip CanExecute
    // from the watch loop / stream lifecycle (the IAsyncCommand interface
    // only exposes a getter).
    [DataMember] public AsyncCommand SendCommand { get; }
    [DataMember] public IAsyncCommand CreateThreadCommand { get; }

    [DataMember]
    public string NewThreadName
    {
        get => _newThreadName;
        set => SetProperty(ref _newThreadName, value ?? string.Empty);
    }

    // ── Lifecycle ────────────────────────────────────────────────

    /// <summary>
    /// Called by the tool window when it first becomes visible. Triggers an
    /// initial load and starts the periodic refresh loop so selector lists
    /// stay synchronized with SharpClaw without requiring user interaction.
    /// </summary>
    public void EnsureStarted()
    {
        if (_initialized) return;
        _initialized = true;

        _ = Task.Run(async () =>
        {
            await RefreshAllAsync(preserveSelection: false, CancellationToken.None).ConfigureAwait(false);
            StartPeriodicRefresh();
        });
    }

    private void StartPeriodicRefresh()
    {
        _periodicCts?.Cancel();
        _periodicCts = new CancellationTokenSource();
        var ct = _periodicCts.Token;

        _ = Task.Run(async () =>
        {
            // Refresh every 15 seconds. Cheap enough for local backend, frequent
            // enough that newly-created channels/threads show up promptly.
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
                    await RefreshAllAsync(preserveSelection: true, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    await _log.WriteLineAsync($"Periodic refresh failed: {ex.Message}").ConfigureAwait(false);
                }
            }
        }, ct);
    }

    // ── Thread activity watch ────────────────────────────────────

    /// <summary>
    /// Connects (or reconnects) the SSE watch for the currently-selected
    /// channel + thread pair. The watch surfaces <c>Processing</c> /
    /// <c>NewMessages</c> events from the backend's
    /// <see cref="ThreadActivitySignal"/>, mirroring the Uno frontend so the
    /// transcript stays current and the composer is gated whenever any
    /// other client is mid-stream on the same thread.
    /// </summary>
    private void ReconnectThreadWatch()
    {
        var channelId = SelectedChannel?.Id;
        var threadId = SelectedThread?.Id;

        // No-op when the (channel, thread) pair is unchanged — avoids tearing
        // down a working watch on an unrelated property setter.
        if (channelId == _watchedChannelId && threadId == _watchedThreadId
            && _watchCts is { IsCancellationRequested: false })
            return;

        DisconnectThreadWatch();

        if (channelId is not Guid chId || threadId is not Guid thId)
            return;

        _watchedChannelId = chId;
        _watchedThreadId = thId;

        var cts = new CancellationTokenSource();
        _watchCts = cts;
        _ = Task.Run(() => RunThreadWatchAsync(chId, thId, cts.Token));
    }

    private void DisconnectThreadWatch()
    {
        if (_watchCts is not null)
        {
            try { _watchCts.Cancel(); } catch { /* already disposed */ }
            _watchCts.Dispose();
            _watchCts = null;
        }
        _watchedChannelId = null;
        _watchedThreadId = null;
        if (_isThreadBusy) IsThreadBusy = false;
    }

    private async Task RunThreadWatchAsync(Guid channelId, Guid threadId, CancellationToken ct)
    {
        await _log.WriteLineAsync($"ThreadWatch: connecting channel={channelId} thread={threadId}").ConfigureAwait(false);
        try
        {
            using var resp = await _backend.StartThreadWatchAsync(channelId, threadId, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                await _log.WriteLineAsync($"ThreadWatch: HTTP {(int)resp.StatusCode}").ConfigureAwait(false);
                return;
            }

            using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var reader = new StreamReader(stream);

            string? eventName = null;
            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (line is null) break;
                if (line.Length == 0) { eventName = null; continue; }

                if (line.StartsWith("event: ", StringComparison.Ordinal))
                {
                    eventName = line.Substring(7);
                }
                else if (line.StartsWith("data: ", StringComparison.Ordinal) && eventName is not null)
                {
                    if (eventName == "Processing")
                    {
                        IsThreadBusy = true;
                    }
                    else if (eventName == "NewMessages")
                    {
                        IsThreadBusy = false;
                        if (_isSending)
                        {
                            // Another client raced us; reload after our own
                            // stream completes so we don't clobber the live
                            // assistant bubble.
                            _historyStaleAfterSend = true;
                        }
                        else
                        {
                            await ReloadHistoryAsync(ct).ConfigureAwait(false);
                        }
                    }
                    eventName = null;
                }
            }
        }
        catch (OperationCanceledException) { /* normal on selection change / shutdown */ }
        catch (Exception ex)
        {
            await _log.WriteLineAsync($"ThreadWatch: {ex.GetType().Name}: {ex.Message}").ConfigureAwait(false);
        }
    }

    private bool ComputeCanSend() => !_isSending && !_isThreadBusy;

    // ── Loaders ──────────────────────────────────────────────────

    public async Task RefreshAllAsync(bool preserveSelection, CancellationToken ct)
    {
        var prevContextId = preserveSelection ? SelectedContext?.Id : null;
        var prevChannelId = preserveSelection ? SelectedChannel?.Id : null;
        var prevThreadId = preserveSelection ? SelectedThread?.Id : null;

        await _loadGate.WaitAsync(ct).ConfigureAwait(false);
        _suppressCascade++;
        try
        {
            IsBusy = true;
            if (!preserveSelection)
                Status = "Loading contexts…";

            var contexts = await _backend.GetContextsAsync(ct).ConfigureAwait(false);
            Contexts.Clear();
            Contexts.Add(NoContext);
            foreach (var c in contexts)
                Contexts.Add(new SharpClawSelectorItem(c.Id, c.Name ?? c.Id.ToString()));

            // Restore previously-selected context if it still exists; otherwise
            // fall back to the sentinel so the channels list re-evaluates.
            var restoredContext = prevContextId is Guid cid
                ? FindById(Contexts, cid) ?? NoContext
                : NoContext;
            ForceSelect(ref _selectedContext, restoredContext, nameof(SelectedContext));

            await ReloadChannelsCoreAsync(prevChannelId, prevThreadId, ct).ConfigureAwait(false);

            if (!preserveSelection)
                Status = "Connected";
            await _log.WriteLineAsync(
                $"Refresh: {contexts.Count} context(s), preserveSelection={preserveSelection}.")
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Status = $"Error: {ex.Message}";
            await _log.WriteLineAsync($"Refresh failed: {ex.Message}").ConfigureAwait(false);
        }
        finally
        {
            IsBusy = false;
            _suppressCascade--;
            _loadGate.Release();
        }
    }

    private async Task ReloadChannelsAsync(Guid? preserveChannelId, Guid? preserveThreadId, CancellationToken ct)
    {
        await _loadGate.WaitAsync(ct).ConfigureAwait(false);
        _suppressCascade++;
        try
        {
            await ReloadChannelsCoreAsync(preserveChannelId, preserveThreadId, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Status = $"Error: {ex.Message}";
            await _log.WriteLineAsync($"ReloadChannels failed: {ex.Message}").ConfigureAwait(false);
        }
        finally
        {
            _suppressCascade--;
            _loadGate.Release();
        }
    }

    /// <summary>
    /// Channel reload body. Caller MUST hold <see cref="_loadGate"/> and have
    /// incremented <see cref="_suppressCascade"/> — this lets RefreshAll chain
    /// into channel/thread reloads without releasing/reacquiring the gate
    /// (which would let a periodic refresh tick race in between).
    /// </summary>
    private async Task ReloadChannelsCoreAsync(Guid? preserveChannelId, Guid? preserveThreadId, CancellationToken ct)
    {
        var all = await _backend.GetChannelsAsync(ct).ConfigureAwait(false);

        Channels.Clear();
        Channels.Add(NoChannels);
        foreach (var ch in all)
        {
            if (SelectedContext?.Id is Guid ctxId && ch.ContextId != ctxId)
                continue;
            var label = string.IsNullOrWhiteSpace(ch.Title) ? ch.Id.ToString() : ch.Title;
            Channels.Add(new SharpClawSelectorItem(ch.Id, label));
        }

        var restored = preserveChannelId is Guid cid
            ? FindById(Channels, cid) ?? NoChannels
            : NoChannels;
        ForceSelect(ref _selectedChannel, restored, nameof(SelectedChannel));

        await ReloadThreadsCoreAsync(preserveThreadId, ct).ConfigureAwait(false);
    }

    private async Task ReloadThreadsAsync(Guid? preserveThreadId, CancellationToken ct)
    {
        await _loadGate.WaitAsync(ct).ConfigureAwait(false);
        _suppressCascade++;
        try
        {
            await ReloadThreadsCoreAsync(preserveThreadId, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Status = $"Error: {ex.Message}";
            await _log.WriteLineAsync($"ReloadThreads failed: {ex.Message}").ConfigureAwait(false);
        }
        finally
        {
            _suppressCascade--;
            _loadGate.Release();
        }

        // History/watch reconnect happen outside the gate so a slow history
        // fetch doesn't block subsequent selector refreshes.
        await ReloadHistoryAsync(ct).ConfigureAwait(false);
        ReconnectThreadWatch();
    }

    /// <summary>
    /// Thread reload body. Same locking contract as
    /// <see cref="ReloadChannelsCoreAsync"/>: caller holds the load gate and
    /// has bumped <see cref="_suppressCascade"/>.
    /// </summary>
    private async Task ReloadThreadsCoreAsync(Guid? preserveThreadId, CancellationToken ct)
    {
        Threads.Clear();
        Threads.Add(NoThread);

        if (SelectedChannel?.Id is Guid channelId)
        {
            var threads = await _backend.GetThreadsAsync(channelId, ct).ConfigureAwait(false);
            foreach (var t in threads)
                Threads.Add(new SharpClawSelectorItem(t.Id, t.Name ?? t.Id.ToString()));
        }

        var restored = preserveThreadId is Guid tid
            ? FindById(Threads, tid) ?? NoThread
            : NoThread;
        ForceSelect(ref _selectedThread, restored, nameof(SelectedThread));
    }

    /// <summary>
    /// Programmatically pin a selector to <paramref name="value"/> without
    /// triggering its cascading reload. Always raises PropertyChanged (even
    /// when the reference is unchanged) so the WPF ComboBox re-syncs after a
    /// Clear() that auto-nulled its SelectedItem — which is what was leaving
    /// the visual "[No X]" entry blank after a refresh.
    /// </summary>
    private void ForceSelect(ref SharpClawSelectorItem? field, SharpClawSelectorItem? value, string propertyName)
    {
        field = value;
        RaiseNotifyPropertyChangedEvent(propertyName);
    }

    private static SharpClawSelectorItem? FindById(ObservableList<SharpClawSelectorItem> list, Guid id)
    {
        foreach (var item in list)
            if (item.Id == id) return item;
        return null;
    }

    // ── Selector-driven loaders (manual changes) ─────────────────

    private async Task ReloadChannelsAsync(CancellationToken ct)
        => await ReloadChannelsAsync(preserveChannelId: null, preserveThreadId: null, ct).ConfigureAwait(false);

    private async Task ReloadThreadsAsync(CancellationToken ct)
        => await ReloadThreadsAsync(preserveThreadId: null, ct).ConfigureAwait(false);

    // ── Thread creation ──────────────────────────────────────────

    public async Task CreateThreadAsync(CancellationToken ct)
    {
        if (SelectedChannel?.Id is not Guid channelId)
        {
            Status = "Select a channel before creating a thread.";
            return;
        }

        var name = NewThreadName?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            Status = "Thread needs a name.";
            return;
        }

        try
        {
            IsBusy = true;
            Status = $"Creating thread '{name}'…";
            await _log.WriteLineAsync($"Creating thread '{name}' on channel {channelId}…").ConfigureAwait(false);

            var created = await _backend.CreateThreadAsync(channelId, name!, ct).ConfigureAwait(false);
            NewThreadName = string.Empty;

            // Reload the thread list and select the new one if we got an id back.
            await ReloadThreadsAsync(created?.Id, ct).ConfigureAwait(false);
            Status = created is null ? "Thread created." : $"Thread '{name}' created.";
        }
        catch (Exception ex)
        {
            Status = $"Create thread failed: {ex.Message}";
            await _log.WriteLineAsync($"CreateThread failed: {ex}").ConfigureAwait(false);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ReloadHistoryAsync(CancellationToken ct)
    {
        try
        {
            Transcript.Clear();
            if (SelectedChannel?.Id is not Guid channelId)
                return;

            IsBusy = true;
            var history = await _backend.GetHistoryAsync(channelId, SelectedThread?.Id, ct).ConfigureAwait(false);
            foreach (var m in history)
                Transcript.Add(BuildTurnFromHistory(m));
        }
        catch (Exception ex)
        {
            Status = $"Error: {ex.Message}";
            await _log.WriteLineAsync($"ReloadHistory failed: {ex}").ConfigureAwait(false);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static SharpClawChatTurn BuildTurnFromHistory(ChatMessageDto m)
    {
        var role = m.Role ?? "assistant";
        var kind = role.Equals("user", StringComparison.OrdinalIgnoreCase) ? SharpClawTurnKind.User
                 : role.Equals("system", StringComparison.OrdinalIgnoreCase) ? SharpClawTurnKind.System
                 : SharpClawTurnKind.Assistant;

        var sender = kind switch
        {
            SharpClawTurnKind.User => m.SenderUsername ?? "user",
            SharpClawTurnKind.System => "system",
            _ => m.SenderAgentName ?? "assistant",
        };
        if (kind == SharpClawTurnKind.User && !string.IsNullOrEmpty(m.ClientType))
            sender = $"{sender} ({m.ClientType})";

        // A user message is "remote" when it was authored by any client other
        // than this VS2026 instance. The chat bubble template uses this flag
        // to paint a different (blue) background so cross-client activity is
        // visually distinct from the local "you" turns.
        var isRemoteUser = kind == SharpClawTurnKind.User
            && !string.IsNullOrEmpty(m.ClientType)
            && !string.Equals(m.ClientType, SharpClawClientType.Value, StringComparison.OrdinalIgnoreCase);

        var ts = m.Timestamp == default ? string.Empty : m.Timestamp.LocalDateTime.ToString("HH:mm");
        return new SharpClawChatTurn(kind, sender, m.Content ?? string.Empty, ts, isRemoteUser);
    }

    // â”€â”€ Send â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    public async Task SendAsync(CancellationToken ct)
    {
        if (SelectedChannel?.Id is not Guid channelId)
        {
            Status = "Select a channel before sending.";
            return;
        }

        if (_isSending || _isThreadBusy)
        {
            Status = _isThreadBusy
                ? "Another client is streaming on this thread — wait for it to finish."
                : "A send is already in progress.";
            return;
        }

        var text = Composer?.Trim();
        if (string.IsNullOrEmpty(text))
            return;

        Composer = string.Empty;
        var nowStamp = DateTimeOffset.Now.LocalDateTime.ToString("HH:mm");
        Transcript.Add(new SharpClawChatTurn(SharpClawTurnKind.User, "you (VS2026)", text!, nowStamp));

        // Live assistant bubble updated as text deltas arrive. We mirror the
        // Uno cursor convention so partial responses look alive.
        var assistant = new SharpClawChatTurn(SharpClawTurnKind.Assistant, "assistant", "â–", nowStamp);
        Transcript.Add(assistant);

        var streamed = new StringBuilder();
        var needsNewline = false;

        try
        {
            IsBusy = true;
            _isSending = true;
            _historyStaleAfterSend = false;
            SendCommand.CanExecute = false;
            Status = "Sendingâ€¦";

            using var response = await _backend
                .StartChatStreamAsync(channelId, SelectedThread?.Id, text!, ct)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                assistant.Body = $"âœ— {(int)response.StatusCode} {response.ReasonPhrase}";
                Status = $"Error: {(int)response.StatusCode}";
                return;
            }

            await foreach (var ev in ChatStreamReader.ReadAsync(response, ct).ConfigureAwait(false))
            {
                switch (ev.Type)
                {
                    case ChatStreamEventType.TextDelta:
                        if (!string.IsNullOrEmpty(ev.Delta))
                        {
                            if (needsNewline)
                            {
                                streamed.Append('\n');
                                needsNewline = false;
                            }
                            streamed.Append(ev.Delta);
                            assistant.Body = streamed.ToString() + "â–";
                        }
                        break;

                    case ChatStreamEventType.ToolCallStart:
                        streamed.Append($"\nâš™ [{ev.ToolName ?? "tool"}] â†’ {ev.ToolStatus ?? "started"}");
                        needsNewline = true;
                        assistant.Body = streamed.ToString() + "â–";
                        break;

                    case ChatStreamEventType.ToolCallResult:
                        streamed.Append($"\nâš™ [{ev.ToolName ?? "tool"}] â†’ {ev.ToolStatus ?? "done"}");
                        needsNewline = true;
                        assistant.Body = streamed.ToString() + "â–";
                        break;

                    case ChatStreamEventType.ApprovalRequired:
                        streamed.Append($"\nâ³ [{ev.ToolName ?? "action"}] awaiting approval");
                        needsNewline = true;
                        assistant.Body = streamed.ToString() + "â–";
                        break;

                    case ChatStreamEventType.ApprovalResult:
                        streamed.Append($"\nâš™ [{ev.ToolName ?? "action"}] â†’ {ev.ToolStatus ?? "resolved"}");
                        needsNewline = true;
                        assistant.Body = streamed.ToString() + "â–";
                        break;

                    case ChatStreamEventType.Error:
                        Status = $"Error: {ev.Error}";
                        assistant.Body = streamed.Length > 0
                            ? streamed.ToString() + $"\nâœ— {ev.Error}"
                            : $"âœ— {ev.Error}";
                        return;

                    case ChatStreamEventType.Done:
                        // Prefer the authoritative final text from the Done payload;
                        // fall back to the streamed buffer if absent.
                        if (!string.IsNullOrEmpty(ev.FinalText))
                            assistant.Body = ev.FinalText!;
                        else if (streamed.Length > 0)
                            assistant.Body = streamed.ToString();
                        else
                            assistant.Body = "(empty response)";
                        Status = "Idle";
                        return;
                }
            }

            // Stream ended without an explicit Done event.
            assistant.Body = streamed.Length > 0 ? streamed.ToString() : "(no response)";
        }
        catch (OperationCanceledException)
        {
            assistant.Body = streamed.Length > 0 ? streamed.ToString() : "(cancelled)";
        }
        catch (Exception ex)
        {
            Status = $"Error: {ex.Message}";
            assistant.Body = streamed.Length > 0
                ? streamed.ToString() + $"\nâœ— {ex.Message}"
                : $"âœ— {ex.Message}";
            await _log.WriteLineAsync($"Send failed: {ex}").ConfigureAwait(false);
        }
        finally
        {
            IsBusy = false;
            _isSending = false;
            SendCommand.CanExecute = ComputeCanSend();

            // Another client posted while we were streaming — refresh now so
            // the transcript reflects the merged history without clobbering
            // our just-rendered assistant bubble mid-stream.
            if (_historyStaleAfterSend)
            {
                _historyStaleAfterSend = false;
                try { await ReloadHistoryAsync(CancellationToken.None).ConfigureAwait(false); }
                catch { /* best-effort */ }
            }
        }
    }
}
