using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
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
    private TaskCompletionSource<bool>? _localModelTcs;
    private TaskCompletionSource<bool>? _upgradePromptTcs;
    private bool _localOnly;
    private bool _switchToCloud;
    private List<ProviderDto>? _providers;
    private List<ProviderTypeDto> _providerTypes = [];

    // ── Module wizard state ──
    public FirstSetupPage()
    {
        this.InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        var setupMarker = App.Services!.GetRequiredService<FirstSetupMarker>();

        if (setupMarker.NeedsUpgradeRerun)
        {
            // Major version advanced since last setup — ask whether to redo
            var oldVer = setupMarker.CompletedMajorVersion;
            var newVer = setupMarker.CurrentMajorVersion;
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
                await setupMarker.MarkCompletedAsync();
                await App.Services!.GetRequiredService<ClientNavigationService>()
                    .NavigateRouteAsync(this, "Main", Qualifiers.ClearBackStack);
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
        _providerTypes = await FetchListAsync<ProviderTypeDto>("/providers/types") ?? [];
        if (_providers is { Count: > 0 })
        {
            ReplaceLastStep($"Found {_providers.Count} provider(s).", done: true);
        }
        else
        {
            ReplaceLastStep("No providers found. Please create one (you can add more later).");
            await PopulateProviderTypeSelectorAsync();
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

        AppendStep("Module-owned setup is handled by installed modules.", done: true);

        // ── Done ──
        AppendStep("Completed first-time setup!");
        await Task.Delay(1000);

        await App.Services!.GetRequiredService<FirstSetupMarker>().MarkCompletedAsync();

        await App.Services!.GetRequiredService<ClientNavigationService>()
            .NavigateRouteAsync(this, "Main", Qualifiers.ClearBackStack);
    }

    // ── Input callbacks ─────────────────────────────────────────

    private bool IsDeviceCodeProvider(ProviderDto? provider)
    {
        if (provider is null) return false;
        return _providerTypes.Any(t =>
            t.ProviderKey.Equals(provider.ProviderKey, StringComparison.OrdinalIgnoreCase)
            && t.SupportsDeviceCodeAuth);
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

        if (ProviderTypeSelector.SelectedItem is not ComboBoxItem { Tag: ProviderTypeDto type }) return;

        string? endpoint = null;
        if (type.RequiresEndpoint || type.SupportsAutomaticEndpointDiscovery)
        {
            endpoint = EndpointBox.Text?.Trim();
            if (string.IsNullOrEmpty(endpoint))
            {
                endpoint = type.ProviderKey.Equals("ollama", StringComparison.OrdinalIgnoreCase)
                    ? "http://localhost:11434"
                    : null;
            }
            if (string.IsNullOrEmpty(endpoint) && type.RequiresEndpoint)
            {
                AppendStep($"API endpoint is required for {type.DisplayName}.", error: true);
                return;
            }
        }

        if (type.ProviderKey.Equals("ollama", StringComparison.OrdinalIgnoreCase))
        {
            AppendStep("Checking Ollama connection…");
            try
            {
                var actions = App.Services!.GetRequiredService<ClientActionDispatcher>();
                var statusCode = await actions.RunCommandAsync(
                    "client.provider.ollama.probe",
                    async token =>
                    {
                        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                        using var check = await http.GetAsync(
                            endpoint!.TrimEnd('/') + "/api/tags",
                            token);
                        return (int)check.StatusCode;
                    });
                if (statusCode is < 200 or >= 300)
                {
                    ReplaceLastStep($"Ollama unreachable ({statusCode}). Check the endpoint and try again.", error: true);
                    return;
                }
                ReplaceLastStep("Ollama connection OK.", done: true);
            }
            catch (Exception ex)
            {
                ReplaceLastStep($"Ollama unreachable: {ex.Message}", error: true);
                return;
            }
        }

        try
        {
            if (string.IsNullOrWhiteSpace(endpoint)) endpoint = null;
            var body = JsonSerializer.Serialize(new { name, providerKey = type.ProviderKey, apiEndpoint = endpoint }, Json);
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
            var body = JsonSerializer.Serialize(new { url = downloadUrl, providerKey = "llamasharp" }, Json);
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

    private async void OnSkipSetupClick(object sender, RoutedEventArgs e)
    {
        await App.Services!.GetRequiredService<FirstSetupMarker>().MarkCompletedAsync();

        // Cancel any pending input steps
        _providerTcs?.TrySetResult(false);
        _apiKeyTcs?.TrySetResult(false);
        _localModelTcs?.TrySetResult(false);
        _upgradePromptTcs?.TrySetResult(false);

        AppendStep("Setup skipped. You can configure everything manually.", done: true);
        await Task.Delay(800);

        await App.Services!.GetRequiredService<ClientNavigationService>()
            .NavigateRouteAsync(this, "Main", Qualifiers.ClearBackStack);
    }

    // ── Module wizard ───────────────────────────────────────────

    // ── Populate helpers ────────────────────────────────────────

    private async Task PopulateProviderTypeSelectorAsync()
    {
        _providerTypes = await FetchListAsync<ProviderTypeDto>("/providers/types") ?? [];
        ProviderTypeSelector.Items.Clear();
        foreach (var t in _providerTypes)
        {
            var item = new ComboBoxItem { Content = $"{t.DisplayName} ({t.ProviderKey})", Tag = t };
            ProviderTypeSelector.Items.Add(item);
        }
        if (ProviderTypeSelector.Items.Count > 0)
            ProviderTypeSelector.SelectedIndex = 0;
        ProviderTypeSelector.SelectionChanged += (_, _) =>
        {
            var type = (ProviderTypeSelector.SelectedItem as ComboBoxItem)?.Tag as ProviderTypeDto;
            var needsEndpoint = type is { RequiresEndpoint: true } or { SupportsAutomaticEndpointDiscovery: true };
            EndpointPanel.Visibility = needsEndpoint ? Visibility.Visible : Visibility.Collapsed;
            if (type?.ProviderKey.Equals("ollama", StringComparison.OrdinalIgnoreCase) == true
                && string.IsNullOrEmpty(EndpointBox.Text))
            {
                EndpointBox.Text = "http://localhost:11434";
            }
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

    // ── Upgrade-prompt callbacks ────────────────────────────────

    private void OnUpgradeRedoClick(object sender, RoutedEventArgs e)
        => _upgradePromptTcs?.TrySetResult(true);

    private void OnUpgradeSkipClick(object sender, RoutedEventArgs e)
        => _upgradePromptTcs?.TrySetResult(false);

    // ── DTOs ────────────────────────────────────────────────────
    [ImplicitKeys(IsEnabled = false)]
    private sealed partial record ProviderDto(Guid Id, string Name, string ProviderKey, string? ApiEndpoint, bool HasApiKey)
    {
        public bool IsLocal => ProviderKey.Equals("llamasharp", StringComparison.OrdinalIgnoreCase);
    }
    [ImplicitKeys(IsEnabled = false)]
    private sealed partial record ProviderTypeDto(
        string ProviderKey,
        string DisplayName,
        bool RequiresEndpoint,
        bool SupportsAutomaticEndpointDiscovery,
        bool RequiresApiKey,
        bool SupportsDeviceCodeAuth);
    [ImplicitKeys(IsEnabled = false)]
    private sealed partial record ModelDto(Guid Id, string Name, JsonElement Capabilities, Guid ProviderId, string ProviderName);
    [ImplicitKeys(IsEnabled = false)]
    private sealed partial record GgufFileDto(string DownloadUrl, string Filename, string? Quantization);
}
