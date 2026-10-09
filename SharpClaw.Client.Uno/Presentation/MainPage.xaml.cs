using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SharpClaw.Helpers;
using SharpClaw.Services;

namespace SharpClaw.Presentation;

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

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (App.Services is null)
            return;

        _pageLifetime?.Cancel();
        _pageLifetime?.Dispose();
        _pageLifetime = new CancellationTokenSource();
        var token = _pageLifetime.Token;
        try
        {
            await CommitUiStateAsync(_ =>
            {
                ChatTitleBlock.Text = "> stateless chat (debug)";
                MessagesPanel.Children.Clear();
                _chatBubblePoolUsed = 0;
                _isSending = false;
                _streamCts?.Cancel();
                _streamCts = null;
                SetChatAvailability(false);
                CancelButton.Visibility = Visibility.Collapsed;
                return ValueTask.CompletedTask;
            }, token);
            UpdateCursor();
            await RefreshChatAvailabilityAsync(token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // A previous page visit cannot enable this page after navigation.
        }
        catch (Exception)
        {
            // Readiness and state-action failures never escape a UI event handler.
            // The page starts fail-closed and the Settings link remains available.
        }
    }

    private async void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _pageLifetime?.Cancel();
        try
        {
            var actions = App.Services?.GetService<ClientActionDispatcher>();
            if (actions is not null)
            {
                await actions.RunCommandAsync(
                    "client.chat.unload",
                    _ =>
                    {
                        _streamCts?.Cancel();
                        return ValueTask.CompletedTask;
                    });
            }
        }
        catch
        {
            // The active stream owns its cancellation path while the page leaves the visual tree.
        }
    }

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
            App.Services!.GetRequiredService<SharpClawApiClient>(), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        await CommitUiStateAsync(_ =>
        {
            SetChatAvailability(available);
            return ValueTask.CompletedTask;
        }, cancellationToken);
        return available;
    }

    private async void OnCheckProviderClick(object sender, RoutedEventArgs e)
    {
        if (_isSending || _pageLifetime is not { IsCancellationRequested: false } lifetime) return;
        try { await RefreshChatAvailabilityAsync(lifetime.Token); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception) { /* Keep the last fail-closed state if a client action is denied. */ }
    }

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

    private async Task CommitUiStateAsync(
        Func<CancellationToken, ValueTask> mutation,
        CancellationToken cancellationToken = default)
    {
        var actions = App.Services!.GetRequiredService<ClientActionDispatcher>();
        const string stateKey = "client.chat.ui";
        await actions.CommitStateAsync(
            stateKey,
            actions.GetStateVersion(stateKey),
            mutation,
            cancellationToken);
    }

    private static SolidColorBrush Brush(int rgb) => TerminalUI.Brush(rgb);
}
