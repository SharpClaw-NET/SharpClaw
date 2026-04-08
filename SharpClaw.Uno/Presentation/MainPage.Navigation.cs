using System.Text;
using System.Text.Json;
using Microsoft.UI.Xaml.Input;
using SharpClaw.Helpers;
using SharpClaw.Services;

namespace SharpClaw.Presentation;

// Navigation, footer links, microphone/transcription, role menus, helpers.
public sealed partial class MainPage
{
    // ── Microphone / voice input ────────────────────────────────

    private void UpdateMicState()
    {
        var agentId = LoadLocalSetting(ClientSettings.TranscriptionAgentId);
        var deviceId = LoadLocalSetting(ClientSettings.SelectedInputAudioId);
        var configured = agentId is not null && Guid.TryParse(agentId, out _)
                      && deviceId is not null && Guid.TryParse(deviceId, out _)
                      && _selectedChannelId is not null;
        var isActive = _activeTranscriptionJobId is not null;

        MicButton.IsEnabled = configured || isActive;
        MicButton.Opacity = configured || isActive ? 1.0 : 0.5;
        MicIcon.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(
            configured || isActive
                ? Windows.UI.Color.FromArgb(255, 0, 255, 0)
                : Windows.UI.Color.FromArgb(255, 100, 100, 100));

        if (isActive)
            ToolTipService.SetToolTip(MicButton, "Stop transcription");
        else if (configured)
            ToolTipService.SetToolTip(MicButton, "Start voice input");
        else
            ToolTipService.SetToolTip(MicButton, "Set a transcription agent and input audio in channel settings to enable voice input");
    }

    private async void OnMicClick(object sender, RoutedEventArgs e)
    {
        if (_selectedChannelId is not { } channelId) return;

        if (_activeTranscriptionJobId is { } activeJobId)
        {
            await StopTranscriptionAsync(channelId, activeJobId);
            return;
        }

        var agentIdStr = LoadLocalSetting(ClientSettings.TranscriptionAgentId);
        var deviceIdStr = LoadLocalSetting(ClientSettings.SelectedInputAudioId);
        if (agentIdStr is null || !Guid.TryParse(agentIdStr, out var agentId)
            || deviceIdStr is null || !Guid.TryParse(deviceIdStr, out var deviceId))
            return;

        MicButton.Opacity = 0.6;

        var api = App.Services!.GetRequiredService<SharpClawApiClient>();
        try
        {
            var body = JsonSerializer.Serialize(new { actionType = "TranscribeFromAudioDevice", resourceId = deviceId, agentId }, Json);
            var resp = await api.PostAsync($"/channels/{channelId}/jobs", new System.Net.Http.StringContent(body, Encoding.UTF8, "application/json"));
            if (!resp.IsSuccessStatusCode)
            {
                string errorDetail = $"{(int)resp.StatusCode} {resp.ReasonPhrase}";
                try { using var errStream = await resp.Content.ReadAsStreamAsync(); var errMsg = await TryExtractErrorAsync(errStream); if (errMsg is not null) errorDetail = errMsg; }
                catch { /* body not readable */ }
                AppendMessage("system", $"✗ Failed to start transcription: {errorDetail}", DateTimeOffset.Now, senderName: "system");
                ScrollToBottom();
                UpdateMicState();
                return;
            }

            using var s = await resp.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(s);
            var jobId = doc.RootElement.GetProperty("id").GetGuid();
            _activeTranscriptionJobId = jobId;

            MicIcon.Text = "\uE15B";
            MicButton.Opacity = 1.0;
            ToolTipService.SetToolTip(MicButton, "Stop transcription");
            SetTranscriptionInputState(active: true);

            var existingText = MessageInput.Text?.Trim() ?? string.Empty;
            _transcriptionPollCts = new CancellationTokenSource();
            _ = StreamTranscriptionSegmentsAsync(jobId, existingText, _transcriptionPollCts.Token);
        }
        catch (Exception ex)
        {
            AppendMessage("system", $"✗ Transcription error: {ex.Message}", DateTimeOffset.Now, senderName: "system");
            ScrollToBottom();
            _activeTranscriptionJobId = null;
            UpdateMicState();
        }
    }

    private async Task StopTranscriptionAsync(Guid channelId, Guid jobId)
    {
        _transcriptionPollCts?.Cancel();
        _transcriptionPollCts = null;
        var api = App.Services!.GetRequiredService<SharpClawApiClient>();
        try { await api.PostAsync($"/channels/{channelId}/jobs/{jobId}/stop", null); }
        catch { /* swallow */ }
        _activeTranscriptionJobId = null;
        MicIcon.Text = "\uE720";
        SetTranscriptionInputState(active: false);
        UpdateMicState();
    }

    private async Task StreamTranscriptionSegmentsAsync(Guid jobId, string prefixText, CancellationToken ct)
    {
        var api = App.Services!.GetRequiredService<SharpClawApiClient>();
        var dispatcher = DispatcherQueue;
        await Task.Delay(1000, ct).ConfigureAwait(false);

        var segments = new Dictionary<Guid, (string Text, double StartTime)>();
        var lastUiUpdate = 0L;
        var throttleTicks = System.Diagnostics.Stopwatch.Frequency * 150 / 1000;
        var lastDispatchedLength = 0;

        try
        {
            using var resp = await api.GetStreamAsync($"/jobs/{jobId}/stream", ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                dispatcher.TryEnqueue(() => { AppendMessage("system", $"\u2717 Failed to connect to transcription stream: {(int)resp.StatusCode}", DateTimeOffset.Now, senderName: "system"); ScrollToBottom(); });
                return;
            }

            using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var reader = new StreamReader(stream);

            while (!reader.EndOfStream && !ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (line is null) break;
                if (line.StartsWith("event:")) { if (line.AsSpan(6).Trim() is "done") break; continue; }
                if (!line.StartsWith("data: ")) continue;

                try
                {
                    var seg = JsonSerializer.Deserialize<TranscriptionSegmentDto>(line.AsMemory(6).Span, Json);
                    if (seg is null) continue;
                    if (!string.IsNullOrEmpty(seg.Text)) segments[seg.Id] = (seg.Text, seg.StartTime);

                    var now = System.Diagnostics.Stopwatch.GetTimestamp();
                    if (now - lastUiUpdate < throttleTicks) continue;
                    lastUiUpdate = now;

                    _transcriptionAccumulator.Clear();
                    if (prefixText.Length > 0) { _transcriptionAccumulator.Append(prefixText); _transcriptionAccumulator.Append(' '); }
                    var first = true;
                    foreach (var sv in segments.Values.OrderBy(sv => sv.StartTime)) { if (!first) _transcriptionAccumulator.Append(' '); _transcriptionAccumulator.Append(sv.Text); first = false; }
                    var snapshot = _transcriptionAccumulator.ToString();
                    lastDispatchedLength = snapshot.Length;
                    dispatcher.TryEnqueue(() => MessageInput.Text = snapshot);
                }
                catch { /* malformed SSE line */ }
            }

            if (segments.Count > 0)
            {
                _transcriptionAccumulator.Clear();
                if (prefixText.Length > 0) { _transcriptionAccumulator.Append(prefixText); _transcriptionAccumulator.Append(' '); }
                var first = true;
                foreach (var sv in segments.Values.OrderBy(sv => sv.StartTime)) { if (!first) _transcriptionAccumulator.Append(' '); _transcriptionAccumulator.Append(sv.Text); first = false; }
                var finalSnapshot = _transcriptionAccumulator.ToString();
                if (finalSnapshot.Length >= lastDispatchedLength) dispatcher.TryEnqueue(() => MessageInput.Text = finalSnapshot);
            }
        }
        catch (OperationCanceledException) { /* expected on stop */ }
        catch (Exception ex) { dispatcher.TryEnqueue(() => { AppendMessage("system", $"\u2717 Transcription stream error: {ex.Message}", DateTimeOffset.Now, senderName: "system"); ScrollToBottom(); }); }

        dispatcher.TryEnqueue(() =>
        {
            if (_activeTranscriptionJobId == jobId)
            {
                _activeTranscriptionJobId = null;
                MicIcon.Text = "\uE720";
                SetTranscriptionInputState(active: false);
                UpdateMicState();
            }
        });
    }

    private void SetTranscriptionInputState(bool active)
    {
        MessageInput.IsReadOnly = active;
        SendButton.IsEnabled = !active;
        if (active) { MessageInput.PlaceholderText = "Listening..."; MessageInput.Opacity = 0.7; }
        else { MessageInput.PlaceholderText = "Type a message..."; MessageInput.Opacity = 1.0; }
    }

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
        if (_activeTranscriptionJobId is not null) return;
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
