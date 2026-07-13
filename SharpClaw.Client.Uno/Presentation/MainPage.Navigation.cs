using System.Text;
using System.Text.Json;
using Microsoft.UI.Xaml.Input;
using SharpClaw.Helpers;
using SharpClaw.Services;

namespace SharpClaw.Presentation;

// Navigation, footer links, role menus, helpers.
public sealed partial class MainPage
{
    private static string? LoadLocalSetting(string key)
        => App.Services?.GetService<ClientSettings>()?.Get(key);

    private static void SaveLocalSetting(string key, string? value)
        => App.Services?.GetService<ClientSettings>()?.Set(key, value);

    // ── Navigation ───────────────────────────────────────────────

    private async void OnNewChannelClick(object sender, RoutedEventArgs e)
    {
        _selectedChannelId = null; _selectedThreadId = null; _selectedAgentId = null; _selectedJobId = null; _pendingNewThread = false;
        ChatTitleBlock.Text = "> Select or create a channel";
        ChannelTabBar.Visibility = Visibility.Collapsed;
        _settingsMode = false; _tasksMode = false; _jobsMode = false;
        SettingsScroller.Visibility = Visibility.Collapsed;
        TaskViewPanel.Visibility = Visibility.Collapsed; DeallocateTaskView();
        JobViewPanel.Visibility = Visibility.Collapsed; DeallocateJobView();
        ThreadSelectorPanel.Visibility = Visibility.Collapsed; OneOffWarning.Visibility = Visibility.Collapsed;
        ShowChatView();
        _chatBubblePoolUsed = 0; MessagesPanel.Children.Clear();
        await LoadSidebarAsync(); await LoadAgentsAsync(null, null); UpdateCursor();
    }

    private void OnNewChannelPointerEntered(object sender, PointerRoutedEventArgs e) => Cursor.SetCommand("sharpclaw channel new ");
    private void OnNewChannelPointerExited(object sender, PointerRoutedEventArgs e) => UpdateCursor();

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        if (App.Services is not { } services) return;
        _ = services.GetRequiredService<INavigator>().NavigateRouteAsync(this, "Settings");
    }

    private void OnLogoutClick(object sender, RoutedEventArgs e)
    {
        if (App.Services is not { } services) return;
        var api = services.GetRequiredService<SharpClawApiClient>();
        api.SetAccessToken(null!);
        _ = services.GetRequiredService<INavigator>().NavigateRouteAsync(this, "Login", qualifier: Qualifiers.ClearBackStack);
    }

    private async void OnReportIssueClick(object sender, RoutedEventArgs e) => await Windows.System.Launcher.LaunchUriAsync(new Uri("https://github.com/mkn8rn/SharpClaw/issues"));
    private async void OnOfficialWebsiteClick(object sender, RoutedEventArgs e) => await Windows.System.Launcher.LaunchUriAsync(new Uri("https://sharpclaw.mkn8rn.com"));
    private async void OnMatrixCommunityClick(object sender, RoutedEventArgs e) => await Windows.System.Launcher.LaunchUriAsync(new Uri("https://matrix.to/#/#p1:matrix.mkn8rn.com"));
    private async void OnCreatorBlogClick(object sender, RoutedEventArgs e) => await Windows.System.Launcher.LaunchUriAsync(new Uri("https://blog.mkn8rn.com"));

    private void OnLegalNoticesClick(object sender, RoutedEventArgs e)
    {
        if (App.Services is not { } services) return;
        _ = services.GetRequiredService<INavigator>().NavigateRouteAsync(this, "LegalNotices");
    }

    private void OnUserGuideClick(object sender, RoutedEventArgs e)
    {
        if (App.Services is not { } services) return;
        _ = services.GetRequiredService<INavigator>().NavigateRouteAsync(this, "UserGuide");
    }

    // ── Role assignment (right-click context menu) ─────────────

    private MenuFlyout? BuildRoleMenuFlyout(bool isUser, Guid? agentId)
    {
        if (_allRoles.Count == 0 && !isUser) return null;
        var flyout = new MenuFlyout();
        var sub = new MenuFlyoutSubItem { Text = isUser ? "Assign Role" : "Assign Role to Agent" };
        var removeItem = new MenuFlyoutItem { Text = "(none — remove role)", FontFamily = _monoFont, FontSize = 11 };
        removeItem.Click += async (_, _) => { if (isUser) await AssignRoleToSelfAsync(Guid.Empty); else if (agentId.HasValue) await AssignRoleToAgentAsync(agentId.Value, Guid.Empty); };
        sub.Items.Add(removeItem);

        if (_allRoles.Count > 0)
        {
            sub.Items.Add(new MenuFlyoutSeparator());
            foreach (var role in _allRoles)
            {
                var roleId = role.Id; var roleName = role.Name;
                var isCurrent = isUser ? _currentUserRoleId == roleId : _allAgents.FirstOrDefault(a => a.Id == agentId)?.RoleId == roleId;
                var item = new MenuFlyoutItem { Text = $"{(isCurrent ? "● " : "")}{roleName}", FontFamily = _monoFont, FontSize = 11 };
                item.Click += async (_, _) => { if (isUser) await AssignRoleToSelfAsync(roleId); else if (agentId.HasValue) await AssignRoleToAgentAsync(agentId.Value, roleId); };
                sub.Items.Add(item);
            }
        }
        flyout.Items.Add(sub);
        return flyout;
    }

    private async Task AssignRoleToAgentAsync(Guid agentId, Guid roleId)
    {
        var api = App.Services!.GetRequiredService<SharpClawApiClient>();
        try
        {
            var body = JsonSerializer.Serialize(new { roleId }, Json);
            var resp = await api.PutAsync($"/agents/{agentId}/role", new System.Net.Http.StringContent(body, Encoding.UTF8, "application/json"));
            if (resp.IsSuccessStatusCode)
            {
                await LoadAgentsAsync(_selectedAgentId, null); await LoadRolesAsync();
                var agent = _allAgents.FirstOrDefault(a => a.Id == agentId);
                var label = roleId == Guid.Empty ? $"✓ Removed role from {agent?.Name ?? "agent"}" : $"✓ Assigned role '{agent?.RoleName}' to {agent?.Name ?? "agent"}";
                AppendMessage("system", label, DateTimeOffset.Now, senderName: "system"); ScrollToBottom();
            }
            else { using var errStream = await resp.Content.ReadAsStreamAsync(); var errMsg = await TryExtractErrorAsync(errStream) ?? $"{(int)resp.StatusCode} {resp.ReasonPhrase}"; AppendMessage("system", $"✗ Role assignment failed: {errMsg}", DateTimeOffset.Now, senderName: "system"); ScrollToBottom(); }
        }
        catch (Exception ex) { AppendMessage("system", $"✗ Role assignment failed: {ex.Message}", DateTimeOffset.Now, senderName: "system"); ScrollToBottom(); }
    }

    private async Task AssignRoleToSelfAsync(Guid roleId)
    {
        var api = App.Services!.GetRequiredService<SharpClawApiClient>();
        try
        {
            var body = JsonSerializer.Serialize(new { roleId }, Json);
            var resp = await api.PutAsync("/auth/me/role", new System.Net.Http.StringContent(body, Encoding.UTF8, "application/json"));
            if (resp.IsSuccessStatusCode)
            {
                await LoadUserInfoAsync(); await LoadRolesAsync();
                var roleName = _allRoles.FirstOrDefault(r => r.Id == roleId)?.Name;
                var label = roleId == Guid.Empty ? "✓ Removed your role" : $"✓ Assigned role '{roleName}' to yourself";
                AppendMessage("system", label, DateTimeOffset.Now, senderName: "system"); ScrollToBottom();
            }
            else { using var errStream = await resp.Content.ReadAsStreamAsync(); var errMsg = await TryExtractErrorAsync(errStream) ?? $"{(int)resp.StatusCode} {resp.ReasonPhrase}"; AppendMessage("system", $"✗ Role assignment failed: {errMsg}", DateTimeOffset.Now, senderName: "system"); ScrollToBottom(); }
        }
        catch (Exception ex) { AppendMessage("system", $"✗ Role assignment failed: {ex.Message}", DateTimeOffset.Now, senderName: "system"); ScrollToBottom(); }
    }

    private static async Task<string?> TryExtractErrorAsync(Stream stream)
    {
        try { using var doc = await JsonDocument.ParseAsync(stream); if (doc.RootElement.TryGetProperty("error", out var ep) && ep.ValueKind == JsonValueKind.String) return ep.GetString(); }
        catch { /* not JSON */ }
        return null;
    }

    // ── Helpers ──────────────────────────────────────────────────

    private void OnMessageTextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateCursor();
    }

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
