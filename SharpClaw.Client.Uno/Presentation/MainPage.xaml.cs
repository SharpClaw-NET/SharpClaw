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
    private CancellationTokenSource? _streamCts;
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

        await CommitUiStateAsync(_ =>
        {
            ChatTitleBlock.Text = "> direct chat";
            MessagesPanel.Children.Clear();
            _chatBubblePoolUsed = 0;
            _isSending = false;
            _streamCts?.Cancel();
            _streamCts = null;
            MessageInput.IsEnabled = true;
            SendButton.IsEnabled = true;
            CancelButton.Visibility = Visibility.Collapsed;
            return ValueTask.CompletedTask;
        });

        UpdateCursor();
        MessageInput.Focus(FocusState.Programmatic);
    }

    private async void OnUnloaded(object sender, RoutedEventArgs e)
    {
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

    private void OnMessageTextChanged(object sender, TextChangedEventArgs e)
        => UpdateCursor();

    private void UpdateCursor(string? command = null)
        => Cursor.SetCommand(command ?? "sharpclaw chat ");

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
