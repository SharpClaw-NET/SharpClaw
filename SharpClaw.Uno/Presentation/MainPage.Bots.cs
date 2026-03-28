using System.Text;
using System.Text.Json;
using SharpClaw.Helpers;
using SharpClaw.Services;

namespace SharpClaw.Presentation;

// Bots tab: list bots, assign / remove from the active channel.
public sealed partial class MainPage
{
    // ── Bots ─────────────────────────────────────────────────────

    private void OnTabBotsClick(object sender, RoutedEventArgs e)
    {
        if (_botsMode || _selectedChannelId is null) return;
        _botsMode = true;
        _settingsMode = false;
        _tasksMode = false;
        _jobsMode = false;
        _taskCreateNewMode = false;
        UpdateTabHighlight();

        MessagesScroller.Visibility = Visibility.Collapsed;
        ChatInputArea.Visibility = Visibility.Collapsed;
        SettingsScroller.Visibility = Visibility.Collapsed;
        TaskViewPanel.Visibility = Visibility.Collapsed;
        DeallocateTaskView();
        JobViewPanel.Visibility = Visibility.Collapsed;
        DeallocateJobView();
        AgentSelectorPanel.Visibility = Visibility.Collapsed;
        ThreadSelectorPanel.Visibility = Visibility.Collapsed;
        OneOffWarning.Visibility = Visibility.Collapsed;

        BotViewPanel.Visibility = Visibility.Visible;

        _ = LoadChannelBotsAsync();
    }

    private async Task LoadChannelBotsAsync()
    {
        BotContentPanel.Children.Clear();

        var channelId = _selectedChannelId;
        if (channelId is null) return;

        // Header
        BotContentPanel.Children.Add(new TextBlock
        {
            Text = "bot integrations",
            FontFamily = _monoFont,
            FontSize = 13,
            Foreground = Brush(0x00FF00),
            Margin = new Thickness(0, 0, 0, 4),
        });

        BotContentPanel.Children.Add(new TextBlock
        {
            Text = "assign a bot to this channel so it forwards messages here.",
            FontFamily = _monoFont,
            FontSize = 11,
            Foreground = Brush(0x666666),
            Margin = new Thickness(0, 0, 0, 8),
        });

        // Fetch bots from core API
        List<BotEntryDto>? bots;
        var api = App.Services!.GetRequiredService<SharpClawApiClient>();
        try
        {
            using var resp = await api.GetAsync("/bots");
            if (!resp.IsSuccessStatusCode)
            {
                BotContentPanel.Children.Add(new TextBlock
                {
                    Text = $"failed to load bots: {(int)resp.StatusCode} {resp.ReasonPhrase}",
                    FontFamily = _monoFont,
                    FontSize = 11,
                    Foreground = Brush(0xFF4444),
                });
                return;
            }

            using var stream = await resp.Content.ReadAsStreamAsync();
            bots = await JsonSerializer.DeserializeAsync<List<BotEntryDto>>(stream, Json);
        }
        catch (Exception ex)
        {
            BotContentPanel.Children.Add(new TextBlock
            {
                Text = $"error: {ex.Message}",
                FontFamily = _monoFont,
                FontSize = 11,
                Foreground = Brush(0xFF4444),
            });
            return;
        }

        if (bots is null or { Count: 0 })
        {
            BotContentPanel.Children.Add(new TextBlock
            {
                Text = "no bots registered. create one in settings → bot integrations.",
                FontFamily = _monoFont,
                FontSize = 11,
                Foreground = Brush(0x888888),
            });
            return;
        }

        // Split into assigned-to-this-channel and others
        var assigned = new List<BotEntryDto>();
        var available = new List<BotEntryDto>();
        foreach (var bot in bots)
        {
            if (bot.DefaultChannelId == channelId)
                assigned.Add(bot);
            else
                available.Add(bot);
        }

        // ── Assigned bots ──
        BotContentPanel.Children.Add(new TextBlock
        {
            Text = $"── assigned to this channel ({assigned.Count}) ──",
            FontFamily = _monoFont,
            FontSize = 11,
            Foreground = Brush(0x00AAFF),
            Margin = new Thickness(0, 4, 0, 4),
        });

        if (assigned.Count == 0)
        {
            BotContentPanel.Children.Add(new TextBlock
            {
                Text = "(none)",
                FontFamily = _monoFont,
                FontSize = 11,
                Foreground = Brush(0x555555),
                Margin = new Thickness(8, 0, 0, 0),
            });
        }
        else
        {
            foreach (var bot in assigned)
                BotContentPanel.Children.Add(BuildBotRow(bot, isAssigned: true, channelId.Value));
        }

        // ── Available bots ──
        BotContentPanel.Children.Add(new TextBlock
        {
            Text = $"── available ({available.Count}) ──",
            FontFamily = _monoFont,
            FontSize = 11,
            Foreground = Brush(0x00AAFF),
            Margin = new Thickness(0, 8, 0, 4),
        });

        if (available.Count == 0)
        {
            BotContentPanel.Children.Add(new TextBlock
            {
                Text = "(all bots are assigned to this channel)",
                FontFamily = _monoFont,
                FontSize = 11,
                Foreground = Brush(0x555555),
                Margin = new Thickness(8, 0, 0, 0),
            });
        }
        else
        {
            foreach (var bot in available)
                BotContentPanel.Children.Add(BuildBotRow(bot, isAssigned: false, channelId.Value));
        }
    }

    private StackPanel BuildBotRow(BotEntryDto bot, bool isAssigned, Guid channelId)
    {
        var wrapper = new StackPanel
        {
            Spacing = 0,
            Background = Brush(0x1A1A1A),
            CornerRadius = new CornerRadius(4),
            Margin = new Thickness(0, 2, 0, 2),
            Padding = new Thickness(8, 6, 8, 6),
        };

        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // Info column
        var info = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };

        var nameLine = new TextBlock
        {
            FontFamily = _monoFont,
            FontSize = 12,
            Foreground = Brush(0xCCCCCC),
        };
        nameLine.Text = bot.Name;
        info.Children.Add(nameLine);

        var statusText = bot.Enabled ? "● enabled" : "○ disabled";
        var tokenText = bot.HasBotToken ? "🔑 token set" : "⚠ no token";
        var threadText = bot.DefaultThreadId is not null
            ? $"🧵 thread {bot.DefaultThreadId.Value.ToString()[..8]}…"
            : "";

        var detailLine = new TextBlock
        {
            FontFamily = _monoFont,
            FontSize = 10,
            Foreground = Brush(0x888888),
        };
        detailLine.Text = string.IsNullOrEmpty(threadText)
            ? $"{bot.BotType}   {statusText}   {tokenText}"
            : $"{bot.BotType}   {statusText}   {tokenText}   {threadText}";
        info.Children.Add(detailLine);

        var isAssignedElsewhere = bot.DefaultChannelId is not null && !isAssigned;
        if (isAssignedElsewhere)
        {
            info.Children.Add(new TextBlock
            {
                Text = "⚠ currently assigned to another channel — reassigning will move it here",
                FontFamily = _monoFont,
                FontSize = 10,
                Foreground = Brush(0xFFAA00),
            });
        }

        Grid.SetColumn(info, 0);
        row.Children.Add(info);

        // Action button
        var assignLabel = isAssignedElsewhere ? "reassign" : "assign";
        var btn = new Button
        {
            Content = new TextBlock
            {
                Text = isAssigned ? "remove" : assignLabel,
                FontFamily = _monoFont,
                FontSize = 11,
                Foreground = Brush(isAssigned ? 0xFF4444 : 0x00FF00),
            },
            Background = _brushTransparent,
            BorderBrush = Brush(isAssigned ? 0xFF4444 : 0x00FF00),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 4),
            VerticalAlignment = VerticalAlignment.Center,
        };

        var botId = bot.Id;
        btn.Click += async (_, _) =>
        {
            btn.IsEnabled = false;
            await UpdateBotChannelAsync(botId, isAssigned ? Guid.Empty : channelId);
            await LoadChannelBotsAsync();
        };

        Grid.SetColumn(btn, 1);
        row.Children.Add(btn);
        wrapper.Children.Add(row);

        // ── Thread selector (only for bots assigned to this channel) ──
        if (isAssigned)
        {
            var threadRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Margin = new Thickness(0, 6, 0, 0),
            };

            threadRow.Children.Add(new TextBlock
            {
                Text = "thread:",
                FontFamily = _monoFont,
                FontSize = 10,
                Foreground = Brush(0x888888),
                VerticalAlignment = VerticalAlignment.Center,
            });

            var threadCombo = new ComboBox
            {
                FontFamily = _monoFont,
                FontSize = 10,
                MinWidth = 200,
                Background = Brush(0x111111),
                BorderBrush = Brush(0x333333),
                BorderThickness = new Thickness(1),
                VerticalAlignment = VerticalAlignment.Center,
            };

            var currentThreadId = bot.DefaultThreadId;
            _ = PopulateBotThreadComboAsync(threadCombo, botId, channelId, currentThreadId);

            threadRow.Children.Add(threadCombo);
            wrapper.Children.Add(threadRow);
        }

        return wrapper;
    }

    /// <summary>
    /// Loads threads for the channel and populates the combo. When the user
    /// selects a different thread the bot is updated immediately.
    /// </summary>
    private async Task PopulateBotThreadComboAsync(
        ComboBox combo, Guid botId, Guid channelId, Guid? currentThreadId)
    {
        List<BotThreadDto>? threads = null;
        var api = App.Services!.GetRequiredService<SharpClawApiClient>();
        try
        {
            using var resp = await api.GetAsync($"/channels/{channelId}/threads");
            if (resp.IsSuccessStatusCode)
            {
                using var stream = await resp.Content.ReadAsStreamAsync();
                threads = await JsonSerializer.DeserializeAsync<List<BotThreadDto>>(stream, Json);
            }
        }
        catch { /* swallow — combo stays empty */ }

        if (threads is null or { Count: 0 })
        {
            combo.Items.Add(new ComboBoxItem
            {
                Content = "(no threads — one will be created on assign)",
                Tag = (Guid?)null,
            });
            combo.SelectedIndex = 0;
            combo.IsEnabled = false;
            return;
        }

        int selectedIndex = -1;
        for (var i = 0; i < threads.Count; i++)
        {
            var t = threads[i];
            var item = new ComboBoxItem
            {
                Content = $"{t.Name}  ({t.Id.ToString()[..8]}…)",
                Tag = (Guid?)t.Id,
            };
            combo.Items.Add(item);
            if (t.Id == currentThreadId)
                selectedIndex = i;
        }

        // If the current thread wasn't found (e.g. deleted), fall back to first
        combo.SelectedIndex = selectedIndex >= 0 ? selectedIndex : 0;

        combo.SelectionChanged += async (_, _) =>
        {
            if (combo.SelectedItem is not ComboBoxItem { Tag: Guid newThreadId })
                return;
            if (newThreadId == currentThreadId) return;

            combo.IsEnabled = false;
            await UpdateBotThreadAsync(botId, newThreadId);
            combo.IsEnabled = true;
        };
    }

    /// <summary>
    /// Sends a PUT to set or clear the bot's default channel, then signals
    /// the gateway to reload bot services.
    /// </summary>
    private async Task UpdateBotChannelAsync(Guid botId, Guid channelIdOrEmpty)
    {
        try
        {
            var api = App.Services!.GetRequiredService<SharpClawApiClient>();
            var payload = new { defaultChannelId = channelIdOrEmpty };
            var body = new StringContent(
                JsonSerializer.Serialize(payload),
                Encoding.UTF8, "application/json");
            using var resp = await api.PutAsync($"/bots/{botId}", body);
            if (!resp.IsSuccessStatusCode)
            {
                var err = await resp.Content.ReadAsStringAsync();
                AppendBotStatus($"failed: {err}", 0xFF4444);
                return;
            }
        }
        catch (Exception ex)
        {
            AppendBotStatus($"error: {ex.Message}", 0xFF4444);
            return;
        }

        await SignalGatewayBotReloadAsync();
    }

    /// <summary>
    /// Sends a PUT to change the bot's default thread, then signals
    /// the gateway to reload bot services.
    /// </summary>
    private async Task UpdateBotThreadAsync(Guid botId, Guid threadId)
    {
        try
        {
            var api = App.Services!.GetRequiredService<SharpClawApiClient>();
            var payload = new { defaultThreadId = threadId };
            var body = new StringContent(
                JsonSerializer.Serialize(payload),
                Encoding.UTF8, "application/json");
            using var resp = await api.PutAsync($"/bots/{botId}", body);
            if (!resp.IsSuccessStatusCode)
            {
                var err = await resp.Content.ReadAsStringAsync();
                AppendBotStatus($"failed to update thread: {err}", 0xFF4444);
                return;
            }
        }
        catch (Exception ex)
        {
            AppendBotStatus($"error: {ex.Message}", 0xFF4444);
            return;
        }

        await SignalGatewayBotReloadAsync();
    }

    /// <summary>
    /// Pings the gateway's reload endpoint so running bot services
    /// (Telegram, Discord) re-fetch their configuration.
    /// </summary>
    private static async Task SignalGatewayBotReloadAsync()
    {
        try
        {
            var gw = App.Services?.GetService<GatewayProcessManager>();
            if (gw is null) return;
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            using var empty = new StringContent("{}", Encoding.UTF8, "application/json");
            await http.PostAsync($"{gw.ClientUrl}/api/bots/reload", empty);
        }
        catch
        {
            // Gateway may be offline — reload will happen on next gateway restart.
        }
    }

    private void AppendBotStatus(string message, int color)
    {
        BotContentPanel.Children.Add(new TextBlock
        {
            Text = message,
            FontFamily = _monoFont,
            FontSize = 11,
            Foreground = Brush(color),
            Margin = new Thickness(8, 2, 0, 2),
        });
    }

    // ── DTOs ──────────────────────────────────────────────────────

    [ImplicitKeys(IsEnabled = false)]
    private sealed record BotEntryDto(
        Guid Id, string Name, string BotType,
        bool Enabled, bool HasBotToken,
        Guid? DefaultChannelId = null,
        Guid? DefaultThreadId = null);

    [ImplicitKeys(IsEnabled = false)]
    private sealed record BotThreadDto(Guid Id, string Name, Guid ChannelId);
}
