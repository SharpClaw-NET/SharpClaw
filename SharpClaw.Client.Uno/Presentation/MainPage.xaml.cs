using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SharpClaw.Helpers;
using SharpClaw.Services;

namespace SharpClaw.Presentation;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1010",
    Justification = "This Uno view inherits nongeneric enumeration from the framework for XAML children; it is not a public collection API and adding generic enumeration would change framework semantics.")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001",
    Justification = "Uno owns reusable page instances. Per-visit cancellation sources are retired and disposed by OnUnloaded; the reusable send gate never allocates AvailableWaitHandle and releases every acquisition in finally.")]
public sealed partial class MainPage : Page
{
    private static readonly FontFamily MonoFont = TerminalUI.Mono;
    private bool _isSending;
    private bool _canChat;
    private CancellationTokenSource? _pageLifetime;
    private CancellationTokenSource? _streamCts;
    private CancellationToken _streamPageToken;
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly List<ChatBubbleRow> _chatBubblePool = [];
    private int _chatBubblePoolUsed;

    private readonly record struct ChatBubbleRow(
        Border Root,
        TextBlock Role,
        TextBlock Content);

    public MainPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => ClientUiEvent.Observe(async () =>
    {
        if (App.Services is null) return;
        SetChatAvailability(false);
        var previous = _pageLifetime;
        var lifetime = new CancellationTokenSource();
        _pageLifetime = lifetime;
        var token = lifetime.Token;
        if (previous is not null)
        {
            try { await previous.CancelAsync().ConfigureAwait(true); }
            finally { previous.Dispose(); }
        }
        token.ThrowIfCancellationRequested();
        if (_streamCts is { IsCancellationRequested: false } stream) await stream.CancelAsync().ConfigureAwait(true);
        await CommitUiStateAsync(_ =>
        {
            ChatTitleBlock.Text = "> stateless chat (debug)";
            MessagesPanel.Children.Clear();
            _chatBubblePoolUsed = 0;
            _isSending = false;
            _streamCts = null;
            SetChatAvailability(false);
            CancelButton.Visibility = Visibility.Collapsed;
            return ValueTask.CompletedTask;
        }, token).ConfigureAwait(true);
        UpdateCursor();
        await RefreshChatAvailabilityAsync(token).ConfigureAwait(true);
    });

    private void OnUnloaded(object sender, RoutedEventArgs e) => ClientUiEvent.Observe(async () =>
    {
        if (_pageLifetime is not { } lifetime) return;
        var stream = _streamCts;
        // Retire this visit before suspension; a new visit owns a different lifetime.
        _pageLifetime = null;
        try
        {
            await lifetime.CancelAsync().ConfigureAwait(true);
            var actions = App.Services?.GetService<ClientActionDispatcher>();
            if (actions is not null)
            {
                await actions.RunCommandAsync("client.chat.unload", async _ =>
                {
                    if (stream is { IsCancellationRequested: false }) await stream.CancelAsync().ConfigureAwait(true);
                }, CancellationToken.None).ConfigureAwait(true);
            }
        }
        finally { lifetime.Dispose(); }
    });

    private void SetChatAvailability(bool available)
    {
        _canChat = available;
        ChatSetupPrompt.Visibility = available ? Visibility.Collapsed : Visibility.Visible;
        MessageInput.IsEnabled = available && !_isSending;
        SendButton.IsEnabled = available && !_isSending;
    }

    private async Task<bool> RefreshChatAvailabilityAsync(CancellationToken cancellationToken)
    {
        var available = await StatelessChatReadiness.CheckAsync(
            App.Services!.GetRequiredService<SharpClawApiClient>(), cancellationToken).ConfigureAwait(true);
        cancellationToken.ThrowIfCancellationRequested();
        await CommitUiStateAsync(_ =>
        {
            SetChatAvailability(available);
            return ValueTask.CompletedTask;
        }, cancellationToken).ConfigureAwait(true);
        return available;
    }

    private void OnCheckProviderClick(object sender, RoutedEventArgs e) => ClientUiEvent.Observe(async () =>
    {
        if (_isSending || _pageLifetime is not { IsCancellationRequested: false } lifetime) return;
        await RefreshChatAvailabilityAsync(lifetime.Token).ConfigureAwait(true);
    });

    private void OnMessageTextChanged(object sender, TextChangedEventArgs e)
        => UpdateCursor();

    private void UpdateCursor()
        => Cursor.SetCommand(FormatChatCommand(MessageInput.Text));

    internal static string FormatChatCommand(string? message)
        => $"sharpclaw chat {message ?? string.Empty}";

    private ChatBubbleRow AcquireChatBubble()
    {
        if (_chatBubblePoolUsed < _chatBubblePool.Count)
            return _chatBubblePool[_chatBubblePoolUsed++];

        var role = new TextBlock
        {
            FontFamily = MonoFont,
            FontSize = 10,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
        };
        var content = new TextBlock
        {
            FontFamily = MonoFont,
            FontSize = 13,
            Foreground = Brush(0xCCCCCC),
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
        };
        var stack = new StackPanel { Spacing = 4 };
        stack.Children.Add(role);
        stack.Children.Add(content);
        var root = new Border
        {
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 8, 12, 8),
            MaxWidth = 700,
            Margin = new Thickness(0, 2, 0, 2),
            Child = stack,
        };

        var entry = new ChatBubbleRow(root, role, content);
        _chatBubblePool.Add(entry);
        _chatBubblePoolUsed++;
        return entry;
    }

    private ChatBubbleRow AppendMessage(string role, string content, bool error = false)
    {
        var row = AcquireChatBubble();
        var isUser = string.Equals(role, "user", StringComparison.OrdinalIgnoreCase);
        row.Root.Background = Brush(isUser ? 0x1A2A1A : 0x1A1A1A);
        row.Root.HorizontalAlignment = isUser
            ? HorizontalAlignment.Right
            : HorizontalAlignment.Left;
        row.Role.Text = isUser ? "you" : "assistant";
        row.Role.Foreground = Brush(isUser ? 0x00FF00 : 0x00AAFF);
        row.Content.Text = content;
        row.Content.Foreground = Brush(error ? 0xFF4444 : 0xCCCCCC);
        MessagesPanel.Children.Add(row.Root);
        return row;
    }

    private void ScrollToBottom()
    {
        MessagesScroller.UpdateLayout();
        MessagesScroller.ChangeView(null, MessagesScroller.ScrollableHeight, null);
    }

    private static async Task CommitUiStateAsync(
        Func<CancellationToken, ValueTask> mutation,
        CancellationToken cancellationToken = default)
    {
        var actions = App.Services!.GetRequiredService<ClientActionDispatcher>();
        const string stateKey = "client.chat.ui";
        await actions.CommitStateAsync(
            stateKey,
            actions.GetStateVersion(stateKey),
            mutation,
            cancellationToken).ConfigureAwait(true);
    }

    private static SolidColorBrush Brush(int rgb) => TerminalUI.Brush(rgb);
}
