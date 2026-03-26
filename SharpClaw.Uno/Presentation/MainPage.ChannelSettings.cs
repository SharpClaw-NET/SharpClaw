using System.Text;
using System.Text.Json;
using SharpClaw.Helpers;
using SharpClaw.Services;

namespace SharpClaw.Presentation;

// Channel settings tab: general config, allowed agents, transcription, audio device, permissions.
public sealed partial class MainPage
{
    private PermissionEditorBuilder? _permEditor;

    private async void OnTabSettingsClick(object sender, RoutedEventArgs e)
    {
        if (_settingsMode || _selectedChannelId is null) return;
        _settingsMode = true;
        _tasksMode = false;
        _jobsMode = false;
        UpdateTabHighlight();
        MessagesScroller.Visibility = Visibility.Collapsed;
        ChatInputArea.Visibility = Visibility.Collapsed;
        JobViewPanel.Visibility = Visibility.Collapsed;
        DeallocateJobView();
        TaskViewPanel.Visibility = Visibility.Collapsed;
        DeallocateTaskView();
        AgentSelectorPanel.Visibility = Visibility.Collapsed;
        ThreadSelectorPanel.Visibility = Visibility.Collapsed;
        OneOffWarning.Visibility = Visibility.Collapsed;
        SettingsScroller.Visibility = Visibility.Visible;
        await LoadChannelSettingsAsync(_selectedChannelId.Value);
    }

    private async Task LoadChannelSettingsAsync(Guid channelId)
    {
        SettingsPanel.Children.Clear();

        var api = App.Services!.GetRequiredService<SharpClawApiClient>();

        bool disableChatHeader = false;
        string? customChatHeader = null;
        List<(Guid Id, string Name, string ProviderModel)> allowedAgents = [];
        Guid? channelPermSetId = null;
        Guid? channelDefaultAgentId = null;

        try
        {
            using var chResp = await api.GetAsync($"/channels/{channelId}");
            if (!chResp.IsSuccessStatusCode) { AddSettingsLabel("✗ Failed to load channel settings", 0xFF4444); return; }

            using var chStream = await chResp.Content.ReadAsStreamAsync();
            using var chDoc = await JsonDocument.ParseAsync(chStream);
            var root = chDoc.RootElement;

            disableChatHeader = root.TryGetProperty("disableChatHeader", out var dch) && dch.GetBoolean();
            if (root.TryGetProperty("customChatHeader", out var cch) && cch.ValueKind == JsonValueKind.String) customChatHeader = cch.GetString();
            if (root.TryGetProperty("permissionSetId", out var psi) && psi.ValueKind == JsonValueKind.String) channelPermSetId = psi.GetGuid();
            if (root.TryGetProperty("agent", out var agentProp) && agentProp.ValueKind == JsonValueKind.Object
                && agentProp.TryGetProperty("id", out var defAgentId) && defAgentId.ValueKind == JsonValueKind.String) channelDefaultAgentId = defAgentId.GetGuid();

            if (root.TryGetProperty("allowedAgents", out var aaProp) && aaProp.ValueKind == JsonValueKind.Array)
                foreach (var a in aaProp.EnumerateArray())
                    if (a.TryGetProperty("id", out var idP) && idP.ValueKind == JsonValueKind.String)
                    {
                        var id = idP.GetGuid();
                        var name = a.TryGetProperty("name", out var np) ? np.GetString() ?? "?" : "?";
                        var model = a.TryGetProperty("modelName", out var mp) ? mp.GetString() ?? "" : "";
                        var provider = a.TryGetProperty("providerName", out var pp) ? pp.GetString() ?? "" : "";
                        allowedAgents.Add((id, name, $"{provider}/{model}"));
                    }
        }
        catch { AddSettingsLabel("✗ Failed to load settings", 0xFF4444); return; }

        Guid? permRoleId = null;
        JsonElement? permJson = null;

        if (channelPermSetId is not null)
        {
            permRoleId = _allRoles.FirstOrDefault(r => r.PermissionSetId == channelPermSetId)?.Id;
            if (permRoleId is not null)
            {
                try
                {
                    using var permResp = await api.GetAsync($"/roles/{permRoleId}/permissions");
                    if (permResp.IsSuccessStatusCode) { using var pStream = await permResp.Content.ReadAsStreamAsync(); using var pDoc = await JsonDocument.ParseAsync(pStream); permJson = pDoc.RootElement.Clone(); }
                }
                catch { /* swallow */ }
            }
        }

        _resourceLookupCache.Clear();
        HashSet<Guid> transcriptionModelIds = [];
        try
        {
            var lookupTasks = TerminalUI.ResourceAccessTypes.Select(async t =>
            {
                try { using var resp = await api.GetAsync($"/resources/lookup/{t.ApiName}"); if (resp.IsSuccessStatusCode) { using var s = await resp.Content.ReadAsStreamAsync(); var items = await JsonSerializer.DeserializeAsync<List<ResourceItemDto>>(s, Json); return (t.ApiName, Items: items ?? []); } }
                catch { /* swallow */ }
                return (t.ApiName, Items: new List<ResourceItemDto>());
            });
            foreach (var (apiName, items) in await Task.WhenAll(lookupTasks))
                _resourceLookupCache[apiName] = items;
        }
        catch { /* swallow */ }

        try
        {
            using var modelsResp = await api.GetAsync("/models");
            if (modelsResp.IsSuccessStatusCode)
            {
                using var modelsStream = await modelsResp.Content.ReadAsStreamAsync();
                using var modelsDoc = await JsonDocument.ParseAsync(modelsStream);
                foreach (var m in modelsDoc.RootElement.EnumerateArray())
                    if (m.TryGetProperty("capabilities", out var cap) && cap.GetString() is { } capStr && capStr.Contains("Transcription", StringComparison.OrdinalIgnoreCase))
                        if (m.TryGetProperty("id", out var mid) && mid.ValueKind == JsonValueKind.String) transcriptionModelIds.Add(mid.GetGuid());
            }
        }
        catch { /* swallow */ }

        await BuildSettingsPanelAsync(api, channelId, disableChatHeader, customChatHeader, allowedAgents, channelDefaultAgentId, permRoleId, permJson, transcriptionModelIds);
    }

    private async Task BuildSettingsPanelAsync(SharpClawApiClient api, Guid channelId, bool disableChatHeader, string? customChatHeader,
        List<(Guid Id, string Name, string ProviderModel)> allowedAgents,
        Guid? channelDefaultAgentId, Guid? permRoleId, JsonElement? permJson, HashSet<Guid> transcriptionModelIds)
    {
        SettingsPanel.Children.Clear();

        // ── General ──
        AddSettingsSection("General", "Basic channel configuration");
        var headerRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        headerRow.Children.Add(new TextBlock { Text = "disable chat header:", FontFamily = _monoFont, FontSize = 11, Foreground = Brush(0xCCCCCC), VerticalAlignment = VerticalAlignment.Center });
        var toggle = new ToggleSwitch { IsOn = disableChatHeader, OnContent = "yes", OffContent = "no" };
        ToolTipService.SetToolTip(toggle, "Suppress the metadata header (time, user, role, bio) prepended to each message sent to the model");
        toggle.Toggled += async (_, _) =>
        {
            var api = App.Services!.GetRequiredService<SharpClawApiClient>();
            try { var body = JsonSerializer.Serialize(new { disableChatHeader = toggle.IsOn }, Json); await api.PutAsync($"/channels/{channelId}", new StringContent(body, Encoding.UTF8, "application/json")); }
            catch { /* swallow */ }
        };
        headerRow.Children.Add(toggle);
        SettingsPanel.Children.Add(headerRow);

        // Custom chat header template
        SettingsPanel.Children.Add(new TextBlock { Text = "custom chat header:", FontFamily = _monoFont, FontSize = 11, Foreground = Brush(0xCCCCCC), Margin = new Thickness(0, 8, 0, 2) });
        SettingsPanel.Children.Add(new TextBlock { Text = "Template override for the metadata header. Use {{tag}} placeholders (e.g. {{time}}, {{user}}, {{agent-role}}, {{Models:{Name}}}).", FontFamily = _monoFont, FontSize = 10, Foreground = Brush(0x808080), TextWrapping = TextWrapping.Wrap, MaxWidth = 520, Margin = new Thickness(0, 0, 0, 4) });
        var customHeaderBox = new TextBox { FontFamily = _monoFont, FontSize = 11, Foreground = Brush(0x00FF00),
            Background = Brush(0x1A1A1A), BorderBrush = Brush(0x333333), BorderThickness = new Thickness(1),
            Text = customChatHeader ?? "", AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap, MinHeight = 60,
            PlaceholderText = "(uses agent header or default)" };
        ToolTipService.SetToolTip(customHeaderBox, "Overrides the agent's custom header and the default header. Leave empty to inherit.");
        var saveHeaderBtn = new Button { Content = new TextBlock { Text = "Save Header", FontFamily = _monoFont, FontSize = 12, Foreground = Brush(0x00FF00) }, Background = Brush(0x1A2A1A), BorderBrush = Brush(0x00FF00), BorderThickness = new Thickness(1), Padding = new Thickness(12, 6), Margin = new Thickness(0, 4, 0, 0) };
        saveHeaderBtn.Click += async (_, _) =>
        {
            var header = string.IsNullOrWhiteSpace(customHeaderBox.Text) ? null : customHeaderBox.Text.Trim();
            var body = JsonSerializer.Serialize(new { customChatHeader = header }, Json);
            try { await api.PutAsync($"/channels/{channelId}", new StringContent(body, Encoding.UTF8, "application/json")); }
            catch { /* swallow */ }
        };
        SettingsPanel.Children.Add(customHeaderBox);
        SettingsPanel.Children.Add(saveHeaderBtn);

        // ── Allowed Agents ──
        AddSettingsSection("Allowed Agents", "Additional agents permitted to respond in this channel besides the default");
        var agentsList = new StackPanel { Spacing = 4 };
        foreach (var agent in allowedAgents)
        {
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var lbl = new TextBlock { Text = $"• {agent.Name}  ({agent.ProviderModel})", FontFamily = _monoFont, FontSize = 12, Foreground = Brush(0xE0E0E0), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(lbl, 0); row.Children.Add(lbl);
            var rmBtn = new Button { Content = new TextBlock { Text = "✕", FontFamily = _monoFont, FontSize = 10, Foreground = Brush(0xFF4444) }, Background = _brushTransparent, BorderThickness = new Thickness(0), Padding = new Thickness(4, 2, 4, 2), MinWidth = 0, MinHeight = 0, Tag = agent.Id };
            rmBtn.Click += async (s, _) => { if (s is Button { Tag: Guid aid }) { var api = App.Services!.GetRequiredService<SharpClawApiClient>(); try { await api.DeleteAsync($"/channels/{channelId}/agents/{aid}"); } catch { /* swallow */ } await ReloadSettingsAndAgentsAsync(channelId); } };
            Grid.SetColumn(rmBtn, 1); row.Children.Add(rmBtn);
            agentsList.Children.Add(row);
        }
        if (allowedAgents.Count == 0) agentsList.Children.Add(new TextBlock { Text = "(no additional agents)", FontFamily = _monoFont, FontSize = 11, Foreground = Brush(0x777777), FontStyle = Windows.UI.Text.FontStyle.Italic });
        SettingsPanel.Children.Add(agentsList);

        var currentIds = new HashSet<Guid>(allowedAgents.Select(a => a.Id));
        var availableAgents = _allAgents.Where(a => !currentIds.Contains(a.Id)).ToList();
        var agentDisplayMap = new Dictionary<string, Guid>(availableAgents.Count);
        foreach (var a in availableAgents) agentDisplayMap[$"{a.Name}  ({a.ProviderName}/{a.ModelName})"] = a.Id;

        var agentSearch = new AutoSuggestBox { PlaceholderText = "Search agents to add\u2026", FontFamily = _monoFont, FontSize = 11, MinWidth = 300, Margin = new Thickness(0, 4, 0, 0) };
        ToolTipService.SetToolTip(agentSearch, "Type to filter, then click a suggestion to add the agent");
        agentSearch.TextChanged += (sender, args) => { if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return; var q = sender.Text.Trim(); sender.ItemsSource = string.IsNullOrEmpty(q) ? agentDisplayMap.Keys.ToList() : agentDisplayMap.Keys.Where(k => k.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList(); };
        agentSearch.QuerySubmitted += async (sender, args) => { var chosen = args.ChosenSuggestion?.ToString(); if (chosen is null || !agentDisplayMap.TryGetValue(chosen, out var aid)) return; sender.Text = string.Empty; var api = App.Services!.GetRequiredService<SharpClawApiClient>(); try { var body = JsonSerializer.Serialize(new { agentId = aid }, Json); await api.PostAsync($"/channels/{channelId}/agents", new StringContent(body, Encoding.UTF8, "application/json")); } catch { /* swallow */ } await ReloadSettingsAndAgentsAsync(channelId); };
        SettingsPanel.Children.Add(agentSearch);

        // ── Transcription Agent ──
        BuildTranscriptionAgentSection(channelId, channelDefaultAgentId, allowedAgents, transcriptionModelIds);

        // ── Audio Device ──
        BuildAudioDeviceSection();

        // ── Channel Permissions ──
        AddSettingsSection("Channel Permissions", "Pre-authorization overrides that let the agent act without requiring user approval");
        SettingsPanel.Children.Add(new TextBlock { Text = "⚠ These are pre-approvals. The agent still needs the permission on its own role to perform an action — pre-approval only means you won't be asked to manually approve it each time.", FontFamily = _monoFont, FontSize = 10, Foreground = Brush(0xFFAA00), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 6), MaxWidth = 520 });

        _permEditor = new PermissionEditorBuilder(api)
            .WithFlagClearance(false)
            .WithGrantClearance(false)
            .WithAutoSuggestBox(true)
            .WithExisting(permJson);

        _permEditor.BuildGlobalFlags(SettingsPanel);
        AddSettingsSubSection("Resource Accesses");
        await _permEditor.BuildResourceGrantsAsync(SettingsPanel);

        var saveBtn = new Button { Content = new TextBlock { Text = "Save Permissions", FontFamily = _monoFont, FontSize = 12, Foreground = Brush(0x00FF00) }, Background = Brush(0x1A2A1A), BorderBrush = Brush(0x00FF00), BorderThickness = new Thickness(1), Padding = new Thickness(16, 8), Margin = new Thickness(0, 8, 0, 0) };
        saveBtn.Click += async (_, _) => await SavePermissionsAsync(permRoleId, channelId);
        SettingsPanel.Children.Add(saveBtn);
    }

    private void BuildTranscriptionAgentSection(Guid channelId, Guid? channelDefaultAgentId,
        List<(Guid Id, string Name, string ProviderModel)> allowedAgents, HashSet<Guid> transcriptionModelIds)
    {
        AddSettingsSection("Transcription Agent", "Agent used for voice-to-text input via the microphone button (must be the channel default or an allowed agent)");
        var channelAgentIds = new HashSet<Guid>(allowedAgents.Select(a => a.Id));
        if (channelDefaultAgentId is { } defId) channelAgentIds.Add(defId);
        var txAgents = _allAgents.Where(a => channelAgentIds.Contains(a.Id) && (transcriptionModelIds.Count == 0 || transcriptionModelIds.Contains(a.ModelId))).ToList();
        var txDisplayMap = new Dictionary<string, Guid>(txAgents.Count);
        foreach (var a in txAgents) txDisplayMap.TryAdd($"{a.Name}  ({a.ProviderName}/{a.ModelName})", a.Id);

        var savedTxAgent = LoadLocalSetting(ClientSettings.TranscriptionAgentId);
        AgentDto? currentTxAgent = null;
        if (savedTxAgent is not null && Guid.TryParse(savedTxAgent, out var savedTxId)) currentTxAgent = _allAgents.FirstOrDefault(a => a.Id == savedTxId);

        var txCurrentRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        txCurrentRow.Children.Add(new TextBlock { Text = "current:", FontFamily = _monoFont, FontSize = 11, Foreground = Brush(0xCCCCCC), VerticalAlignment = VerticalAlignment.Center });
        var txCurrentLabel = new TextBlock { Text = currentTxAgent is not null ? $"{currentTxAgent.Name}  ({currentTxAgent.ProviderName}/{currentTxAgent.ModelName})" : "(none)", FontFamily = _monoFont, FontSize = 11, Foreground = Brush(currentTxAgent is not null ? 0xE0E0E0 : 0x777777), FontStyle = currentTxAgent is null ? Windows.UI.Text.FontStyle.Italic : Windows.UI.Text.FontStyle.Normal, VerticalAlignment = VerticalAlignment.Center };
        txCurrentRow.Children.Add(txCurrentLabel);

        if (currentTxAgent is not null)
        {
            var txClearBtn = new Button { Content = new TextBlock { Text = "\u2715", FontFamily = _monoFont, FontSize = 10, Foreground = Brush(0xFF4444) }, Background = _brushTransparent, BorderThickness = new Thickness(0), Padding = new Thickness(4, 2, 4, 2), MinWidth = 0, MinHeight = 0 };
            txClearBtn.Click += (_, _) => { SaveLocalSetting(ClientSettings.TranscriptionAgentId, null); txCurrentLabel.Text = "(none)"; txCurrentLabel.Foreground = Brush(0x777777); txCurrentLabel.FontStyle = Windows.UI.Text.FontStyle.Italic; if (txCurrentRow.Children.Count > 2) txCurrentRow.Children.RemoveAt(2); UpdateMicState(); };
            txCurrentRow.Children.Add(txClearBtn);
        }
        SettingsPanel.Children.Add(txCurrentRow);

        var txSearch = new AutoSuggestBox { PlaceholderText = txAgents.Count > 0 ? "Search transcription agents..." : "No transcription-capable agents available", FontFamily = _monoFont, FontSize = 11, MinWidth = 300, Margin = new Thickness(0, 4, 0, 0), IsEnabled = txAgents.Count > 0 };
        ToolTipService.SetToolTip(txSearch, "Type to filter agents with transcription-capable models");
        txSearch.TextChanged += (sender, args) => { if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return; var q = sender.Text.Trim(); sender.ItemsSource = string.IsNullOrEmpty(q) ? txDisplayMap.Keys.ToList() : txDisplayMap.Keys.Where(k => k.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList(); };
        txSearch.QuerySubmitted += (sender, args) =>
        {
            var chosen = args.ChosenSuggestion?.ToString();
            if (chosen is null || !txDisplayMap.TryGetValue(chosen, out var aid)) return;
            sender.Text = string.Empty;
            SaveLocalSetting(ClientSettings.TranscriptionAgentId, aid.ToString());
            txCurrentLabel.Text = chosen; txCurrentLabel.Foreground = Brush(0xE0E0E0); txCurrentLabel.FontStyle = Windows.UI.Text.FontStyle.Normal;
            if (txCurrentRow.Children.Count <= 2)
            {
                var clearBtn = new Button { Content = new TextBlock { Text = "\u2715", FontFamily = _monoFont, FontSize = 10, Foreground = Brush(0xFF4444) }, Background = _brushTransparent, BorderThickness = new Thickness(0), Padding = new Thickness(4, 2, 4, 2), MinWidth = 0, MinHeight = 0 };
                clearBtn.Click += (_, _) => { SaveLocalSetting(ClientSettings.TranscriptionAgentId, null); txCurrentLabel.Text = "(none)"; txCurrentLabel.Foreground = Brush(0x777777); txCurrentLabel.FontStyle = Windows.UI.Text.FontStyle.Italic; if (txCurrentRow.Children.Count > 2) txCurrentRow.Children.RemoveAt(2); UpdateMicState(); };
                txCurrentRow.Children.Add(clearBtn);
            }
            UpdateMicState();
        };
        SettingsPanel.Children.Add(txSearch);
    }

    private void BuildAudioDeviceSection()
    {
        AddSettingsSection("Audio Device", "Audio capture device used for voice-to-text transcription (saved locally per device)");
        var audioDevices = _resourceLookupCache.TryGetValue("audioDeviceAccesses", out var adItems) ? adItems : [];
        var adDisplayMap = new Dictionary<string, Guid>(audioDevices.Count);
        foreach (var d in audioDevices) adDisplayMap.TryAdd($"{d.Name}  ({d.Id.ToString()[..8]}…)", d.Id);

        var savedAdId = LoadLocalSetting(ClientSettings.SelectedAudioDeviceId);
        ResourceItemDto? currentAd = null;
        if (savedAdId is not null && Guid.TryParse(savedAdId, out var savedAdGuid)) currentAd = audioDevices.FirstOrDefault(d => d.Id == savedAdGuid);

        var adCurrentRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        adCurrentRow.Children.Add(new TextBlock { Text = "current:", FontFamily = _monoFont, FontSize = 11, Foreground = Brush(0xCCCCCC), VerticalAlignment = VerticalAlignment.Center });
        var adCurrentLabel = new TextBlock { Text = currentAd is not null ? $"{currentAd.Name}  ({currentAd.Id.ToString()[..8]}…)" : "(none)", FontFamily = _monoFont, FontSize = 11, Foreground = Brush(currentAd is not null ? 0xE0E0E0 : 0x777777), FontStyle = currentAd is null ? Windows.UI.Text.FontStyle.Italic : Windows.UI.Text.FontStyle.Normal, VerticalAlignment = VerticalAlignment.Center };
        adCurrentRow.Children.Add(adCurrentLabel);

        if (currentAd is not null)
        {
            var adClearBtn = new Button { Content = new TextBlock { Text = "\u2715", FontFamily = _monoFont, FontSize = 10, Foreground = Brush(0xFF4444) }, Background = _brushTransparent, BorderThickness = new Thickness(0), Padding = new Thickness(4, 2, 4, 2), MinWidth = 0, MinHeight = 0 };
            adClearBtn.Click += (_, _) => { SaveLocalSetting(ClientSettings.SelectedAudioDeviceId, null); adCurrentLabel.Text = "(none)"; adCurrentLabel.Foreground = Brush(0x777777); adCurrentLabel.FontStyle = Windows.UI.Text.FontStyle.Italic; if (adCurrentRow.Children.Count > 2) adCurrentRow.Children.RemoveAt(2); UpdateMicState(); };
            adCurrentRow.Children.Add(adClearBtn);
        }
        SettingsPanel.Children.Add(adCurrentRow);

        var adSearch = new AutoSuggestBox { PlaceholderText = audioDevices.Count > 0 ? "Search audio devices..." : "No audio devices available", FontFamily = _monoFont, FontSize = 11, MinWidth = 300, Margin = new Thickness(0, 4, 0, 0), IsEnabled = audioDevices.Count > 0 };
        ToolTipService.SetToolTip(adSearch, "Type to filter, then click a suggestion to select the audio device");
        adSearch.TextChanged += (sender, args) => { if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return; var q = sender.Text.Trim(); sender.ItemsSource = string.IsNullOrEmpty(q) ? adDisplayMap.Keys.ToList() : adDisplayMap.Keys.Where(k => k.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList(); };
        adSearch.QuerySubmitted += (sender, args) =>
        {
            var chosen = args.ChosenSuggestion?.ToString();
            if (chosen is null || !adDisplayMap.TryGetValue(chosen, out var did)) return;
            sender.Text = string.Empty;
            SaveLocalSetting(ClientSettings.SelectedAudioDeviceId, did.ToString());
            adCurrentLabel.Text = chosen; adCurrentLabel.Foreground = Brush(0xE0E0E0); adCurrentLabel.FontStyle = Windows.UI.Text.FontStyle.Normal;
            if (adCurrentRow.Children.Count <= 2)
            {
                var clearBtn = new Button { Content = new TextBlock { Text = "\u2715", FontFamily = _monoFont, FontSize = 10, Foreground = Brush(0xFF4444) }, Background = _brushTransparent, BorderThickness = new Thickness(0), Padding = new Thickness(4, 2, 4, 2), MinWidth = 0, MinHeight = 0 };
                clearBtn.Click += (_, _) => { SaveLocalSetting(ClientSettings.SelectedAudioDeviceId, null); adCurrentLabel.Text = "(none)"; adCurrentLabel.Foreground = Brush(0x777777); adCurrentLabel.FontStyle = Windows.UI.Text.FontStyle.Italic; if (adCurrentRow.Children.Count > 2) adCurrentRow.Children.RemoveAt(2); UpdateMicState(); };
                adCurrentRow.Children.Add(clearBtn);
            }
            UpdateMicState();
        };
        SettingsPanel.Children.Add(adSearch);
    }

    private async Task SavePermissionsAsync(Guid? roleId, Guid channelId)
    {
        var request = _permEditor?.CollectAll() ?? new Dictionary<string, object?>();

        var api = App.Services!.GetRequiredService<SharpClawApiClient>();
        try
        {
            if (roleId is null)
            {
                var createBody = JsonSerializer.Serialize(new { name = $"channel-{channelId.ToString()[..8]}" }, Json);
                var createResp = await api.PostAsync("/roles", new StringContent(createBody, Encoding.UTF8, "application/json"));
                if (!createResp.IsSuccessStatusCode) { using var errStream = await createResp.Content.ReadAsStreamAsync(); var errMsg = await TryExtractErrorAsync(errStream) ?? $"{(int)createResp.StatusCode} {createResp.ReasonPhrase}"; AddSettingsLabel($"✗ Failed to create permission set: {errMsg}", 0xFF4444); return; }
                using var createStream = await createResp.Content.ReadAsStreamAsync();
                using var createDoc = await JsonDocument.ParseAsync(createStream);
                roleId = createDoc.RootElement.GetProperty("id").GetGuid();
                var permSetId = createDoc.RootElement.GetProperty("permissionSetId").GetGuid();
                var assignBody = JsonSerializer.Serialize(new { permissionSetId = permSetId }, Json);
                var assignResp = await api.PutAsync($"/channels/{channelId}", new StringContent(assignBody, Encoding.UTF8, "application/json"));
                if (!assignResp.IsSuccessStatusCode) { using var errStream = await assignResp.Content.ReadAsStreamAsync(); var errMsg = await TryExtractErrorAsync(errStream) ?? $"{(int)assignResp.StatusCode} {assignResp.ReasonPhrase}"; AddSettingsLabel($"✗ Failed to assign permission set: {errMsg}", 0xFF4444); return; }
                await LoadRolesAsync();
            }

            var body = JsonSerializer.Serialize(request, Json);
            var resp = await api.PutAsync($"/roles/{roleId}/permissions", new StringContent(body, Encoding.UTF8, "application/json"));
            if (resp.IsSuccessStatusCode) await LoadChannelSettingsAsync(channelId);
            else { using var errStream = await resp.Content.ReadAsStreamAsync(); var errMsg = await TryExtractErrorAsync(errStream) ?? $"{(int)resp.StatusCode} {resp.ReasonPhrase}"; AddSettingsLabel($"✗ Save failed: {errMsg}", 0xFF4444); }
        }
        catch (Exception ex) { AddSettingsLabel($"✗ Save failed: {ex.Message}", 0xFF4444); }
    }

    private async Task ReloadSettingsAndAgentsAsync(Guid channelId)
    {
        await LoadAgentsAsync(_selectedAgentId, null);
        await LoadChannelSettingsAsync(channelId);
    }

    private void AddSettingsSection(string text, string? tooltip = null)
    {
        var block = new TextBlock { Text = $"── {text} ──", FontFamily = _monoFont, FontSize = 12, Foreground = Brush(0x00FF00), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
        if (tooltip is not null) ToolTipService.SetToolTip(block, tooltip);
        SettingsPanel.Children.Add(block);
    }

    private void AddSettingsSubSection(string text) =>
        SettingsPanel.Children.Add(new TextBlock { Text = text, FontFamily = _monoFont, FontSize = 11, Foreground = Brush(0xBBBBBB), Margin = new Thickness(0, 4, 0, 0) });

    private void AddSettingsLabel(string text, int color) =>
        SettingsPanel.Children.Add(new TextBlock { Text = text, FontFamily = _monoFont, FontSize = 11, Foreground = Brush(color) });
}
