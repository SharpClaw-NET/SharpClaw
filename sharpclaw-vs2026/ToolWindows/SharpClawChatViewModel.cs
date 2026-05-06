using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Extensibility.UI;
using SharpClaw.VS2026Extension.Services;

namespace SharpClaw.VS2026Extension.ToolWindows;

/// <summary>
/// Awaitable that, when awaited, resumes the continuation on the captured
/// <see cref="SynchronizationContext"/> (the sticky
/// <c>NonConcurrentSynchronizationContext</c> owned by
/// <see cref="SharpClawChatControl"/>). Used by the view model to guarantee
/// that every mutation of an <c>ObservableList</c>, <c>SelectedXxx</c>, or
/// <c>Status</c> property happens on a single, strictly serialized execution
/// context — even when the work was spawned from a thread-pool callback
/// (periodic refresh, SSE watch, reconnect notification).
/// </summary>
internal readonly struct SyncContextAwaitable : INotifyCompletion
{
    private readonly SynchronizationContext? _ctx;
    public SyncContextAwaitable(SynchronizationContext? ctx) { _ctx = ctx; }
    public SyncContextAwaitable GetAwaiter() => this;
    public bool IsCompleted => _ctx is null || ReferenceEquals(SynchronizationContext.Current, _ctx);
    public void OnCompleted(Action continuation)
    {
        if (_ctx is null) continuation();
#pragma warning disable VSTHRD001 // _ctx is the Remote UI NonConcurrentSynchronizationContext, not the VS main thread.
        else _ctx.Post(static s => ((Action)s!)(), continuation);
#pragma warning restore VSTHRD001
    }
    public void GetResult() { }
}

/// <summary>
/// Selectable item in the Context / Channel / Thread strip. Wraps an optional
/// backend identifier; the view model uses <see cref="Guid.Empty"/> for
/// sentinel rows like <c>[No Context]</c> or <c>[No Thread]</c> so WPF
/// <c>SelectedValue</c> can distinguish an explicit sentinel selection from
/// a missing selection.
///
/// <para><see cref="DisplayName"/> is mutable + INPC so periodic refreshes
/// can update a renamed channel/thread label in place without replacing the
/// item instance. Replacing instances would invalidate the WPF ComboBox's
/// reference-based <c>SelectedItem</c> and cause the picker to render blank
/// after every refresh.</para>
/// </summary>
[DataContract]
internal sealed class SharpClawSelectorItem : NotifyPropertyChangedObject
{
    private string _displayName;

    public SharpClawSelectorItem(Guid? id, string displayName)
    {
        Id = id;
        _displayName = displayName ?? string.Empty;
    }

    [DataMember] public Guid? Id { get; }

    [DataMember]
    public string DisplayName
    {
        get => _displayName;
        set => SetProperty(ref _displayName, value ?? string.Empty);
    }

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
    private static readonly Guid SentinelId = Guid.Empty;
    private static readonly SharpClawSelectorItem NoContext = new(SentinelId, "[No Context]");
    private static readonly SharpClawSelectorItem NoChannels = new(SentinelId, "[No Channel]");
    private static readonly SharpClawSelectorItem NoThread = new(SentinelId, "[No Thread]");

    private readonly SharpClawBackend _backend;
    private readonly SharpClawOutputLog _log;
    private readonly SynchronizationContext? _uiContext;

    private SharpClawSelectorItem? _selectedContext;
    private SharpClawSelectorItem? _selectedChannel;
    private SharpClawSelectorItem? _selectedThread;
    private Guid? _selectedContextId = SentinelId;
    private Guid? _selectedChannelId = SentinelId;
    private Guid? _selectedThreadId = SentinelId;
    private string _composer = string.Empty;
    private string _status = "Idle";
    private bool _isBusy;
    private string _newThreadName = string.Empty;
    private CancellationTokenSource? _periodicCts;
    private CancellationTokenSource? _watchCts;
    private CancellationTokenSource? _threadActivationCts;
    private Guid? _watchedChannelId;
    private Guid? _watchedThreadId;
    private bool _isThreadBusy;
    private bool _isSending;
    private bool _historyStaleAfterSend;
    private bool _initialized;
    private long _selectionVersion;

    // Re-entrancy guard for the cascading reload chain. Because every state
    // mutation is now serialized on the sticky NonConcurrentSynchronizationContext
    // (see SharpClawChatControl), races between periodic refresh / reconnect /
    // user selection are no longer possible. We only need to suppress the
    // automatic "SelectedItem became null because the previous instance left
    // the collection" callback that WPF raises mid-merge — otherwise it would
    // queue a second cascading reload that fights the one already in flight.
    private int _suppressCascade;

    // True while a refresh chain is executing on the sync context. Subsequent
    // refresh requests collapse into a single "do another pass when done"
    // signal so we never queue an unbounded backlog of background reloads.
    private bool _refreshInFlight;
    private bool _refreshPending;
    private bool _refreshPendingPreserve;

    public SharpClawChatViewModel(SharpClawBackend backend, SharpClawOutputLog log)
        : this(backend, log, ui: null) { }

    public SharpClawChatViewModel(SharpClawBackend backend, SharpClawOutputLog log, SynchronizationContext? ui)
    {
        _backend = backend;
        _log = log;
        _uiContext = ui;

        RefreshCommand = new AsyncCommand(async (_, ct) => await RefreshAllAsync(preserveSelection: true, ct).ConfigureAwait(false));
        SendCommand = new AsyncCommand(async (_, ct) => await SendAsync(ct).ConfigureAwait(false));
        CreateThreadCommand = new AsyncCommand(async (_, ct) => await CreateThreadAsync(ct).ConfigureAwait(false));

        // Seed sentinel-only state so the picker is populated on first paint.
        Contexts.Add(NoContext);
        _selectedContext = NoContext;
        Channels.Add(NoChannels);
        _selectedChannel = NoChannels;
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
        // Hop onto the serialized UI context before touching VM state. This
        // is what makes a successful reconnect authoritatively overwrite a
        // stale "Error: …" subtitle: we set Status synchronously on the same
        // context that the XAML binding reads from, then trigger a refresh
        // that will re-confirm "Connected" once data has loaded.
        _ = Task.Run(async () =>
        {
            try
            {
                await SwitchToUi();
                Status = "Connected — refreshing…";
                await _log.WriteLineAsync("Backend connected — refreshing chat selectors.").ConfigureAwait(false);
                await RefreshAllAsync(preserveSelection: true, CancellationToken.None).ConfigureAwait(false);
                // RefreshAllAsync only sets "Connected" when preserveSelection
                // is false (initial load). For a reconnect we still want to
                // visibly clear any stale error text, so do it here.
                await SwitchToUi();
                if (_status.StartsWith("Error", StringComparison.Ordinal)
                    || _status.StartsWith("Connected — refreshing", StringComparison.Ordinal))
                {
                    Status = "Connected";
                }
            }
            catch (Exception ex)
            {
                await _log.WriteLineAsync($"Connected-refresh failed: {ex.Message}").ConfigureAwait(false);
            }
        });
    }

    /// <summary>
    /// Awaits a hop onto the sticky UI synchronization context. After this
    /// awaits returns, every subsequent statement up to the next true
    /// <c>await</c> on a different context runs serialized with the rest of
    /// the VM's UI work — selector mutations, property setters, command
    /// CanExecute toggles, etc. This is the single primitive that replaces
    /// the old <c>SemaphoreSlim</c>-based load gate.
    /// </summary>
    private SyncContextAwaitable SwitchToUi() => new(_uiContext);

    private static Guid? NormalizeSelectorId(Guid? id)
        => id is Guid value ? value : SentinelId;

    private static bool IsRealId(Guid? id)
        => id is Guid value && value != SentinelId;

    private static Guid? RealIdOrNull(Guid? id)
        => IsRealId(id) ? id : null;

    private bool SetSelectorId(ref Guid? field, Guid? value, string propertyName, bool forceNotify = false)
    {
        value = NormalizeSelectorId(value);
        if (field == value)
        {
            if (forceNotify)
                RaiseNotifyPropertyChangedEvent(propertyName);
            return false;
        }

        field = value;
        RaiseNotifyPropertyChangedEvent(propertyName);
        return true;
    }

    private void SyncSelectedItemFromId(
        ref SharpClawSelectorItem? field,
        ObservableList<SharpClawSelectorItem> list,
        Guid? id,
        SharpClawSelectorItem sentinel,
        string propertyName)
    {
        var item = IsRealId(id) ? FindById(list, id!.Value) ?? sentinel : sentinel;
        if (!ReferenceEquals(field, item))
        {
            field = item;
            RaiseNotifyPropertyChangedEvent(propertyName);
        }
    }

    private void ResetChannelAndThreadSelection()
    {
        _suppressCascade++;
        try
        {
            SetSelectorId(ref _selectedChannelId, SentinelId, nameof(SelectedChannelId), forceNotify: true);
            SyncSelectedItemFromId(ref _selectedChannel, Channels, SentinelId, NoChannels, nameof(SelectedChannel));
            ResetThreadSelection(clearThreads: true);
            CancelThreadActivation();
            Transcript.Clear();
            DisconnectThreadWatch();
        }
        finally
        {
            _suppressCascade--;
        }
    }

    private void ResetThreadSelection(bool clearThreads)
    {
        _suppressCascade++;
        try
        {
            SetSelectorId(ref _selectedThreadId, SentinelId, nameof(SelectedThreadId), forceNotify: true);
            SyncSelectedItemFromId(ref _selectedThread, Threads, SentinelId, NoThread, nameof(SelectedThread));
            CancelThreadActivation();
            if (clearThreads)
            {
                MergeById(Threads, new (Guid? Id, string Label)[] { (NoThread.Id, NoThread.DisplayName) }, NoThread);
            }
        }
        finally
        {
            _suppressCascade--;
        }
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
            // Remote UI/WPF can transiently push null while ItemsSource is
            // being reshuffled by an in-place MergeById (the ComboBox sees
            // its SelectedItem reference momentarily leave the collection
            // during an Insert/RemoveAt sequence). A real "no selection"
            // is represented by the sentinel item (NoContext), never by a
            // literal null. Drop the spurious null so it doesn't clobber a
            // valid selection and silently desync SelectedContextId from
            // the visible dropdown state.
            if (value is null) return;
            if (SetProperty(ref _selectedContext, value))
                SelectedContextId = value.Id ?? SentinelId;
        }
    }

    [DataMember]
    public Guid? SelectedContextId
    {
        get => _selectedContextId;
        set
        {
            var normalized = NormalizeSelectorId(value);
            if (!SetSelectorId(ref _selectedContextId, normalized, nameof(SelectedContextId)))
                return;

            SyncSelectedItemFromId(ref _selectedContext, Contexts, normalized, NoContext, nameof(SelectedContext));
            if (_suppressCascade != 0)
                return;

            unchecked { _selectionVersion++; }
            ResetChannelAndThreadSelection();
            _ = ReloadChannelsAsync(CancellationToken.None);
        }
    }

    [DataMember]
    public SharpClawSelectorItem? SelectedChannel
    {
        get => _selectedChannel;
        set
        {
            // See SelectedContext: ignore transient null pushes from WPF
            // mid-merge. A real "no channel" goes through the NoChannels
            // sentinel item, not literal null. Without this guard, a
            // periodic refresh racing the user's channel selection would
            // null SelectedChannelId out — leaving the thread dropdown
            // empty and CreateThread reporting "Select a channel before
            // creating a thread." even though the visible ComboBox still
            // shows the chosen channel.
            if (value is null) return;
            if (SetProperty(ref _selectedChannel, value))
                SelectedChannelId = value.Id ?? SentinelId;
        }
    }

    [DataMember]
    public Guid? SelectedChannelId
    {
        get => _selectedChannelId;
        set
        {
            var normalized = NormalizeSelectorId(value);
            if (!SetSelectorId(ref _selectedChannelId, normalized, nameof(SelectedChannelId)))
                return;

            SyncSelectedItemFromId(ref _selectedChannel, Channels, normalized, NoChannels, nameof(SelectedChannel));
            if (_suppressCascade != 0)
                return;

            unchecked { _selectionVersion++; }
            ResetThreadSelection(clearThreads: false);
            Transcript.Clear();
            DisconnectThreadWatch();
            _ = ReloadThreadsAsync(CancellationToken.None);
        }
    }

    [DataMember]
    public SharpClawSelectorItem? SelectedThread
    {
        get => _selectedThread;
        set
        {
            // See SelectedContext: ignore transient null pushes from WPF
            // mid-merge. A real "no thread" goes through the NoThread
            // sentinel item.
            if (value is null) return;
            if (SetProperty(ref _selectedThread, value))
                SelectedThreadId = value.Id ?? SentinelId;
        }
    }

    [DataMember]
    public Guid? SelectedThreadId
    {
        get => _selectedThreadId;
        set
        {
            var normalized = NormalizeSelectorId(value);
            if (!SetSelectorId(ref _selectedThreadId, normalized, nameof(SelectedThreadId)))
                return;

            SyncSelectedItemFromId(ref _selectedThread, Threads, normalized, NoThread, nameof(SelectedThread));
            if (_suppressCascade != 0)
                return;

            unchecked { _selectionVersion++; }
            if (IsRealId(normalized))
            {
                QueueThreadActivation();
            }
            else
            {
                CancelThreadActivation();
                Transcript.Clear();
                DisconnectThreadWatch();
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

    private void QueueThreadActivation()
    {
        CancelThreadActivation();

        var channelId = RealIdOrNull(SelectedChannelId);
        var threadId = RealIdOrNull(SelectedThreadId);
        if (channelId is not Guid chId || threadId is not Guid thId)
        {
            Transcript.Clear();
            DisconnectThreadWatch();
            return;
        }

        var version = _selectionVersion;
        var cts = new CancellationTokenSource();
        _threadActivationCts = cts;
        _ = Task.Run(() => ActivateThreadAsync(chId, thId, version, cts));
    }

    private void CancelThreadActivation()
    {
        var cts = _threadActivationCts;
        if (cts is null)
            return;

        _threadActivationCts = null;
        try { cts.Cancel(); } catch { /* best effort */ }
    }

    private async Task ActivateThreadAsync(Guid channelId, Guid threadId, long version, CancellationTokenSource cts)
    {
        var ct = cts.Token;
        try
        {
            // Let the ComboBox finish committing/closing before we touch the
            // transcript or open the watch stream. This keeps selection UI
            // work tiny and prevents heavy Remote UI updates from running
            // inside the selection-change delivery path.
            await Task.Delay(150, ct).ConfigureAwait(false);

            await SwitchToUi();
            if (ct.IsCancellationRequested
                || version != _selectionVersion
                || RealIdOrNull(SelectedChannelId) != channelId
                || RealIdOrNull(SelectedThreadId) != threadId)
                return;

            IsBusy = true;
            Status = "Loading thread...";
            Transcript.Clear();

            var history = await _backend.GetHistoryAsync(channelId, threadId, ct).ConfigureAwait(false);

            await SwitchToUi();
            if (ct.IsCancellationRequested
                || version != _selectionVersion
                || RealIdOrNull(SelectedChannelId) != channelId
                || RealIdOrNull(SelectedThreadId) != threadId)
                return;

            ReplaceTranscript(history);
            Status = "Thread loaded.";
            ReconnectThreadWatch();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            await SwitchToUi();
            if (!ct.IsCancellationRequested)
                Status = $"Error: {ex.Message}";
            await _log.WriteLineAsync($"ActivateThread failed: {ex}").ConfigureAwait(false);
        }
        finally
        {
            await SwitchToUi();
            if (ReferenceEquals(_threadActivationCts, cts))
            {
                _threadActivationCts = null;
                IsBusy = false;
            }
            cts.Dispose();
        }
    }

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
        var channelId = RealIdOrNull(SelectedChannelId);
        var threadId = RealIdOrNull(SelectedThreadId);

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
                        await SwitchToUi();
                        IsThreadBusy = true;
                    }
                    else if (eventName == "NewMessages")
                    {
                        await SwitchToUi();
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
        // Coalesce overlapping refresh requests. Periodic refresh ticks,
        // reconnect notifications, and the user clicking "Refresh" can all
        // race; we only ever want one refresh chain executing on the UI
        // context at a time, with at most one queued follow-up pass.
        await SwitchToUi();
        if (_refreshInFlight)
        {
            _refreshPending = true;
            // If any caller wants a fresh wipe, the queued pass should honor
            // that. Otherwise the queued pass preserves selection.
            _refreshPendingPreserve = _refreshPendingPreserve && preserveSelection;
            return;
        }
        _refreshInFlight = true;
        _refreshPendingPreserve = true;

        try
        {
            await RefreshAllCoreAsync(preserveSelection, ct).ConfigureAwait(false);

            await SwitchToUi();
            while (_refreshPending && !ct.IsCancellationRequested)
            {
                _refreshPending = false;
                var preserve = _refreshPendingPreserve;
                _refreshPendingPreserve = true;
                await RefreshAllCoreAsync(preserve, ct).ConfigureAwait(false);
                await SwitchToUi();
            }
        }
        finally
        {
            await SwitchToUi();
            _refreshInFlight = false;
        }
    }

    private async Task RefreshAllCoreAsync(bool preserveSelection, CancellationToken ct)
    {
        await SwitchToUi();
        var version = _selectionVersion;
        var prevContextId = preserveSelection ? RealIdOrNull(SelectedContextId) : null;
        var prevChannelId = preserveSelection ? RealIdOrNull(SelectedChannelId) : null;
        var prevThreadId = preserveSelection ? RealIdOrNull(SelectedThreadId) : null;

        IsBusy = true;
        if (!preserveSelection)
            Status = "Loading contexts…";

        try
        {
            // HTTP runs off-context (it's "free thread") so we don't block
            // the UI sync context while waiting for the network.
            var contexts = await _backend.GetContextsAsync(ct).ConfigureAwait(false);

            await SwitchToUi();
            if (version != _selectionVersion)
                return;

            _suppressCascade++;
            try
            {
                var desired = new System.Collections.Generic.List<(Guid? Id, string Label)>(contexts.Count + 1)
                {
                    (NoContext.Id, NoContext.DisplayName),
                };
                foreach (var c in contexts)
                    desired.Add((c.Id, c.Name ?? c.Id.ToString()));
                MergeById(Contexts, desired, NoContext);

                if (prevContextId is Guid cid)
                {
                    var restoredContext = FindById(Contexts, cid);
                    if (restoredContext is null)
                        ForceSelect(ref _selectedContext, NoContext, nameof(SelectedContext));
                    else if (!ReferenceEquals(_selectedContext, restoredContext))
                        ForceSelect(ref _selectedContext, restoredContext, nameof(SelectedContext));
                }
                else
                {
                    ForceSelect(ref _selectedContext, NoContext, nameof(SelectedContext));
                }
            }
            finally
            {
                _suppressCascade--;
            }

            await ReloadChannelsCoreAsync(prevChannelId, prevThreadId, ct).ConfigureAwait(false);

            await SwitchToUi();
            // Always overwrite stale error text on a successful refresh so a
            // reconnect can clear an "API error" subtitle without waiting for
            // the user to interact again.
            if (!preserveSelection || _status.StartsWith("Error", StringComparison.Ordinal))
                Status = "Connected";

            await _log.WriteLineAsync(
                $"Refresh: {contexts.Count} context(s), preserveSelection={preserveSelection}.")
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* shutdown / cancellation */ }
        catch (Exception ex)
        {
            await SwitchToUi();
            Status = $"Error: {ex.Message}";
            await _log.WriteLineAsync($"Refresh failed: {ex.Message}").ConfigureAwait(false);
        }
        finally
        {
            await SwitchToUi();
            IsBusy = false;
        }
    }

    private async Task ReloadChannelsAsync(Guid? preserveChannelId, Guid? preserveThreadId, CancellationToken ct)
    {
        try
        {
            await ReloadChannelsCoreAsync(preserveChannelId, preserveThreadId, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            await SwitchToUi();
            Status = $"Error: {ex.Message}";
            await _log.WriteLineAsync($"ReloadChannels failed: {ex.Message}").ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Channel reload body. All collection / selector mutations happen on the
    /// UI sync context (via <see cref="SwitchToUi"/>); the network call runs
    /// off-context so we never freeze Visual Studio while fetching.
    /// </summary>
    private async Task ReloadChannelsCoreAsync(Guid? preserveChannelId, Guid? preserveThreadId, CancellationToken ct)
    {
        // Capture filter on UI context (avoid reading SelectedContext from
        // a background thread).
        await SwitchToUi();
        var contextFilter = RealIdOrNull(SelectedContextId);
        var version = _selectionVersion;

        var all = await _backend.GetChannelsAsync(ct).ConfigureAwait(false);

        await SwitchToUi();
        if (version != _selectionVersion || contextFilter != RealIdOrNull(SelectedContextId))
            return;

        _suppressCascade++;
        try
        {
            var desired = new System.Collections.Generic.List<(Guid? Id, string Label)>(all.Count + 1)
            {
                (NoChannels.Id, NoChannels.DisplayName),
            };
            foreach (var ch in all)
            {
                if (contextFilter is Guid ctxId && ch.ContextId != ctxId)
                    continue;
                var label = string.IsNullOrWhiteSpace(ch.Title) ? ch.Id.ToString() : ch.Title;
                desired.Add((ch.Id, label));
            }

            MergeById(Channels, desired, NoChannels);

            if (preserveChannelId is Guid cid)
            {
                var restored = FindById(Channels, cid);
                if (restored is null)
                    ForceSelect(ref _selectedChannel, NoChannels, nameof(SelectedChannel));
                else if (!ReferenceEquals(_selectedChannel, restored))
                    ForceSelect(ref _selectedChannel, restored, nameof(SelectedChannel));
            }
            else
            {
                ForceSelect(ref _selectedChannel, NoChannels, nameof(SelectedChannel));
            }
        }
        finally
        {
            _suppressCascade--;
        }

        await ReloadThreadsCoreAsync(preserveThreadId, ct).ConfigureAwait(false);
    }

    private async Task ReloadThreadsAsync(Guid? preserveThreadId, CancellationToken ct)
    {
        try
        {
            await ReloadThreadsCoreAsync(preserveThreadId, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            await SwitchToUi();
            Status = $"Error: {ex.Message}";
            await _log.WriteLineAsync($"ReloadThreads failed: {ex.Message}").ConfigureAwait(false);
        }

        await SwitchToUi();
        if (IsRealId(SelectedThreadId))
        {
            QueueThreadActivation();
        }
        else
        {
            CancelThreadActivation();
            Transcript.Clear();
            DisconnectThreadWatch();
        }
    }

    /// <summary>
    /// Thread reload body. UI-affinity rules identical to
    /// <see cref="ReloadChannelsCoreAsync"/>.
    /// </summary>
    private async Task ReloadThreadsCoreAsync(Guid? preserveThreadId, CancellationToken ct)
    {
        await SwitchToUi();
        var channelId = RealIdOrNull(SelectedChannelId);
        var version = _selectionVersion;

        var threads = channelId is Guid chId
            ? await _backend.GetThreadsAsync(chId, ct).ConfigureAwait(false)
            : (System.Collections.Generic.IReadOnlyList<ThreadDto>)Array.Empty<ThreadDto>();

        await SwitchToUi();
        if (version != _selectionVersion || channelId != RealIdOrNull(SelectedChannelId))
            return;

        _suppressCascade++;
        try
        {
            var desired = new System.Collections.Generic.List<(Guid? Id, string Label)>(threads.Count + 1)
            {
                (NoThread.Id, NoThread.DisplayName),
            };
            foreach (var t in threads)
                desired.Add((t.Id, t.Name ?? t.Id.ToString()));

            MergeById(Threads, desired, NoThread);

            if (preserveThreadId is Guid tid)
            {
                var restored = FindById(Threads, tid);
                if (restored is null)
                    ForceSelect(ref _selectedThread, NoThread, nameof(SelectedThread));
                else if (!ReferenceEquals(_selectedThread, restored))
                    ForceSelect(ref _selectedThread, restored, nameof(SelectedThread));
            }
            else
            {
                ForceSelect(ref _selectedThread, NoThread, nameof(SelectedThread));
            }
        }
        finally
        {
            _suppressCascade--;
        }
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

        if (propertyName == nameof(SelectedContext))
        {
            SetSelectorId(ref _selectedContextId, value?.Id ?? SentinelId, nameof(SelectedContextId), forceNotify: true);
        }
        else if (propertyName == nameof(SelectedChannel))
        {
            SetSelectorId(ref _selectedChannelId, value?.Id ?? SentinelId, nameof(SelectedChannelId), forceNotify: true);
        }
        else if (propertyName == nameof(SelectedThread))
        {
            SetSelectorId(ref _selectedThreadId, value?.Id ?? SentinelId, nameof(SelectedThreadId), forceNotify: true);
        }
    }

    private static SharpClawSelectorItem? FindById(ObservableList<SharpClawSelectorItem> list, Guid id)
    {
        foreach (var item in list)
            if (item.Id == id) return item;
        return null;
    }

    /// <summary>
    /// Reconciles <paramref name="list"/> against <paramref name="desired"/>
    /// in place: the sentinel is kept at index 0, surviving items keep their
    /// reference identity (and only get a DisplayName update if it changed),
    /// new items are added, and removed items are deleted. The single
    /// sentinel reference (<paramref name="sentinel"/>) is reused so it never
    /// turns into a "blank" entry after a refresh.
    ///
    /// <para>This replaces the previous Clear()+repopulate pattern, which
    /// (a) raised a Reset notification that crashed the WPF virtualizing
    /// ComboBox when its dropdown was being realized concurrently, and
    /// (b) replaced item instances every refresh — invalidating WPF's
    /// reference-based <c>SelectedItem</c> and leaving the picker visually
    /// blank even though the bound view-model field had been re-pinned.</para>
    /// </summary>
    private static void MergeById(
        ObservableList<SharpClawSelectorItem> list,
        System.Collections.Generic.IList<(Guid? Id, string Label)> desired,
        SharpClawSelectorItem sentinel)
    {
        // Pass 1: ensure each desired item exists at the right slot,
        // reusing existing references by Id whenever possible.
        for (int i = 0; i < desired.Count; i++)
        {
            var (id, label) = desired[i];
            SharpClawSelectorItem item;

            if (!IsRealId(id))
            {
                item = sentinel;
                item.DisplayName = label;
            }
            else
            {
                var existing = FindById(list, id!.Value);
                if (existing is not null)
                {
                    existing.DisplayName = label;
                    item = existing;
                }
                else
                {
                    item = new SharpClawSelectorItem(id, label);
                }
            }

            if (i < list.Count)
            {
                if (!ReferenceEquals(list[i], item))
                {
                    // Find the item in the tail and move it into position
                    // instead of removing+inserting, to keep notifications
                    // minimal and preserve SelectedItem identity.
                    var currentIndex = -1;
                    for (int j = i + 1; j < list.Count; j++)
                    {
                        if (ReferenceEquals(list[j], item)) { currentIndex = j; break; }
                    }

                    if (currentIndex >= 0)
                    {
                        list.RemoveAt(currentIndex);
                        list.Insert(i, item);
                    }
                    else
                    {
                        list.Insert(i, item);
                    }
                }
            }
            else
            {
                list.Add(item);
            }
        }

        // Pass 2: trim any leftovers from the tail.
        while (list.Count > desired.Count)
            list.RemoveAt(list.Count - 1);
    }

    // ── Selector-driven loaders (manual changes) ─────────────────

    private async Task ReloadChannelsAsync(CancellationToken ct)
        => await ReloadChannelsAsync(preserveChannelId: null, preserveThreadId: null, ct).ConfigureAwait(false);

    private async Task ReloadThreadsAsync(CancellationToken ct)
        => await ReloadThreadsAsync(preserveThreadId: null, ct).ConfigureAwait(false);

    // ── Thread creation ──────────────────────────────────────────

    public async Task CreateThreadAsync(CancellationToken ct)
    {
        if (RealIdOrNull(SelectedChannelId) is not Guid channelId)
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
            await SwitchToUi();
            NewThreadName = string.Empty;

            // Reload the thread list and select the new one if we got an id back.
            await ReloadThreadsAsync(created?.Id, ct).ConfigureAwait(false);
            await SwitchToUi();
            Status = created is null ? "Thread created." : $"Thread '{name}' created.";
        }
        catch (Exception ex)
        {
            await SwitchToUi();
            Status = $"Create thread failed: {ex.Message}";
            await _log.WriteLineAsync($"CreateThread failed: {ex}").ConfigureAwait(false);
        }
        finally
        {
            await SwitchToUi();
            IsBusy = false;
        }
    }

    private async Task ReloadHistoryAsync(CancellationToken ct)
    {
        try
        {
            await SwitchToUi();
            var channelId = RealIdOrNull(SelectedChannelId);
            var threadId = RealIdOrNull(SelectedThreadId);
            var version = _selectionVersion;
            if (channelId is not Guid chId || threadId is not Guid thId)
                return;

            IsBusy = true;
            var history = await _backend.GetHistoryAsync(chId, thId, ct).ConfigureAwait(false);

            await SwitchToUi();
            if (version != _selectionVersion
                || RealIdOrNull(SelectedChannelId) != channelId
                || RealIdOrNull(SelectedThreadId) != threadId)
                return;

            ReplaceTranscript(history);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            await SwitchToUi();
            Status = $"Error: {ex.Message}";
            await _log.WriteLineAsync($"ReloadHistory failed: {ex}").ConfigureAwait(false);
        }
        finally
        {
            await SwitchToUi();
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

    private void ReplaceTranscript(System.Collections.Generic.IReadOnlyList<ChatMessageDto> history)
    {
        // Already on the UI sync context (callers ensure that). Direct, simple
        // rebuild — no caps, no truncation, no batched yields. The earlier
        // cosmetic mitigations were hiding the real cause (selector cascade
        // re-entry from object-reference SelectedItem bindings).
        Transcript.Clear();
        for (var i = 0; i < history.Count; i++)
            Transcript.Add(BuildTurnFromHistory(history[i]));
    }

    // â”€â”€ Send â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    public async Task SendAsync(CancellationToken ct)
    {
        if (RealIdOrNull(SelectedChannelId) is not Guid channelId)
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
                .StartChatStreamAsync(channelId, RealIdOrNull(SelectedThreadId), text!, ct)
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
