using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.UI.Xaml.Media;
using SharpClaw.Helpers;
using SharpClaw.Services;

namespace SharpClaw.Presentation;

public sealed partial class FirstSetupPage : Page
{
    private static JsonSerializerOptions Json => TerminalUI.Json;
    private static FontFamily Mono => TerminalUI.Mono;

    private SharpClawApiClient Api => App.Services!.GetRequiredService<SharpClawApiClient>();

    // State used across async steps
    private TaskCompletionSource<bool>? _providerTcs;
    private TaskCompletionSource<bool>? _apiKeyTcs;
    private TaskCompletionSource<Guid?>? _agentTcs;
    private TaskCompletionSource<bool>? _localModelTcs;
    private TaskCompletionSource<bool>? _roleTcs;
    private TaskCompletionSource<bool>? _upgradePromptTcs;
    private bool _localOnly;
    private bool _switchToCloud;
    private List<ProviderDto>? _providers;

    private PermissionEditorBuilder? _permEditor;

    public FirstSetupPage()
    {
        this.InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (FirstSetupMarker.NeedsUpgradeRerun)
        {
            // Major version advanced since last setup — ask whether to redo
            var oldVer = FirstSetupMarker.CompletedMajorVersion;
            var newVer = FirstSetupMarker.CurrentMajorVersion;
            var label = oldVer.HasValue
                ? $"v{oldVer} → v{newVer}"
                : $"v{newVer}";

            UpgradeVersionLabel.Text = label;
            UpgradePromptPanel.Visibility = Visibility.Visible;
            SkipSetupPanel.Visibility = Visibility.Collapsed;

            _upgradePromptTcs = new TaskCompletionSource<bool>();
            var redo = await _upgradePromptTcs.Task;
            UpgradePromptPanel.Visibility = Visibility.Collapsed;

            if (!redo)
            {
                // User chose to skip — stamp current version and go to Main
                FirstSetupMarker.MarkCompleted();
                var navigator = App.Services!.GetRequiredService<INavigator>();
                await navigator.NavigateRouteAsync(this, "Main", qualifier: Qualifiers.ClearBackStack);
                return;
            }

            // User chose redo — fall through to normal setup
            SkipSetupPanel.Visibility = Visibility.Visible;
        }

        Cursor.SetCommand("sharpclaw setup ");
        await RunSetupAsync();
    }

    // ── Step rendering ──────────────────────────────────────────

    private void AppendStep(string text, bool done = false, bool error = false, string? copyText = null)
    {
        var icon = done ? "✓" : error ? "✗" : "›";
        var iconColor = done ? 0x00FF00 : error ? 0xFF6666 : 0xFFCC00;
        var textColor = done ? 0xE0E0E0 : error ? 0xFF6666 : 0xE0E0E0;

        var grid = new Grid { ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var iconBlock = new TextBlock
        {
            Text = icon,
            FontFamily = Mono,
            FontSize = 15,
            Foreground = TerminalUI.Brush(iconColor),
        };
        Grid.SetColumn(iconBlock, 0);
        grid.Children.Add(iconBlock);

        var textBlock = new TextBlock
        {
            Text = text,
            FontFamily = Mono,
            FontSize = 15,
            Foreground = TerminalUI.Brush(textColor),
            TextWrapping = TextWrapping.Wrap,
        };
        Grid.SetColumn(textBlock, 1);
        grid.Children.Add(textBlock);

        if (copyText is not null)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var copyBtn = new Button
            {
                Content = "Copy",
                FontFamily = Mono,
                FontSize = 11,
                Padding = new Thickness(6, 2),
                MinHeight = 0, MinWidth = 0,
                VerticalAlignment = VerticalAlignment.Top,
                Background = TerminalUI.Brush(0x1A1A1A),
                Foreground = TerminalUI.Brush(0xCCCCCC),
                BorderBrush = TerminalUI.Brush(0x444444),
                BorderThickness = new Thickness(1),
            };
            var captured = copyText;
            copyBtn.Click += (_, _) =>
            {
                TerminalUI.CopyToClipboard(captured);
                copyBtn.Content = "Copied";
            };
            Grid.SetColumn(copyBtn, 2);
            grid.Children.Add(copyBtn);
        }

        // Insert before the Cursor (always last child)
        var idx = StepsPanel.Children.Count - 1;
        StepsPanel.Children.Insert(idx < 0 ? 0 : idx, grid);
    }

    // ── Main setup flow ─────────────────────────────────────────

    private async Task RunSetupAsync()
    {
        // ── Step 0: Admin permission check ──
        AppendStep("Checking admin permissions...");
        bool isAdmin;
        try
        {
            var resp = await Api.GetAsync("/auth/me");
            if (!resp.IsSuccessStatusCode)
            {
                ReplaceLastStep("Not authenticated. Please log in as admin.", error: true);
                return;
            }

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            isAdmin = doc.RootElement.TryGetProperty("roleName", out var rn)
                      && rn.ValueKind == JsonValueKind.String
                      && rn.GetString() is not null;
        }
        catch (Exception ex)
        {
            ReplaceLastStep($"Failed: {ex.Message}", error: true);
            return;
        }

        if (!isAdmin)
        {
            ReplaceLastStep("Current user is not an admin. Log in with the admin account to run first-time setup.", error: true);
            return;
        }
        ReplaceLastStep("Admin permissions verified.", done: true);

        // ── Step 1: Providers ──
        AppendStep("Checking providers...");
        _providers = await FetchListAsync<ProviderDto>("/providers");
        if (_providers is { Count: > 0 })
        {
            ReplaceLastStep($"Found {_providers.Count} provider(s).", done: true);
        }
        else
        {
            ReplaceLastStep("No providers found. Please create one (you can add more later).");
            PopulateProviderTypeSelector();
            ProviderInputPanel.Visibility = Visibility.Visible;
            _providerTcs = new TaskCompletionSource<bool>();
            var created = await _providerTcs.Task;
            ProviderInputPanel.Visibility = Visibility.Collapsed;
            if (!created) return;
            _providers = await FetchListAsync<ProviderDto>("/providers");
            ReplaceLastStep("Provider created.", done: true);
        }

        // ── Steps 2–3: API keys & models (loop to allow switching back from local-only) ──
        List<ModelDto>? models = null;
        while (true)
        {
            _localOnly = false;
            _switchToCloud = false;

            // ── Step 2: Logged-in providers (has API key) ──
            AppendStep("Checking provider API keys...");
            var loggedIn = (_providers ?? []).Where(p => p.HasApiKey && !p.IsLocal).ToList();
            if (loggedIn.Count > 0)
            {
                ReplaceLastStep($"{loggedIn.Count} provider(s) have API keys.", done: true);
            }
            else
            {
                var remote = (_providers ?? []).Where(p => !p.IsLocal).ToList();
                if (remote.Count == 0)
                {
                    ReplaceLastStep("No remote providers available. Continuing with local models only.", done: true);
                    _localOnly = true;
                }
                else
                {
                    ReplaceLastStep("No provider has an API key set. Please provide one.");
                    PopulateApiKeyProviderSelector(remote);
                    ApiKeyInputPanel.Visibility = Visibility.Visible;
                    _apiKeyTcs = new TaskCompletionSource<bool>();
                    var keySet = await _apiKeyTcs.Task;
                    ApiKeyInputPanel.Visibility = Visibility.Collapsed;
                    if (!keySet)
                    {
                        _localOnly = true;
                        ReplaceLastStep("Continuing with local models only.", done: true);
                    }
                    else
                    {
                        _providers = await FetchListAsync<ProviderDto>("/providers");
                        ReplaceLastStep("API key configured.", done: true);
                    }
                }
            }

            // ── Step 3: Models ──
            AppendStep("Checking models...");
            models = await FetchListAsync<ModelDto>("/models");
            if (models is { Count: > 0 })
            {
                ReplaceLastStep($"Found {models.Count} model(s).", done: true);
                break;
            }

            if (_localOnly)
            {
                ReplaceLastStep("No models found. Download a local model to continue.");
                LocalModelDownloadPanel.Visibility = Visibility.Visible;
                _localModelTcs = new TaskCompletionSource<bool>();
                var downloaded = await _localModelTcs.Task;
                LocalModelDownloadPanel.Visibility = Visibility.Collapsed;

                if (!downloaded && _switchToCloud)
                {
                    ReplaceLastStep("Switching to cloud provider setup...", done: true);
                    continue;
                }

                if (!downloaded)
                {
                    ReplaceLastStep("No local model downloaded. Setup cannot continue.", error: true);
                    return;
                }

                models = await FetchListAsync<ModelDto>("/models");
                ReplaceLastStep($"Downloaded and registered {models?.Count ?? 0} model(s).", done: true);
                break;
            }
            else
            {
                ReplaceLastStep("No models found. Syncing from providers...");
                var synced = false;
                foreach (var p in _providers!.Where(p => p.HasApiKey))
                {
                    try
                    {
                        var resp = await Api.PostAsync($"/providers/{p.Id}/sync-models", null);
                        if (resp.IsSuccessStatusCode) synced = true;
                    }
                    catch { /* try next */ }
                }

                if (!synced)
                {
                    ReplaceLastStep("Model sync failed. Check your API key and try setup again.", error: true);
                    foreach (var p in _providers!.Where(p => p.HasApiKey))
                    {
                        try { await Api.PostAsync($"/providers/{p.Id}/set-key", new StringContent(JsonSerializer.Serialize(new { apiKey = "" }, Json), Encoding.UTF8, "application/json")); }
                        catch { /* best effort */ }
                    }
                    return;
                }

                models = await FetchListAsync<ModelDto>("/models");
                ReplaceLastStep($"Synced {models?.Count ?? 0} model(s).", done: true);
                break;
            }
        }

        // ── Step 4: Agents ──
        AppendStep("Checking agents...");
        var agents = await FetchListAsync<AgentDto>("/agents");
        if (agents is { Count: > 0 })
        {
            ReplaceLastStep($"Found {agents.Count} agent(s).", done: true);
        }
        else
        {
            ReplaceLastStep("No agents found. Creating default agents...");
            try
            {
                var resp = await Api.PostAsync("/agents/sync-with-models", null);
                if (resp.IsSuccessStatusCode)
                {
                    agents = await FetchListAsync<AgentDto>("/agents");
                    ReplaceLastStep($"Created {agents?.Count ?? 0} default agent(s).", done: true);
                }
                else
                {
                    ReplaceLastStep("Failed to create agents.", error: true);
                    return;
                }
            }
            catch (Exception ex)
            {
                ReplaceLastStep($"Failed: {ex.Message}", error: true);
                return;
            }
        }

        // ── Step 5: Default context + channel + thread ──
        AppendStep("Checking contexts and channels...");
        var contexts = await FetchListAsync<ContextDto>("/channel-contexts");
        var hasFullSetup = false;
        Guid? selectedAgentId = null;
        if (contexts is { Count: > 0 })
        {
            // Check if any context has a channel with a thread
            foreach (var ctx in contexts)
            {
                var channels = await FetchListAsync<ChannelDto>($"/channels?contextId={ctx.Id}");
                if (channels is not { Count: > 0 }) continue;
                foreach (var ch in channels)
                {
                    var threads = await FetchListAsync<ThreadDto>($"/channels/{ch.Id}/threads");
                    if (threads is { Count: > 0 })
                    {
                        hasFullSetup = true;
                        break;
                    }
                }
                if (hasFullSetup) break;
            }
        }

        if (hasFullSetup)
        {
            // Resolve the agent from the existing context so Step 6
            // can still check/assign its role.
            selectedAgentId = contexts!
                .Select(c => c.Agent?.Id)
                .FirstOrDefault(id => id.HasValue);

            // If contexts don't carry AgentId (older API), try the agent list.
            if (selectedAgentId is null)
            {
                agents ??= await FetchListAsync<AgentDto>("/agents") ?? [];
                selectedAgentId = agents.FirstOrDefault()?.Id;
            }

            ReplaceLastStep("Default context, channel, and thread exist.", done: true);
        }
        else
        {
            ReplaceLastStep("Creating default workspace...");

            agents ??= await FetchListAsync<AgentDto>("/agents") ?? [];
            ReplaceLastStep("Please select a default agent for the initial workspace.");
            PopulateAgentSelector(agents);
            AgentSelectorPanel.Visibility = Visibility.Visible;
            _agentTcs = new TaskCompletionSource<Guid?>();
            selectedAgentId = await _agentTcs.Task;
            AgentSelectorPanel.Visibility = Visibility.Collapsed;

            if (selectedAgentId is null)
            {
                ReplaceLastStep("No agent selected. Setup cannot continue.", error: true);
                return;
            }

            try
            {
                // Create context
                var ctxBody = JsonSerializer.Serialize(new { agentId = selectedAgentId, name = "Default" }, Json);
                var ctxResp = await Api.PostAsync("/channel-contexts", new StringContent(ctxBody, Encoding.UTF8, "application/json"));
                if (!ctxResp.IsSuccessStatusCode)
                {
                    ReplaceLastStep("Failed to create context.", error: true);
                    return;
                }
                using var ctxDoc = JsonDocument.Parse(await ctxResp.Content.ReadAsStringAsync());
                var ctxId = ctxDoc.RootElement.GetProperty("id").GetGuid();

                // Create channel
                var chBody = JsonSerializer.Serialize(new { title = "General", agentId = selectedAgentId, contextId = ctxId }, Json);
                var chResp = await Api.PostAsync("/channels", new StringContent(chBody, Encoding.UTF8, "application/json"));
                if (!chResp.IsSuccessStatusCode)
                {
                    ReplaceLastStep("Failed to create channel.", error: true);
                    return;
                }
                using var chDoc = JsonDocument.Parse(await chResp.Content.ReadAsStringAsync());
                var chId = chDoc.RootElement.GetProperty("id").GetGuid();

                // Create thread
                var thBody = JsonSerializer.Serialize(new { name = "Default" }, Json);
                var thResp = await Api.PostAsync($"/channels/{chId}/threads", new StringContent(thBody, Encoding.UTF8, "application/json"));
                if (!thResp.IsSuccessStatusCode)
                {
                    ReplaceLastStep("Failed to create thread.", error: true);
                    return;
                }

                ReplaceLastStep("Default workspace created.", done: true);
            }
            catch (Exception ex)
            {
                ReplaceLastStep($"Failed: {ex.Message}", error: true);
                return;
            }
        }

        // ── Step 6: Role & permissions for the selected agent ──
        if (selectedAgentId is not null)
        {
            AppendStep("Checking agent role...");
            var needsRole = true;

            try
            {
                var agentResp = await Api.GetAsync($"/agents/{selectedAgentId}");
                if (agentResp.IsSuccessStatusCode)
                {
                    using var agDoc = JsonDocument.Parse(await agentResp.Content.ReadAsStringAsync());
                    if (agDoc.RootElement.TryGetProperty("roleId", out var rp)
                        && rp.ValueKind == JsonValueKind.String)
                    {
                        needsRole = false;
                        ReplaceLastStep("Agent already has a role.", done: true);
                    }
                }
            }
            catch { /* continue to create role */ }

            if (needsRole)
            {
                while (true)
                {
                    ReplaceLastStep("Agent has no role. Set up permissions so it can use tools.");
                    await BuildPermissionsEditorAsync();
                    RolePermissionsPanel.Visibility = Visibility.Visible;
                    _roleTcs = new TaskCompletionSource<bool>();
                    var roleCreated = await _roleTcs.Task;
                    RolePermissionsPanel.Visibility = Visibility.Collapsed;

                    if (!roleCreated)
                    {
                        ReplaceLastStep("Role setup skipped. Agent has no permissions.", done: true);
                        break;
                    }

                    try
                    {
                        await CreateRoleAndAssignAsync(selectedAgentId.Value);
                        ReplaceLastStep("Role created and permissions applied.", done: true);
                        break;
                    }
                    catch (Exception ex)
                    {
                        ReplaceLastStep(ex.Message, error: true, copyText: ex.Message);
                        AppendStep("Retrying permissions setup...");
                    }
                }
            }
        }

        // ── Done ──
        AppendStep("Completed first-time setup!");
        await Task.Delay(1000);

        FirstSetupMarker.MarkCompleted();

        var navigator = App.Services!.GetRequiredService<INavigator>();
        await navigator.NavigateRouteAsync(this, "Main", qualifier: Qualifiers.ClearBackStack);
    }

    // ── Input callbacks ─────────────────────────────────────────

    // Known provider types that use device code auth instead of API keys
    private static readonly HashSet<string> DeviceCodeProviderTypes =
        ["GitHubCopilot", "11"];

    private bool IsDeviceCodeProvider(ProviderDto? provider)
    {
        if (provider is null) return false;
        var typeStr = provider.ProviderType.ToString();
        return DeviceCodeProviderTypes.Contains(typeStr);
    }

    private ProviderDto? GetSelectedApiKeyProvider()
    {
        if (ApiKeyProviderSelector.SelectedItem is not ComboBoxItem { Tag: Guid id }) return null;
        return (_providers ?? []).FirstOrDefault(p => p.Id == id);
    }

    private void OnApiKeyProviderSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var isDevice = IsDeviceCodeProvider(GetSelectedApiKeyProvider());
        ApiKeyFieldsPanel.Visibility = isDevice ? Visibility.Collapsed : Visibility.Visible;
        DeviceCodePanel.Visibility = isDevice ? Visibility.Visible : Visibility.Collapsed;
        DeviceCodeInfoPanel.Visibility = Visibility.Collapsed;
    }

    private async void OnProviderSubmitClick(object sender, RoutedEventArgs e)
    {
        var name = ProviderNameBox.Text?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            AppendStep("Provider name is required.", error: true);
            return;
        }

        if (ProviderTypeSelector.SelectedItem is not ComboBoxItem { Tag: string typeStr }) return;

        string? endpoint = null;
        if (typeStr == "Custom")
        {
            endpoint = EndpointBox.Text?.Trim();
            if (string.IsNullOrEmpty(endpoint))
            {
                AppendStep("API endpoint is required for Custom providers.", error: true);
                return;
            }
        }

        try
        {
            var body = JsonSerializer.Serialize(new { name, providerType = typeStr, apiEndpoint = endpoint }, Json);
            var resp = await Api.PostAsync("/providers", new StringContent(body, Encoding.UTF8, "application/json"));
            _providerTcs?.TrySetResult(resp.IsSuccessStatusCode);
            if (!resp.IsSuccessStatusCode)
                AppendStep($"Failed: {(int)resp.StatusCode} {resp.ReasonPhrase}", error: true);
        }
        catch (Exception ex)
        {
            AppendStep($"Failed: {ex.Message}", error: true);
            _providerTcs?.TrySetResult(false);
        }
    }

    private async void OnApiKeySubmitClick(object sender, RoutedEventArgs e)
    {
        var key = ApiKeyBox.Password?.Trim();
        if (string.IsNullOrEmpty(key))
        {
            AppendStep("API key is required.", error: true);
            return;
        }

        if (ApiKeyProviderSelector.SelectedItem is not ComboBoxItem { Tag: Guid providerId }) return;

        try
        {
            var body = JsonSerializer.Serialize(new { apiKey = key }, Json);
            var resp = await Api.PostAsync($"/providers/{providerId}/set-key",
                new StringContent(body, Encoding.UTF8, "application/json"));
            _apiKeyTcs?.TrySetResult(resp.IsSuccessStatusCode);
            if (!resp.IsSuccessStatusCode)
                AppendStep($"Failed: {(int)resp.StatusCode} {resp.ReasonPhrase}", error: true);
        }
        catch (Exception ex)
        {
            AppendStep($"Failed: {ex.Message}", error: true);
            _apiKeyTcs?.TrySetResult(false);
        }
    }

    private void OnLocalOnlyClick(object sender, RoutedEventArgs e)
    {
        _apiKeyTcs?.TrySetResult(false);
    }

    private void OnSwitchToCloudClick(object sender, RoutedEventArgs e)
    {
        _switchToCloud = true;
        _localModelTcs?.TrySetResult(false);
    }

    private async void OnListFilesClick(object sender, RoutedEventArgs e)
    {
        var url = HfUrlBox.Text?.Trim();
        if (string.IsNullOrEmpty(url))
        {
            AppendStep("URL is required.", error: true);
            return;
        }

        HfListFilesBtn.IsEnabled = false;
        HfStatusBlock.Text = "Fetching available files...";
        HfStatusBlock.Visibility = Visibility.Visible;

        try
        {
            var encodedUrl = Uri.EscapeDataString(url);
            var files = await FetchListAsync<GgufFileDto>($"/models/local/download/list?url={encodedUrl}");
            if (files is not { Count: > 0 })
            {
                HfStatusBlock.Text = "No GGUF files found at this URL.";
                HfListFilesBtn.IsEnabled = true;
                return;
            }

            HfFileSelector.Items.Clear();
            foreach (var f in files)
            {
                var label = f.Quantization is not null
                    ? $"{f.Filename}  ({f.Quantization})"
                    : f.Filename;
                HfFileSelector.Items.Add(new ComboBoxItem
                {
                    Content = label,
                    Tag = f.DownloadUrl,
                });
            }
            HfFileSelector.SelectedIndex = 0;
            HfFileSelectionPanel.Visibility = Visibility.Visible;
            HfStatusBlock.Text = $"{files.Count} file(s) available.";
        }
        catch (Exception ex)
        {
            HfStatusBlock.Text = $"Failed: {ex.Message}";
        }
        finally
        {
            HfListFilesBtn.IsEnabled = true;
        }
    }

    private async void OnDownloadModelClick(object sender, RoutedEventArgs e)
    {
        if (HfFileSelector.SelectedItem is not ComboBoxItem { Tag: string downloadUrl })
        {
            AppendStep("Please select a file.", error: true);
            return;
        }

        HfDownloadBtn.IsEnabled = false;
        HfListFilesBtn.IsEnabled = false;
        HfStatusBlock.Text = "Downloading model — this may take a while...";
        HfStatusBlock.Visibility = Visibility.Visible;

        try
        {
            var body = JsonSerializer.Serialize(new { url = downloadUrl }, Json);
            var resp = await Api.PostAsync("/models/local/download",
                new StringContent(body, Encoding.UTF8, "application/json"));

            if (resp.IsSuccessStatusCode)
            {
                HfStatusBlock.Text = "Download complete!";
                _localModelTcs?.TrySetResult(true);
            }
            else
            {
                var msg = await resp.Content.ReadAsStringAsync();
                HfStatusBlock.Text = $"Download failed: {(int)resp.StatusCode} — {msg}";
                HfDownloadBtn.IsEnabled = true;
                HfListFilesBtn.IsEnabled = true;
            }
        }
        catch (Exception ex)
        {
            HfStatusBlock.Text = $"Download failed: {ex.Message}";
            HfDownloadBtn.IsEnabled = true;
            HfListFilesBtn.IsEnabled = true;
        }
    }

    private async void OnDeviceCodeStartClick(object sender, RoutedEventArgs e)
    {
        if (ApiKeyProviderSelector.SelectedItem is not ComboBoxItem { Tag: Guid providerId }) return;

        DeviceCodeStartBtn.IsEnabled = false;
        DeviceCodeInfoPanel.Visibility = Visibility.Visible;
        DeviceCodeStatusBlock.Text = "Requesting device code...";

        try
        {
            // 1. Start the device code flow
            var startResp = await Api.PostAsync($"/providers/{providerId}/auth/device-code", null);
            if (!startResp.IsSuccessStatusCode)
            {
                AppendStep($"Failed to start device code flow: {(int)startResp.StatusCode}", error: true);
                DeviceCodeStartBtn.IsEnabled = true;
                return;
            }

            using var doc = JsonDocument.Parse(await startResp.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            var deviceCode = root.GetProperty("deviceCode").GetString()!;
            var userCode = root.GetProperty("userCode").GetString()!;
            var verificationUri = root.GetProperty("verificationUri").GetString()!;
            var expiresIn = root.GetProperty("expiresInSeconds").GetInt32();
            var interval = root.GetProperty("intervalSeconds").GetInt32();

            DeviceCodeValueBlock.Text = userCode;
            DeviceCodeStatusBlock.Text = $"Opening {verificationUri} — waiting for authorization...";

            // Open browser
            _ = Windows.System.Launcher.LaunchUriAsync(new Uri(verificationUri));

            // 2. Poll for completion (server blocks until user completes or timeout)
            var pollBody = JsonSerializer.Serialize(new
            {
                deviceCode,
                userCode,
                verificationUri,
                expiresInSeconds = expiresIn,
                intervalSeconds = interval
            }, Json);

            var pollResp = await Api.PostAsync($"/providers/{providerId}/auth/device-code/poll",
                new StringContent(pollBody, Encoding.UTF8, "application/json"));

            if (pollResp.IsSuccessStatusCode)
            {
                DeviceCodeStatusBlock.Text = "Authorized!";
                _apiKeyTcs?.TrySetResult(true);
            }
            else
            {
                DeviceCodeStatusBlock.Text = "Authorization expired or failed. Try again.";
                DeviceCodeStartBtn.IsEnabled = true;
            }
        }
        catch (Exception ex)
        {
            AppendStep($"Device code flow failed: {ex.Message}", error: true);
            DeviceCodeStartBtn.IsEnabled = true;
        }
    }

    private void OnAgentSubmitClick(object sender, RoutedEventArgs e)
    {
        if (DefaultAgentSelector.SelectedItem is ComboBoxItem { Tag: Guid agentId })
            _agentTcs?.TrySetResult(agentId);
        else
            AppendStep("Please select an agent.", error: true);
    }

    private async void OnSkipSetupClick(object sender, RoutedEventArgs e)
    {
        FirstSetupMarker.MarkCompleted();

        // Cancel any pending input steps
        _providerTcs?.TrySetResult(false);
        _apiKeyTcs?.TrySetResult(false);
        _agentTcs?.TrySetResult(null);
        _localModelTcs?.TrySetResult(false);
        _roleTcs?.TrySetResult(false);
        _upgradePromptTcs?.TrySetResult(false);

        AppendStep("Setup skipped. You can configure everything manually.", done: true);
        await Task.Delay(800);

        var navigator = App.Services!.GetRequiredService<INavigator>();
        await navigator.NavigateRouteAsync(this, "Main", qualifier: Qualifiers.ClearBackStack);
    }

    private void OnRoleSubmitClick(object sender, RoutedEventArgs e)
        => _roleTcs?.TrySetResult(true);

    private void OnRoleSkipClick(object sender, RoutedEventArgs e)
        => _roleTcs?.TrySetResult(false);

    private async Task BuildPermissionsEditorAsync()
    {
        // Populate per-flag clearance selectors (XAML-defined)
        PopulateClearanceCombo(ClrLocalhostBrowser);
        PopulateClearanceCombo(ClrLocalhostCli);
        PopulateClearanceCombo(ClrClickDesktop);
        PopulateClearanceCombo(ClrTypeOnDesktop);
        PopulateClearanceCombo(ClrCreateSubAgents);
        PopulateClearanceCombo(ClrCreateContainers);
        PopulateClearanceCombo(ClrRegisterInfoStores);

        // Build resource grants UI via shared builder
        _permEditor = new PermissionEditorBuilder(Api)
            .WithGrantClearance(true);
        ResourceGrantsContainer.Children.Clear();
        await _permEditor.BuildResourceGrantsAsync(ResourceGrantsContainer);
    }

    private static void PopulateClearanceCombo(ComboBox box)
    {
        box.Items.Clear();
        box.Items.Add(new ComboBoxItem { Content = "Unset", Tag = "Unset" });
        box.Items.Add(new ComboBoxItem { Content = "Can act without approval", Tag = "Independent" });
        box.Items.Add(new ComboBoxItem { Content = "Only with approval from a managing agent", Tag = "ApprovedByWhitelistedAgent" });
        box.Items.Add(new ComboBoxItem { Content = "Only with approval from an agent that has clearance to act", Tag = "ApprovedByPermittedAgent" });
        box.Items.Add(new ComboBoxItem { Content = "Only with approval from a user", Tag = "ApprovedByWhitelistedUser" });
        box.Items.Add(new ComboBoxItem { Content = "Only with approval from a user that can grant the permission", Tag = "ApprovedBySameLevelUser" });
        box.SelectedIndex = 1; // Default to Independent for first-time setup
    }

    private static string GetClearanceTag(ComboBox box)
        => box.SelectedItem is ComboBoxItem { Tag: string cl } ? cl : "Independent";

    private async Task CreateRoleAndAssignAsync(Guid agentId)
    {
        // 1. Create a new role via POST /roles
        var roleBody = JsonSerializer.Serialize(new { name = "Default Agent Role" }, Json);
        var roleResp = await Api.PostAsync("/roles",
            new StringContent(roleBody, Encoding.UTF8, "application/json"));

        if (!roleResp.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"Failed to create role ({(int)roleResp.StatusCode}). {await ExtractErrorAsync(roleResp)}");

        Guid roleId;
        using (var roleDoc = JsonDocument.Parse(await roleResp.Content.ReadAsStringAsync()))
            roleId = roleDoc.RootElement.GetProperty("id").GetGuid();

        // 2. Assign the role to the agent
        var assignBody = JsonSerializer.Serialize(new { roleId }, Json);
        var assignResp = await Api.PutAsync($"/agents/{agentId}/role",
            new StringContent(assignBody, Encoding.UTF8, "application/json"));

        if (!assignResp.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"Failed to assign role ({(int)assignResp.StatusCode}). {await ExtractErrorAsync(assignResp)}");

        // 3. Build the permission set from the UI
        var req = new Dictionary<string, object?>
        {
            // Global flags with per-flag clearance
            ["canCreateSubAgents"] = ChkCreateSubAgents.IsChecked == true,
            ["createSubAgentsClearance"] = GetClearanceTag(ClrCreateSubAgents),
            ["canCreateContainers"] = ChkCreateContainers.IsChecked == true,
            ["createContainersClearance"] = GetClearanceTag(ClrCreateContainers),
            ["canRegisterInfoStores"] = ChkRegisterInfoStores.IsChecked == true,
            ["registerInfoStoresClearance"] = GetClearanceTag(ClrRegisterInfoStores),
            ["canAccessLocalhostInBrowser"] = ChkLocalhostBrowser.IsChecked == true,
            ["accessLocalhostInBrowserClearance"] = GetClearanceTag(ClrLocalhostBrowser),
            ["canAccessLocalhostCli"] = ChkLocalhostCli.IsChecked == true,
            ["accessLocalhostCliClearance"] = GetClearanceTag(ClrLocalhostCli),
            ["canClickDesktop"] = ChkClickDesktop.IsChecked == true,
            ["clickDesktopClearance"] = GetClearanceTag(ClrClickDesktop),
            ["canTypeOnDesktop"] = ChkTypeOnDesktop.IsChecked == true,
            ["typeOnDesktopClearance"] = GetClearanceTag(ClrTypeOnDesktop),
        };

        // Per-resource grants from shared builder
        if (_permEditor is not null)
            foreach (var (k, v) in _permEditor.CollectGrants())
                req[k] = v;

        var permBody = JsonSerializer.Serialize(req, Json);

        var permResp = await Api.PutAsync($"/roles/{roleId}/permissions",
            new StringContent(permBody, Encoding.UTF8, "application/json"));

        if (!permResp.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"Failed to set permissions ({(int)permResp.StatusCode}). {await ExtractErrorAsync(permResp)}");
    }

    private static async Task<string> ExtractErrorAsync(HttpResponseMessage resp)
    {
        var raw = await resp.Content.ReadAsStringAsync();
        if (string.IsNullOrWhiteSpace(raw))
            return resp.ReasonPhrase ?? "Unknown error";

        try
        {
            using var doc = JsonDocument.Parse(raw);
            // RFC 7807 problem detail
            if (doc.RootElement.TryGetProperty("detail", out var detail))
                return detail.GetString() ?? raw;
            if (doc.RootElement.TryGetProperty("title", out var title))
                return title.GetString() ?? raw;
            if (doc.RootElement.TryGetProperty("message", out var msg))
                return msg.GetString() ?? raw;
        }
        catch { /* not JSON — use raw text */ }

        // Truncate overly long responses
        return raw.Length > 200 ? raw[..200] + "..." : raw;
    }

    // ── Populate helpers ────────────────────────────────────────

    private void PopulateProviderTypeSelector()
    {
        string[] types = ["OpenAI", "Anthropic", "OpenRouter", "GoogleGemini", "GoogleVertexAI",
            "ZAI", "VercelAIGateway", "XAI", "Groq", "Cerebras", "Mistral", "GitHubCopilot", "Minimax", "Custom"];
        foreach (var t in types)
        {
            var item = new ComboBoxItem { Content = t, Tag = t };
            ProviderTypeSelector.Items.Add(item);
        }
        ProviderTypeSelector.SelectedIndex = 0;
        ProviderTypeSelector.SelectionChanged += (_, _) =>
        {
            var isCustom = ProviderTypeSelector.SelectedItem is ComboBoxItem { Tag: "Custom" };
            EndpointPanel.Visibility = isCustom ? Visibility.Visible : Visibility.Collapsed;
        };
    }

    private void PopulateApiKeyProviderSelector(List<ProviderDto> providers)
    {
        ApiKeyProviderSelector.Items.Clear();
        foreach (var p in providers)
        {
            ApiKeyProviderSelector.Items.Add(new ComboBoxItem
            {
                Content = p.Name,
                Tag = p.Id,
            });
        }
        if (providers.Count == 1)
            ApiKeyProviderSelector.SelectedIndex = 0;

        ApiKeyProviderLabel.Text = providers.Count == 1
            ? $"Provider: {providers[0].Name}"
            : "Select a provider:";
    }

    private void PopulateAgentSelector(List<AgentDto> agents)
    {
        DefaultAgentSelector.Items.Clear();
        foreach (var a in agents)
        {
            DefaultAgentSelector.Items.Add(new ComboBoxItem
            {
                Content = $"{a.Name}  ({a.ProviderName}/{a.ModelName})",
                Tag = a.Id,
            });
        }
        if (agents.Count > 0)
            DefaultAgentSelector.SelectedIndex = 0;
    }

    // ── Utilities ────────────────────────────────────────────────

    private void ReplaceLastStep(string text, bool done = false, bool error = false, string? copyText = null)
    {
        // Remove the last step line (the one before the Cursor)
        var idx = StepsPanel.Children.Count - 2; // -1 is Cursor, -2 is last step
        if (idx >= 0)
            StepsPanel.Children.RemoveAt(idx);
        AppendStep(text, done, error, copyText);
    }

    private Task<List<T>?> FetchListAsync<T>(string path) => Api.FetchListAsync<T>(path, Json);

    private static SolidColorBrush B(int rgb) => TerminalUI.Brush(rgb);

    // ── Upgrade-prompt callbacks ────────────────────────────────

    private void OnUpgradeRedoClick(object sender, RoutedEventArgs e)
        => _upgradePromptTcs?.TrySetResult(true);

    private void OnUpgradeSkipClick(object sender, RoutedEventArgs e)
        => _upgradePromptTcs?.TrySetResult(false);

    // ── DTOs ────────────────────────────────────────────────────
    // ProviderType may arrive as a string ("OpenAI") or an integer (0)
    // depending on whether the API has the JsonStringEnumConverter.
    // Using JsonElement makes deserialization resilient to either form.

    [ImplicitKeys(IsEnabled = false)]
    private sealed partial record ProviderDto(Guid Id, string Name, JsonElement ProviderType, string? ApiEndpoint, bool HasApiKey)
    {
        public bool IsLocal => ProviderType.ToString() is "Local" or "13";
    }
    [ImplicitKeys(IsEnabled = false)]
    private sealed partial record ModelDto(Guid Id, string Name, JsonElement Capabilities, Guid ProviderId, string ProviderName);
    [ImplicitKeys(IsEnabled = false)]
    private sealed partial record AgentDto(Guid Id, string Name, Guid ModelId, string ModelName, string ProviderName);
    [ImplicitKeys(IsEnabled = false)]
    private sealed partial record ContextDto(Guid Id, string Name, ContextAgentDto? Agent = null);
    [ImplicitKeys(IsEnabled = false)]
    private sealed partial record ContextAgentDto(Guid Id, string? Name = null);
    [ImplicitKeys(IsEnabled = false)]
    private sealed partial record ChannelDto(Guid Id, string Title, Guid? ContextId);
    [ImplicitKeys(IsEnabled = false)]
    private sealed partial record ThreadDto(Guid Id, string Name, Guid ChannelId);
    [ImplicitKeys(IsEnabled = false)]
    private sealed partial record GgufFileDto(string DownloadUrl, string Filename, string? Quantization);
}
