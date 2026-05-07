using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using System.Security;
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
internal sealed class SharpClawChatViewModel : NotifyPropertyChangedObject, IDisposable
{
    private static readonly Guid SentinelId = Guid.Empty;
    private static readonly SharpClawSelectorItem NoContext = new(SentinelId, "[No Context]");
    private static readonly SharpClawSelectorItem NoChannels = new(SentinelId, "[No Channel]");
    private static readonly SharpClawSelectorItem NoThread = new(SentinelId, "[No Thread]");
    private const int MaxRenderedHistoryMessages = 200;

    private readonly SharpClawBackend _backend;
    private readonly SharpClawOutputLog _log;
    private readonly SynchronizationContext? _uiContext;

    private SharpClawSelectorItem? _selectedContext;
    private SharpClawSelectorItem? _selectedChannel;
    private SharpClawSelectorItem? _selectedThread;
    private Guid? _selectedContextId = SentinelId;
    private Guid? _selectedChannelId = SentinelId;
    private Guid? _selectedThreadId = SentinelId;
    private int _selectedContextIndex;
    private int _selectedChannelIndex;
    private int _selectedThreadIndex;
    private readonly System.Collections.Generic.List<SharpClawChatTurn> _transcriptBlocks = new();
    private string _composer = string.Empty;
    private string _status = "Idle";
    private string _transcriptText = string.Empty;
    private XamlFragment _transcriptContent = EmptyTranscriptFragment();
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
    private bool _disposed;

    public SharpClawChatViewModel(SharpClawBackend backend, SharpClawOutputLog log)
        : this(backend, log, ui: null) { }

    public SharpClawChatViewModel(SharpClawBackend backend, SharpClawOutputLog log, SynchronizationContext? ui)
    {
        _backend = backend;
        _log = log;
        _uiContext = ui;

        SendCommand = new AsyncCommand(async (_, ct) => await SendAsync(ct).ConfigureAwait(false));
        CreateThreadCommand = new AsyncCommand(async (_, ct) => await CreateThreadAsync(ct).ConfigureAwait(false));
        OpenThreadCommand = new AsyncCommand(async (_, ct) => await OpenSelectedThreadAsync(ct).ConfigureAwait(false));

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
        if (_disposed)
            return;

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
                if (_disposed)
                    return;

                Status = "Connected - refreshing...";
                await _log.WriteLineAsync("Backend connected — refreshing chat selectors.").ConfigureAwait(false);
                await RefreshAllAsync(preserveSelection: true, CancellationToken.None).ConfigureAwait(false);
                // RefreshAllAsync only sets "Connected" when preserveSelection
                // is false (initial load). For a reconnect we still want to
                // visibly clear any stale error text, so do it here.
                await SwitchToUi();
                if (_disposed)
                    return;

                if (_status.StartsWith("Error", StringComparison.Ordinal)
                    || _status.StartsWith("Connected - refreshing", StringComparison.Ordinal))
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

    public bool IsDisposed => _disposed;

    private Task LogVmAsync(string message)
        => _log.WriteLineAsync($"ChatVM[{GetHashCode():x8}] {message}");

    private void LogVmImmediate(string message)
        => _log.WriteLineImmediate($"ChatVM[{GetHashCode():x8}] {message}");

    public void Dispose()
    {
        if (_disposed)
            return;

        _ = LogVmAsync("Dispose: tearing down chat view model.");
        _disposed = true;
        _backend.Connected -= OnBackendConnected;

        try { _periodicCts?.Cancel(); } catch { }
        _periodicCts?.Dispose();
        _periodicCts = null;

        CancelThreadActivation();
        DisconnectThreadWatch();
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

    private bool SetSelectorIndex(ref int field, int value, string propertyName, bool forceNotify = false)
    {
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

    private static int FindIndexById(ObservableList<SharpClawSelectorItem> list, Guid? id)
    {
        var normalized = NormalizeSelectorId(id);
        for (var i = 0; i < list.Count; i++)
        {
            if (NormalizeSelectorId(list[i].Id) == normalized)
                return i;
        }

        return 0;
    }

    private static SharpClawSelectorItem ItemAtOrSentinel(
        ObservableList<SharpClawSelectorItem> list,
        int index,
        SharpClawSelectorItem sentinel)
    {
        return index >= 0 && index < list.Count ? list[index] : sentinel;
    }

    private void SyncSelectedIndexFromId(
        ObservableList<SharpClawSelectorItem> list,
        Guid? id,
        ref int indexField,
        string indexPropertyName)
    {
        SetSelectorIndex(ref indexField, FindIndexById(list, id), indexPropertyName, forceNotify: true);
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
            SyncSelectedIndexFromId(Channels, SentinelId, ref _selectedChannelIndex, nameof(SelectedChannelIndex));
            ResetThreadSelection(clearThreads: true);
            CancelThreadActivation();
            ClearTranscript();
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
            SyncSelectedIndexFromId(Threads, SentinelId, ref _selectedThreadIndex, nameof(SelectedThreadIndex));
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
    public int SelectedContextIndex
    {
        get => _selectedContextIndex;
        set
        {
            if (value < 0 || value >= Contexts.Count)
                return;

            if (_suppressCascade != 0)
                return;

            if (!SetSelectorIndex(ref _selectedContextIndex, value, nameof(SelectedContextIndex)))
                return;

            var item = ItemAtOrSentinel(Contexts, value, NoContext);
            if (!ReferenceEquals(_selectedContext, item))
            {
                _selectedContext = item;
                RaiseNotifyPropertyChangedEvent(nameof(SelectedContext));
            }

            SelectedContextId = item.Id ?? SentinelId;
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
            SyncSelectedIndexFromId(Contexts, normalized, ref _selectedContextIndex, nameof(SelectedContextIndex));
            if (_suppressCascade != 0)
            {
                _ = LogVmAsync($"SelectedContextIndex: ignored suppressed value={value}.");
                return;
            }

            unchecked { _selectionVersion++; }
            _ = LogVmAsync($"SelectedContextId: changed to {normalized?.ToString() ?? "<null>"} version={_selectionVersion}.");
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
    public int SelectedChannelIndex
    {
        get => _selectedChannelIndex;
        set
        {
            if (value < 0 || value >= Channels.Count)
                return;

            if (_suppressCascade != 0)
                return;

            if (!SetSelectorIndex(ref _selectedChannelIndex, value, nameof(SelectedChannelIndex)))
                return;

            var item = ItemAtOrSentinel(Channels, value, NoChannels);
            if (!ReferenceEquals(_selectedChannel, item))
            {
                _selectedChannel = item;
                RaiseNotifyPropertyChangedEvent(nameof(SelectedChannel));
            }

            SelectedChannelId = item.Id ?? SentinelId;
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
            SyncSelectedIndexFromId(Channels, normalized, ref _selectedChannelIndex, nameof(SelectedChannelIndex));
            if (_suppressCascade != 0)
            {
                _ = LogVmAsync($"SelectedChannelIndex: ignored suppressed value={value}.");
                return;
            }

            unchecked { _selectionVersion++; }
            _ = LogVmAsync($"SelectedChannelId: changed to {normalized?.ToString() ?? "<null>"} version={_selectionVersion}.");
            ResetThreadSelection(clearThreads: false);
            ClearTranscript();
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
    public int SelectedThreadIndex
    {
        get => _selectedThreadIndex;
        set
        {
            if (value < 0 || value >= Threads.Count)
                return;

            if (_suppressCascade != 0)
                return;

            if (!SetSelectorIndex(ref _selectedThreadIndex, value, nameof(SelectedThreadIndex)))
                return;

            var item = ItemAtOrSentinel(Threads, value, NoThread);
            if (!ReferenceEquals(_selectedThread, item))
            {
                _selectedThread = item;
                RaiseNotifyPropertyChangedEvent(nameof(SelectedThread));
            }

            SelectedThreadId = item.Id ?? SentinelId;
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
            SyncSelectedIndexFromId(Threads, normalized, ref _selectedThreadIndex, nameof(SelectedThreadIndex));
            if (_suppressCascade != 0)
            {
                _ = LogVmAsync($"SelectedThreadIndex: ignored suppressed value={value}.");
                return;
            }

            unchecked { _selectionVersion++; }
            _ = LogVmAsync($"SelectedThreadId: changed to {normalized?.ToString() ?? "<null>"} version={_selectionVersion}.");
            CancelThreadActivation();
            DisconnectThreadWatch();
            if (!IsRealId(normalized))
            {
                ClearTranscript();
            }
            else
            {
                QueueSelectedThreadActivation("thread selection");
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
    public string TranscriptText
    {
        get => _transcriptText;
        private set => SetProperty(ref _transcriptText, value ?? string.Empty);
    }

    [DataMember]
    public XamlFragment TranscriptContent
    {
        get => _transcriptContent;
        private set => SetProperty(ref _transcriptContent, value ?? EmptyTranscriptFragment());
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

    // Send is exposed as the concrete AsyncCommand so we can flip CanExecute
    // from the watch loop / stream lifecycle (the IAsyncCommand interface
    // only exposes a getter).
    [DataMember] public AsyncCommand SendCommand { get; }
    [DataMember] public IAsyncCommand CreateThreadCommand { get; }
    [DataMember] public AsyncCommand OpenThreadCommand { get; }

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
        if (_disposed)
            return;

        if (_initialized) return;
        _initialized = true;
        _ = LogVmAsync("EnsureStarted: initial refresh scheduled.");

        _ = Task.Run(async () =>
        {
            await RefreshAllAsync(preserveSelection: false, CancellationToken.None).ConfigureAwait(false);
            StartPeriodicRefresh();
        });
    }

    private void StartPeriodicRefresh()
    {
        if (_disposed)
            return;

        _periodicCts?.Cancel();
        _periodicCts = new CancellationTokenSource();
        var ct = _periodicCts.Token;
        _ = LogVmAsync("StartPeriodicRefresh: 15 second refresh loop started.");

        _ = Task.Run(async () =>
        {
            // Refresh every 15 seconds. Cheap enough for local backend, frequent
            // enough that newly-created channels/threads show up promptly.
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);

                    await SwitchToUi();
                    if (_disposed)
                        break;

                    if (IsRealId(SelectedThreadId))
                    {
                        await _log.WriteLineAsync("Periodic refresh skipped: active thread selected.").ConfigureAwait(false);
                        continue;
                    }

                    await _log.WriteLineAsync("Periodic refresh tick.").ConfigureAwait(false);
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

    private async Task OpenSelectedThreadAsync(CancellationToken ct)
    {
        // Remote UI command callbacks are asynchronous. Capture the
        // selector state synchronously at command invocation time before
        // this method yields, matching Microsoft's guidance for commands
        // that depend on multiple bound properties.
        var channelId = RealIdOrNull(SelectedChannelId);
        var threadId = RealIdOrNull(SelectedThreadId);
        var version = _selectionVersion;

        await SwitchToUi();
        if (_disposed || ct.IsCancellationRequested)
            return;

        if (channelId is not Guid chId || threadId is not Guid thId)
        {
            Status = "Select a channel and thread before opening.";
            return;
        }

        _ = LogVmAsync($"OpenSelectedThread: requested channel={channelId} thread={threadId} version={version}.");
        QueueThreadActivation(chId, thId, version);
    }

    private void QueueThreadActivation(Guid channelId, Guid threadId, long version)
    {
        CancelThreadActivation();

        var cts = new CancellationTokenSource();
        _threadActivationCts = cts;
        _ = LogVmAsync($"QueueThreadActivation: channel={channelId} thread={threadId} version={version}.");
        _ = Task.Run(() => ActivateThreadAsync(channelId, threadId, version, cts));
    }

    private void QueueSelectedThreadActivation(string reason)
    {
        var channelId = RealIdOrNull(SelectedChannelId);
        var threadId = RealIdOrNull(SelectedThreadId);
        var version = _selectionVersion;

        if (channelId is not Guid chId || threadId is not Guid thId)
        {
            Status = "Select a channel and thread before opening.";
            return;
        }

        _ = LogVmAsync($"QueueSelectedThreadActivation: reason={reason} channel={chId} thread={thId} version={version}.");
        QueueThreadActivation(chId, thId, version);
    }

    private void CancelThreadActivation()
    {
        var cts = _threadActivationCts;
        if (cts is null)
            return;

        _threadActivationCts = null;
        _ = LogVmAsync("CancelThreadActivation: cancellation requested.");
        try { cts.Cancel(); } catch { /* best effort */ }
    }

    private async Task ActivateThreadAsync(Guid channelId, Guid threadId, long version, CancellationTokenSource cts)
    {
        var ct = cts.Token;
        try
        {
            LogVmImmediate($"CRASHMARK ActivateThread ENTER channel={channelId} thread={threadId} version={version}.");
            LogVmImmediate($"CRASHMARK ActivateThread BEFORE SwitchToUi#1 channel={channelId} thread={threadId}.");
            await SwitchToUi();
            LogVmImmediate($"CRASHMARK ActivateThread AFTER SwitchToUi#1 selectedChannel={RealIdOrNull(SelectedChannelId)?.ToString() ?? "<none>"} selectedThread={RealIdOrNull(SelectedThreadId)?.ToString() ?? "<none>"} currentVersion={_selectionVersion}.");
            if (ct.IsCancellationRequested
                || version != _selectionVersion
                || RealIdOrNull(SelectedChannelId) != channelId
                || RealIdOrNull(SelectedThreadId) != threadId)
            {
                _ = LogVmAsync($"ActivateThread: stale before load channel={channelId} thread={threadId} requestedVersion={version} currentVersion={_selectionVersion}.");
                return;
            }

            LogVmImmediate("CRASHMARK ActivateThread BEFORE IsBusy=true.");
            IsBusy = true;
            LogVmImmediate("CRASHMARK ActivateThread AFTER IsBusy=true BEFORE Status=Loading.");
            Status = "Loading thread...";
            DisconnectThreadWatch();
            LogVmImmediate("CRASHMARK ActivateThread AFTER Status=Loading BEFORE GetHistory.");

            using var loadCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            loadCts.CancelAfter(TimeSpan.FromSeconds(20));
            var history = await _backend.GetHistoryAsync(channelId, threadId, loadCts.Token).ConfigureAwait(false);
            LogVmImmediate($"CRASHMARK ActivateThread AFTER GetHistory count={history.Count} BEFORE SwitchToUi#2.");

            await SwitchToUi();
            LogVmImmediate($"CRASHMARK ActivateThread AFTER SwitchToUi#2 selectedChannel={RealIdOrNull(SelectedChannelId)?.ToString() ?? "<none>"} selectedThread={RealIdOrNull(SelectedThreadId)?.ToString() ?? "<none>"} currentVersion={_selectionVersion}.");
            if (ct.IsCancellationRequested
                || version != _selectionVersion
                || RealIdOrNull(SelectedChannelId) != channelId
                || RealIdOrNull(SelectedThreadId) != threadId)
            {
                _ = LogVmAsync($"ActivateThread: stale after history channel={channelId} thread={threadId} requestedVersion={version} currentVersion={_selectionVersion}.");
                return;
            }

            LogVmImmediate($"CRASHMARK ActivateThread BEFORE ReplaceTranscript count={history.Count}.");
            ReplaceTranscript(history);
            LogVmImmediate($"CRASHMARK ActivateThread AFTER ReplaceTranscript chars={TranscriptText.Length} BEFORE Status=ThreadLoaded.");
            Status = "Thread loaded.";
            LogVmImmediate("CRASHMARK ActivateThread COMPLETED without starting thread watch.");
        }
        catch (OperationCanceledException)
        {
            LogVmImmediate($"CRASHMARK ActivateThread CANCELED channel={channelId} thread={threadId}.");
        }
        catch (Exception ex)
        {
            LogVmImmediate($"CRASHMARK ActivateThread EXCEPTION {ex}");
            await SwitchToUi();
            if (!ct.IsCancellationRequested)
                Status = $"Error: {ex.Message}";
            await _log.WriteLineAsync($"ActivateThread failed: {ex}").ConfigureAwait(false);
        }
        finally
        {
            LogVmImmediate("CRASHMARK ActivateThread FINALLY BEFORE SwitchToUi.");
            await SwitchToUi();
            LogVmImmediate("CRASHMARK ActivateThread FINALLY AFTER SwitchToUi.");
            if (ReferenceEquals(_threadActivationCts, cts))
            {
                _threadActivationCts = null;
                IsBusy = false;
                LogVmImmediate("CRASHMARK ActivateThread FINALLY cleared current activation and IsBusy=false.");
            }
            cts.Dispose();
            LogVmImmediate("CRASHMARK ActivateThread FINALLY disposed CTS.");
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
        if (_isThreadBusy)
        {
            if (_disposed)
                _isThreadBusy = false;
            else
                IsThreadBusy = false;
        }
    }

    private async Task RunThreadWatchAsync(Guid channelId, Guid threadId, CancellationToken ct)
    {
        LogVmImmediate($"CRASHMARK ThreadWatch ENTER channel={channelId} thread={threadId}.");
        await _log.WriteLineAsync($"ThreadWatch: connecting channel={channelId} thread={threadId}").ConfigureAwait(false);
        try
        {
            LogVmImmediate($"CRASHMARK ThreadWatch BEFORE StartThreadWatchAsync channel={channelId} thread={threadId}.");
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectCts.CancelAfter(TimeSpan.FromSeconds(10));
            using var resp = await _backend.StartThreadWatchAsync(channelId, threadId, connectCts.Token).ConfigureAwait(false);
            LogVmImmediate($"CRASHMARK ThreadWatch AFTER StartThreadWatchAsync status={(int)resp.StatusCode}.");
            if (!resp.IsSuccessStatusCode)
            {
                await _log.WriteLineAsync($"ThreadWatch: HTTP {(int)resp.StatusCode}").ConfigureAwait(false);
                return;
            }

            LogVmImmediate("CRASHMARK ThreadWatch BEFORE ReadAsStream.");
            using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            LogVmImmediate("CRASHMARK ThreadWatch AFTER ReadAsStream.");
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
                        LogVmImmediate("CRASHMARK ThreadWatch EVENT Processing BEFORE SwitchToUi.");
                        await SwitchToUi();
                        LogVmImmediate("CRASHMARK ThreadWatch EVENT Processing AFTER SwitchToUi.");
                        IsThreadBusy = true;
                    }
                    else if (eventName == "NewMessages")
                    {
                        LogVmImmediate("CRASHMARK ThreadWatch EVENT NewMessages BEFORE SwitchToUi.");
                        await SwitchToUi();
                        LogVmImmediate("CRASHMARK ThreadWatch EVENT NewMessages AFTER SwitchToUi.");
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
            LogVmImmediate("CRASHMARK ThreadWatch EXIT stream ended.");
        }
        catch (OperationCanceledException)
        {
            LogVmImmediate("CRASHMARK ThreadWatch CANCELED.");
        }
        catch (Exception ex)
        {
            LogVmImmediate($"CRASHMARK ThreadWatch EXCEPTION {ex}");
            await _log.WriteLineAsync($"ThreadWatch: {ex.GetType().Name}: {ex.Message}").ConfigureAwait(false);
        }
    }

    private bool ComputeCanSend() => !_isSending && !_isThreadBusy;

    // ── Loaders ──────────────────────────────────────────────────

    public async Task RefreshAllAsync(bool preserveSelection, CancellationToken ct)
    {
        _ = LogVmAsync($"RefreshAll: requested preserveSelection={preserveSelection}.");
        // Coalesce overlapping refresh requests. Periodic refresh ticks,
        // reconnect notifications, and the user clicking "Refresh" can all
        // race; we only ever want one refresh chain executing on the UI
        // context at a time, with at most one queued follow-up pass.
        await SwitchToUi();
        if (_disposed)
            return;

        if (_refreshInFlight)
        {
            _ = LogVmAsync($"RefreshAll: coalesced preserveSelection={preserveSelection}.");
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
        if (_disposed)
            return;

        var version = _selectionVersion;
        var prevContextId = preserveSelection ? RealIdOrNull(SelectedContextId) : null;
        var prevChannelId = preserveSelection ? RealIdOrNull(SelectedChannelId) : null;
        var prevThreadId = preserveSelection ? RealIdOrNull(SelectedThreadId) : null;
        _ = LogVmAsync($"RefreshAllCore: start preserveSelection={preserveSelection} prevContext={prevContextId?.ToString() ?? "<none>"} prevChannel={prevChannelId?.ToString() ?? "<none>"} prevThread={prevThreadId?.ToString() ?? "<none>"}.");

        IsBusy = true;
        if (!preserveSelection)
            Status = "Loading contexts…";

        try
        {
            // HTTP runs off-context (it's "free thread") so we don't block
            // the UI sync context while waiting for the network.
            _ = LogVmAsync("RefreshAllCore: GET contexts.");
            var contexts = await _backend.GetContextsAsync(ct).ConfigureAwait(false);
            _ = LogVmAsync($"RefreshAllCore: GET contexts returned {contexts.Count}.");

            await SwitchToUi();
            if (_disposed)
                return;

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
                _ = LogVmAsync($"RefreshAllCore: contexts merged desired={desired.Count} actual={Contexts.Count}.");

                if (prevContextId is Guid cid)
                {
                    var restoredContext = FindById(Contexts, cid);
                    if (restoredContext is null)
                        ForceSelect(ref _selectedContext, NoContext, nameof(SelectedContext));
                    else
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
            if (_disposed)
                return;

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
            if (!_disposed)
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
        if (_disposed)
            return;

        var contextFilter = RealIdOrNull(SelectedContextId);
        var version = _selectionVersion;
        _ = LogVmAsync($"ReloadChannelsCore: start contextFilter={contextFilter?.ToString() ?? "<none>"} preserveChannel={preserveChannelId?.ToString() ?? "<none>"} preserveThread={preserveThreadId?.ToString() ?? "<none>"} version={version}.");

        _ = LogVmAsync("ReloadChannelsCore: GET channels.");
        var all = await _backend.GetChannelsAsync(ct).ConfigureAwait(false);
        _ = LogVmAsync($"ReloadChannelsCore: GET channels returned {all.Count}.");

        await SwitchToUi();
        if (_disposed)
            return;

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
            _ = LogVmAsync($"ReloadChannelsCore: channels merged desired={desired.Count} actual={Channels.Count}.");

            if (preserveChannelId is Guid cid)
            {
                var restored = FindById(Channels, cid);
                if (restored is null)
                    ForceSelect(ref _selectedChannel, NoChannels, nameof(SelectedChannel));
                else
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
        _ = LogVmAsync("ReloadChannelsCore: completed.");
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
            Status = "Thread selected. Click Load to refresh the transcript.";
        }
        else
        {
            CancelThreadActivation();
            ClearTranscript();
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
        if (_disposed)
            return;

        var channelId = RealIdOrNull(SelectedChannelId);
        var version = _selectionVersion;
        _ = LogVmAsync($"ReloadThreadsCore: start channel={channelId?.ToString() ?? "<none>"} preserveThread={preserveThreadId?.ToString() ?? "<none>"} version={version}.");

        _ = LogVmAsync($"ReloadThreadsCore: GET threads channel={channelId?.ToString() ?? "<none>"}.");
        var threads = channelId is Guid chId
            ? await _backend.GetThreadsAsync(chId, ct).ConfigureAwait(false)
            : (System.Collections.Generic.IReadOnlyList<ThreadDto>)Array.Empty<ThreadDto>();
        _ = LogVmAsync($"ReloadThreadsCore: GET threads returned {threads.Count}.");

        await SwitchToUi();
        if (_disposed)
            return;

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

            if (preserveThreadId is Guid preserved
                && IsRealId(SelectedThreadId)
                && SelectedThreadId == preserved
                && SameSelectorSnapshot(Threads, desired))
            {
                _ = LogVmAsync("ReloadThreadsCore: thread snapshot unchanged; skipped merge/restore.");
                return;
            }

            MergeById(Threads, desired, NoThread);
            _ = LogVmAsync($"ReloadThreadsCore: threads merged desired={desired.Count} actual={Threads.Count}.");

            if (preserveThreadId is Guid tid)
            {
                var restored = FindById(Threads, tid);
                if (restored is null)
                    ForceSelect(ref _selectedThread, NoThread, nameof(SelectedThread));
                else
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
            SyncSelectedIndexFromId(Contexts, value?.Id ?? SentinelId, ref _selectedContextIndex, nameof(SelectedContextIndex));
        }
        else if (propertyName == nameof(SelectedChannel))
        {
            SetSelectorId(ref _selectedChannelId, value?.Id ?? SentinelId, nameof(SelectedChannelId), forceNotify: true);
            SyncSelectedIndexFromId(Channels, value?.Id ?? SentinelId, ref _selectedChannelIndex, nameof(SelectedChannelIndex));
        }
        else if (propertyName == nameof(SelectedThread))
        {
            SetSelectorId(ref _selectedThreadId, value?.Id ?? SentinelId, nameof(SelectedThreadId), forceNotify: true);
            SyncSelectedIndexFromId(Threads, value?.Id ?? SentinelId, ref _selectedThreadIndex, nameof(SelectedThreadIndex));
        }
    }

    private static SharpClawSelectorItem? FindById(ObservableList<SharpClawSelectorItem> list, Guid id)
    {
        foreach (var item in list)
            if (item.Id == id) return item;
        return null;
    }

    private static bool SameSelectorSnapshot(
        ObservableList<SharpClawSelectorItem> list,
        System.Collections.Generic.IList<(Guid? Id, string Label)> desired)
    {
        if (list.Count != desired.Count)
            return false;

        for (var i = 0; i < desired.Count; i++)
        {
            if (NormalizeSelectorId(list[i].Id) != NormalizeSelectorId(desired[i].Id))
                return false;
            if (!string.Equals(list[i].DisplayName, desired[i].Label ?? string.Empty, StringComparison.Ordinal))
                return false;
        }

        return true;
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
            if (created?.Id is Guid createdThreadId
                && RealIdOrNull(SelectedChannelId) == channelId
                && RealIdOrNull(SelectedThreadId) == createdThreadId)
            {
                QueueSelectedThreadActivation("created thread");
            }
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

    private void ClearTranscript()
    {
        _transcriptBlocks.Clear();
        TranscriptText = string.Empty;
        TranscriptContent = EmptyTranscriptFragment();
    }

    private void ReplaceTranscript(System.Collections.Generic.IReadOnlyList<ChatMessageDto> history)
    {
        // Already on the UI sync context (callers ensure that). Keep the
        // Remote UI transaction bounded to one property change: we render
        // all chat bubbles into a single XamlFragment instead of mutating a
        // remote ObservableList and asking the VS host to realize item
        // containers while the command callback is unwinding.
        _transcriptBlocks.Clear();

        var start = 0;
        if (history.Count > MaxRenderedHistoryMessages)
        {
            start = history.Count - MaxRenderedHistoryMessages;
            _transcriptBlocks.Add(new SharpClawChatTurn(
                SharpClawTurnKind.System,
                "system",
                $"Showing the last {MaxRenderedHistoryMessages} of {history.Count} messages.",
                DateTimeOffset.Now.LocalDateTime.ToString("HH:mm")));
        }

        for (var i = start; i < history.Count; i++)
            _transcriptBlocks.Add(BuildTurnFromHistory(history[i]));

        RenderTranscriptBlocks();
    }

    private void RenderTranscriptBlocks()
    {
        TranscriptText = BuildTranscriptText(_transcriptBlocks);
        TranscriptContent = BuildTranscriptFragment(_transcriptBlocks);
    }

    private static string BuildTranscriptText(System.Collections.Generic.IReadOnlyList<SharpClawChatTurn> turns)
    {
        if (turns.Count == 0)
            return string.Empty;

        var sb = new StringBuilder();
        foreach (var turn in turns)
        {
            AppendTranscriptBlock(sb, turn.Sender, turn.Timestamp, turn.Body, addSeparator: sb.Length > 0);
        }

        return sb.ToString();
    }

    private static XamlFragment EmptyTranscriptFragment()
        => new("<TextBlock xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" />");

    private static XamlFragment BuildTranscriptFragment(System.Collections.Generic.IReadOnlyList<SharpClawChatTurn> turns)
    {
        if (turns.Count == 0)
            return EmptyTranscriptFragment();

        var xaml = new StringBuilder();
        xaml.Append("<StackPanel xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" ")
            .Append("xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" ")
            .Append("xmlns:styles=\"clr-namespace:Microsoft.VisualStudio.Shell;assembly=Microsoft.VisualStudio.Shell.15.0\" ")
            .Append("TextElement.Foreground=\"{DynamicResource {x:Static styles:VsBrushes.ToolWindowTextKey}}\">");

        foreach (var turn in turns)
            AppendBubbleXaml(xaml, turn);

        xaml.Append("</StackPanel>");
        return new XamlFragment(xaml.ToString());
    }

    private static void AppendBubbleXaml(StringBuilder xaml, SharpClawChatTurn turn)
    {
        var background = "#5022C55E";
        var border = "#AA22C55E";
        var align = "Left";
        var textAlign = "Left";
        var foreground = "{DynamicResource {x:Static styles:VsBrushes.ToolWindowTextKey}}";

        if (turn.IsLocalUser)
        {
            background = "#508B5CF6";
            border = "#AA8B5CF6";
            align = "Right";
        }
        else if (turn.IsRemoteUser)
        {
            background = "#503B82F6";
            border = "#AA3B82F6";
            align = "Right";
        }
        else if (turn.IsSystem)
        {
            background = "Transparent";
            border = "Transparent";
            align = "Stretch";
            textAlign = "Center";
            foreground = "{DynamicResource {x:Static styles:VsBrushes.GrayTextKey}}";
        }

        xaml.Append("<Grid Margin=\"8,5\" HorizontalAlignment=\"Stretch\">")
            .Append("<Border Padding=\"10,7\" CornerRadius=\"6\" HorizontalAlignment=\"")
            .Append(align)
            .Append("\" Background=\"")
            .Append(background)
            .Append("\" BorderBrush=\"")
            .Append(border)
            .Append("\" BorderThickness=\"1\" MinWidth=\"240\" MaxWidth=\"980\">")
            .Append("<StackPanel>");

        if (!string.IsNullOrWhiteSpace(turn.Sender) || !string.IsNullOrWhiteSpace(turn.Timestamp))
        {
            xaml.Append("<TextBlock FontWeight=\"SemiBold\" FontSize=\"11\" Margin=\"0,0,0,3\" TextAlignment=\"")
                .Append(textAlign)
                .Append("\" Foreground=\"{DynamicResource {x:Static styles:VsBrushes.GrayTextKey}}\" Text=\"")
                .Append(XmlEscape(BuildBubbleHeader(turn)))
                .Append("\" />");
        }

        AppendMarkdownBodyXaml(xaml, turn.Body, foreground, textAlign);
        xaml.Append("</StackPanel></Border></Grid>");
    }

    private static void AppendMarkdownBodyXaml(StringBuilder xaml, string? body, string foreground, string textAlign)
    {
        var text = NormalizeMarkdownText(body);
        if (string.IsNullOrEmpty(text))
        {
            xaml.Append("<TextBlock />");
            return;
        }

        var paragraph = new System.Collections.Generic.List<string>();
        var code = new StringBuilder();
        var inCodeFence = false;
        var lines = text.Split('\n');

        void FlushParagraph()
        {
            if (paragraph.Count == 0)
                return;

            AppendMarkdownParagraphXaml(xaml, paragraph, foreground, textAlign);
            paragraph.Clear();
        }

        void FlushCodeBlock()
        {
            AppendCodeBlockXaml(xaml, code.ToString(), foreground);
            code.Clear();
        }

        foreach (var line in lines)
        {
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                if (inCodeFence)
                {
                    FlushCodeBlock();
                    inCodeFence = false;
                }
                else
                {
                    FlushParagraph();
                    inCodeFence = true;
                }

                continue;
            }

            if (inCodeFence)
            {
                code.AppendLine(line);
                continue;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                FlushParagraph();
                continue;
            }

            paragraph.Add(line);
        }

        if (inCodeFence)
            FlushCodeBlock();
        FlushParagraph();
    }

    private static void AppendMarkdownParagraphXaml(
        StringBuilder xaml,
        System.Collections.Generic.IReadOnlyList<string> lines,
        string foreground,
        string textAlign)
    {
        var heading = TryParseHeading(lines);
        var fontSize = heading.Level switch
        {
            1 => "15",
            2 => "13",
            3 => "12",
            _ => string.Empty,
        };

        xaml.Append("<TextBlock TextWrapping=\"Wrap\" FontFamily=\"Consolas\" Margin=\"0,")
            .Append(heading.IsHeading ? "6,0,3" : "2,0,2")
            .Append("\" TextAlignment=\"")
            .Append(textAlign)
            .Append("\" Foreground=\"")
            .Append(foreground)
            .Append("\"");

        if (heading.IsHeading)
        {
            xaml.Append(" FontWeight=\"SemiBold\" FontSize=\"")
                .Append(fontSize)
                .Append("\"");
        }

        xaml.Append(">");

        for (var i = 0; i < lines.Count; i++)
        {
            if (i > 0)
                xaml.Append("<LineBreak />");

            var line = heading.IsHeading ? heading.Text : lines[i];
            AppendInlineMarkdownXaml(xaml, line);
        }

        xaml.Append("</TextBlock>");
    }

    private static (bool IsHeading, int Level, string Text) TryParseHeading(System.Collections.Generic.IReadOnlyList<string> lines)
    {
        if (lines.Count != 1)
            return (false, 0, string.Empty);

        var trimmed = lines[0].TrimStart();
        var level = 0;
        while (level < trimmed.Length && level < 3 && trimmed[level] == '#')
            level++;

        if (level == 0 || level >= trimmed.Length || trimmed[level] != ' ')
            return (false, 0, string.Empty);

        return (true, level, trimmed[(level + 1)..]);
    }

    private static void AppendCodeBlockXaml(StringBuilder xaml, string code, string foreground)
    {
        xaml.Append("<Border Margin=\"0,6,0,6\" Padding=\"8\" CornerRadius=\"4\" ")
            .Append("Background=\"#26000000\" BorderBrush=\"#50808080\" BorderThickness=\"1\">")
            .Append("<TextBlock TextWrapping=\"Wrap\" FontFamily=\"Consolas\" Foreground=\"")
            .Append(foreground)
            .Append("\" Text=\"")
            .Append(XmlEscape(code.TrimEnd('\r', '\n')))
            .Append("\" /></Border>");
    }

    private static void AppendInlineMarkdownXaml(StringBuilder xaml, string text)
    {
        var i = 0;
        while (i < text.Length)
        {
            if (text[i] == '`')
            {
                var end = text.IndexOf('`', i + 1);
                if (end > i + 1)
                {
                    AppendInlineRunXaml(xaml, text[(i + 1)..end], " FontFamily=\"Consolas\" FontWeight=\"SemiBold\"");
                    i = end + 1;
                    continue;
                }
            }

            if (i + 1 < text.Length && text[i] == '*' && text[i + 1] == '*')
            {
                var end = text.IndexOf("**", i + 2, StringComparison.Ordinal);
                if (end > i + 2)
                {
                    xaml.Append("<Bold>");
                    AppendInlineRunXaml(xaml, text[(i + 2)..end], string.Empty);
                    xaml.Append("</Bold>");
                    i = end + 2;
                    continue;
                }
            }

            if (text[i] == '*')
            {
                var end = text.IndexOf('*', i + 1);
                if (end > i + 1)
                {
                    xaml.Append("<Italic>");
                    AppendInlineRunXaml(xaml, text[(i + 1)..end], string.Empty);
                    xaml.Append("</Italic>");
                    i = end + 1;
                    continue;
                }
            }

            var next = FindNextMarkdownMarker(text, i + 1);
            AppendInlineRunXaml(xaml, text[i..next], string.Empty);
            i = next;
        }
    }

    private static int FindNextMarkdownMarker(string text, int start)
    {
        for (var i = start; i < text.Length; i++)
        {
            if (text[i] is '`' or '*')
                return i;
        }

        return text.Length;
    }

    private static void AppendInlineRunXaml(StringBuilder xaml, string text, string attributes)
    {
        if (string.IsNullOrEmpty(text))
            return;

        xaml.Append("<Run")
            .Append(attributes)
            .Append(" Text=\"")
            .Append(XmlEscape(text))
            .Append("\" />");
    }

    private static string BuildBubbleHeader(SharpClawChatTurn turn)
    {
        if (string.IsNullOrWhiteSpace(turn.Timestamp))
            return turn.Sender;
        if (string.IsNullOrWhiteSpace(turn.Sender))
            return turn.Timestamp;
        return $"[{turn.Timestamp}] {turn.Sender}";
    }

    private static string XmlEscape(string? text)
    {
        var escaped = SecurityElement.Escape(text ?? string.Empty) ?? string.Empty;
        return escaped
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\r", "\n", StringComparison.Ordinal)
            .Replace("\n", "&#x0A;", StringComparison.Ordinal)
            .Replace("\t", "&#x09;", StringComparison.Ordinal);
    }

    private static string NormalizeMarkdownText(string? text)
        => (text ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\r", "\n", StringComparison.Ordinal);

    private static void AppendTranscriptBlock(
        StringBuilder sb,
        string sender,
        string timestamp,
        string body,
        bool addSeparator)
    {
        if (addSeparator)
            sb.AppendLine().AppendLine();

        if (!string.IsNullOrWhiteSpace(timestamp))
            sb.Append('[').Append(timestamp).Append("] ");

        sb.Append(string.IsNullOrWhiteSpace(sender) ? "message" : sender.Trim());
        sb.AppendLine();
        sb.Append(body ?? string.Empty);
    }

    // â”€â”€ Send â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    public async Task SendAsync(CancellationToken ct)
    {
        await SwitchToUi();
        if (RealIdOrNull(SelectedChannelId) is not Guid channelId)
        {
            Status = "Select a channel before sending.";
            return;
        }

        if (_isSending || _isThreadBusy)
        {
            Status = _isThreadBusy
                ? "Another client is streaming on this thread - wait for it to finish."
                : "A send is already in progress.";
            return;
        }

        var text = Composer?.Trim();
        if (string.IsNullOrEmpty(text))
            return;

        var threadId = RealIdOrNull(SelectedThreadId);
        Composer = string.Empty;
        var nowStamp = DateTimeOffset.Now.LocalDateTime.ToString("HH:mm");
        _transcriptBlocks.Add(new SharpClawChatTurn(SharpClawTurnKind.User, "you (VS2026)", text!, nowStamp));
        var assistant = new SharpClawChatTurn(SharpClawTurnKind.Assistant, "assistant", "|", nowStamp);
        _transcriptBlocks.Add(assistant);
        void SetAssistantText(string body)
        {
            assistant.Body = body;
            RenderTranscriptBlocks();
        }

        RenderTranscriptBlocks();

        var streamed = new StringBuilder();
        var needsNewline = false;

        try
        {
            IsBusy = true;
            _isSending = true;
            _historyStaleAfterSend = false;
            SendCommand.CanExecute = false;
            Status = "Sending...";

            using var response = await _backend
                .StartChatStreamAsync(channelId, threadId, text!, ct)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                await SwitchToUi();
                SetAssistantText($"x {(int)response.StatusCode} {response.ReasonPhrase}");
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
                            await SwitchToUi();
                            SetAssistantText(streamed.ToString() + "|");
                        }
                        break;

                    case ChatStreamEventType.ToolCallStart:
                        streamed.Append($"\n[tool:{ev.ToolName ?? "tool"}] {ev.ToolStatus ?? "started"}");
                        needsNewline = true;
                        await SwitchToUi();
                        SetAssistantText(streamed.ToString() + "|");
                        break;

                    case ChatStreamEventType.ToolCallResult:
                        streamed.Append($"\n[tool:{ev.ToolName ?? "tool"}] {ev.ToolStatus ?? "done"}");
                        needsNewline = true;
                        await SwitchToUi();
                        SetAssistantText(streamed.ToString() + "|");
                        break;

                    case ChatStreamEventType.ApprovalRequired:
                        streamed.Append($"\n[action:{ev.ToolName ?? "action"}] awaiting approval");
                        needsNewline = true;
                        await SwitchToUi();
                        SetAssistantText(streamed.ToString() + "|");
                        break;

                    case ChatStreamEventType.ApprovalResult:
                        streamed.Append($"\n[action:{ev.ToolName ?? "action"}] {ev.ToolStatus ?? "resolved"}");
                        needsNewline = true;
                        await SwitchToUi();
                        SetAssistantText(streamed.ToString() + "|");
                        break;

                    case ChatStreamEventType.Error:
                        await SwitchToUi();
                        Status = $"Error: {ev.Error}";
                        SetAssistantText(streamed.Length > 0
                            ? streamed.ToString() + $"\nx {ev.Error}"
                            : $"x {ev.Error}");
                        return;

                    case ChatStreamEventType.Done:
                        await SwitchToUi();
                        if (!string.IsNullOrEmpty(ev.FinalText))
                            SetAssistantText(ev.FinalText!);
                        else if (streamed.Length > 0)
                            SetAssistantText(streamed.ToString());
                        else
                            SetAssistantText("(empty response)");
                        Status = "Idle";
                        return;
                }
            }

            await SwitchToUi();
            SetAssistantText(streamed.Length > 0 ? streamed.ToString() : "(no response)");
        }
        catch (OperationCanceledException)
        {
            await SwitchToUi();
            SetAssistantText(streamed.Length > 0 ? streamed.ToString() : "(cancelled)");
        }
        catch (Exception ex)
        {
            await SwitchToUi();
            Status = $"Error: {ex.Message}";
            SetAssistantText(streamed.Length > 0
                ? streamed.ToString() + $"\nx {ex.Message}"
                : $"x {ex.Message}");
            await _log.WriteLineAsync($"Send failed: {ex}").ConfigureAwait(false);
        }
        finally
        {
            await SwitchToUi();
            IsBusy = false;
            _isSending = false;
            SendCommand.CanExecute = ComputeCanSend();

            if (_historyStaleAfterSend)
            {
                _historyStaleAfterSend = false;
                try { await ReloadHistoryAsync(CancellationToken.None).ConfigureAwait(false); }
                catch { /* best-effort */ }
            }
        }
    }
}
