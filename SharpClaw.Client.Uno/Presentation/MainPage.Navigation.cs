using Microsoft.UI.Xaml.Input;
using SharpClaw.Helpers;
using SharpClaw.Services;

namespace SharpClaw.Presentation;

// Navigation and footer links.
public sealed partial class MainPage
{
    private static string? LoadLocalSetting(string key)
        => App.Services?.GetService<ClientSettings>()?.Get(key);

    private async void OnNewChannelClick(object sender, RoutedEventArgs e)
    {
        _selectedChannelId = null;
        _selectedThreadId = null;
        _selectedAgentId = null;
        _selectedJobId = null;
        _pendingNewThread = false;
        ChatTitleBlock.Text = "> Select or create a channel";
        ChannelTabBar.Visibility = Visibility.Collapsed;
        _jobsMode = false;
        JobViewPanel.Visibility = Visibility.Collapsed;
        DeallocateJobView();
        ThreadSelectorPanel.Visibility = Visibility.Collapsed;
        OneOffWarning.Visibility = Visibility.Collapsed;
        ShowChatView();
        _chatBubblePoolUsed = 0;
        MessagesPanel.Children.Clear();
        await LoadSidebarAsync();
        await LoadAgentsAsync(null, null);
        UpdateCursor();
    }

    private void OnNewChannelPointerEntered(object sender, PointerRoutedEventArgs e)
        => Cursor.SetCommand("sharpclaw channel new ");

    private void OnNewChannelPointerExited(object sender, PointerRoutedEventArgs e)
        => UpdateCursor();

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        if (App.Services is not { } services) return;
        _ = services.GetRequiredService<ClientNavigationService>()
            .NavigateRouteAsync(this, "Settings");
    }

    private async void OnLogoutClick(object sender, RoutedEventArgs e)
    {
        if (App.Services is not { } services) return;
        await services.GetRequiredService<ClientSessionService>().ClearAsync();
        await services.GetRequiredService<ClientNavigationService>()
            .NavigateRouteAsync(this, "Login", Qualifiers.ClearBackStack);
    }

    private async void OnReportIssueClick(object sender, RoutedEventArgs e)
        => await Windows.System.Launcher.LaunchUriAsync(new Uri("https://github.com/mkn8rn/SharpClaw/issues"));

    private async void OnOfficialWebsiteClick(object sender, RoutedEventArgs e)
        => await Windows.System.Launcher.LaunchUriAsync(new Uri("https://sharpclaw.mkn8rn.com"));

    private async void OnMatrixCommunityClick(object sender, RoutedEventArgs e)
        => await Windows.System.Launcher.LaunchUriAsync(new Uri("https://matrix.to/#/#p1:matrix.mkn8rn.com"));

    private async void OnCreatorBlogClick(object sender, RoutedEventArgs e)
        => await Windows.System.Launcher.LaunchUriAsync(new Uri("https://blog.mkn8rn.com"));

    private void OnLegalNoticesClick(object sender, RoutedEventArgs e)
    {
        if (App.Services is not { } services) return;
        _ = services.GetRequiredService<ClientNavigationService>()
            .NavigateRouteAsync(this, "LegalNotices");
    }

    private void OnUserGuideClick(object sender, RoutedEventArgs e)
    {
        if (App.Services is not { } services) return;
        _ = services.GetRequiredService<ClientNavigationService>()
            .NavigateRouteAsync(this, "UserGuide");
    }

    private void OnMessageTextChanged(object sender, TextChangedEventArgs e)
        => UpdateCursor();

    private void UpdateCursor(string? overrideMessage = null)
    {
        var msg = overrideMessage ?? MessageInput.Text ?? string.Empty;
        string cmd;
        if (_selectedThreadId is { } tid) cmd = $"sharpclaw chat {tid}";
        else if (_selectedChannelId is { } cid) cmd = $"sharpclaw chat {cid}";
        else cmd = "sharpclaw chat new-channel";
        if (msg.Length > 0) cmd += " " + TerminalUI.Truncate(msg.Trim(), 40);
        Cursor.SetCommand(cmd + " ");
    }

    private static Windows.UI.Color ColorFrom(int rgb)
        => TerminalUI.ColorFrom(rgb);
}
