using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.UI.Xaml.Media;
using SharpClaw.Services;

namespace SharpClaw.Presentation;

public sealed partial class FirstSetupPage : Page
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private SharpClawApiClient Api => App.Services!.GetRequiredService<SharpClawApiClient>();

    // State used across async steps
    private TaskCompletionSource<bool>? _providerTcs;
    private TaskCompletionSource<bool>? _apiKeyTcs;
    private TaskCompletionSource<Guid?>? _agentTcs;
    private bool _localOnly;
    private List<ProviderDto>? _providers;

    public FirstSetupPage()
    {
        this.InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Cursor.SetCommand("sharpclaw setup ");
        await RunSetupAsync();
    }

    // ── Step rendering ──────────────────────────────────────────

    private void AppendStep(string text, bool done = false, bool error = false)
    {
        var icon = done ? "✓" : error ? "✗" : "›";
        var color = done ? 0x00FF00 : error ? 0xFF4444 : 0x808080;

        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        panel.Children.Add(new TextBlock
        {
            Text = icon,
            FontFamily = new FontFamily("Consolas, Courier New, monospace"),
            FontSize = 13,
            Foreground = new SolidColorBrush(ColorFrom(color)),
        });
        panel.Children.Add(new TextBlock
        {
            Text = text,
            FontFamily = new FontFamily("Consolas, Courier New, monospace"),
            FontSize = 13,
            Foreground = new SolidColorBrush(ColorFrom(done ? 0xCCCCCC : error ? 0xFF4444 : 0x808080)),
            TextWrapping = TextWrapping.Wrap,
        });

        // Insert before the Cursor (always last child)
        var idx = StepsPanel.Children.Count - 1;
        StepsPanel.Children.Insert(idx < 0 ? 0 : idx, panel);
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
        var models = await FetchListAsync<ModelDto>("/models");
        if (models is { Count: > 0 })
        {
            ReplaceLastStep($"Found {models.Count} model(s).", done: true);
        }
        else
        {
            if (_localOnly)
            {
                ReplaceLastStep("No models available (local only). Setup cannot continue.", error: true);
                return;
            }

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
                // Wipe API keys on failed sync
                foreach (var p in _providers!.Where(p => p.HasApiKey))
                {
                    try { await Api.PostAsync($"/providers/{p.Id}/set-key", new StringContent(JsonSerializer.Serialize(new { apiKey = "" }, Json), Encoding.UTF8, "application/json")); }
                    catch { /* best effort */ }
                }
                return;
            }

            models = await FetchListAsync<ModelDto>("/models");
            ReplaceLastStep($"Synced {models?.Count ?? 0} model(s).", done: true);
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
            ReplaceLastStep("Default context, channel, and thread exist.", done: true);
        }
        else
        {
            ReplaceLastStep("Creating default workspace...");

            // Try to find GPT 5.4 agent (non-pro)
            agents ??= await FetchListAsync<AgentDto>("/agents") ?? [];
            var gpt54 = agents.FirstOrDefault(a =>
                a.ModelName.Contains("gpt-5.4", StringComparison.OrdinalIgnoreCase)
                && !a.ModelName.Contains("pro", StringComparison.OrdinalIgnoreCase));

            Guid? selectedAgentId = gpt54?.Id;

            if (selectedAgentId is null)
            {
                // User must select
                ReplaceLastStep("GPT 5.4 not available. Please select a default agent.");
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

        AppendStep("Setup skipped. You can configure everything manually.", done: true);
        await Task.Delay(800);

        var navigator = App.Services!.GetRequiredService<INavigator>();
        await navigator.NavigateRouteAsync(this, "Main", qualifier: Qualifiers.ClearBackStack);
    }

    // ── Populate helpers ────────────────────────────────────────

    private void PopulateProviderTypeSelector()
    {
        string[] types = ["OpenAI", "Anthropic", "OpenRouter", "GoogleGemini", "GoogleVertexAI",
            "ZAI", "VercelAIGateway", "XAI", "Groq", "Cerebras", "Mistral", "GitHubCopilot", "Custom"];
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

    private void ReplaceLastStep(string text, bool done = false, bool error = false)
    {
        // Remove the last step line (the one before the Cursor)
        var idx = StepsPanel.Children.Count - 2; // -1 is Cursor, -2 is last step
        if (idx >= 0)
            StepsPanel.Children.RemoveAt(idx);
        AppendStep(text, done, error);
    }

    private async Task<List<T>?> FetchListAsync<T>(string path)
    {
        try
        {
            var resp = await Api.GetAsync(path);
            if (!resp.IsSuccessStatusCode) return null;
            return JsonSerializer.Deserialize<List<T>>(
                await resp.Content.ReadAsStringAsync(), Json);
        }
        catch { return null; }
    }

    private static Windows.UI.Color ColorFrom(int rgb)
        => Windows.UI.Color.FromArgb(255, (byte)((rgb >> 16) & 0xFF), (byte)((rgb >> 8) & 0xFF), (byte)(rgb & 0xFF));

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
    private sealed partial record ContextDto(Guid Id, string Name);
    [ImplicitKeys(IsEnabled = false)]
    private sealed partial record ChannelDto(Guid Id, string Title, Guid? ContextId);
    [ImplicitKeys(IsEnabled = false)]
    private sealed partial record ThreadDto(Guid Id, string Name, Guid ChannelId);
}
