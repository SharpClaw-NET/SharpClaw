using System.Buffers;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.UI;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using SharpClaw.Services;

namespace SharpClaw.Presentation;

public sealed partial class MainPage : Page
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private Guid? _selectedChannelId;
    private Guid? _selectedThreadId;
    private Guid? _selectedAgentId;
    private Guid? _selectedJobId;
    private bool _pendingNewThread;
    private bool _settingsMode;
    private bool _suppressThreadSelection;
    private bool _suppressJobSelection;
    private readonly Dictionary<Guid, bool> _expandedContexts = [];
    private List<AgentDto> _allAgents = [];
    private List<JobDto> _channelJobs = [];
    private List<RoleDto> _allRoles = [];
    private string _currentUsername = "user";
    private Guid _currentUserId;
    private Guid? _currentUserRoleId;
    private static readonly int _clientType = DetectClientType();

    // ── Cached UI resources (avoids per-rebuild native allocations) ──
    private static readonly FontFamily _monoFont = new("Consolas, Courier New, monospace");
    private static readonly SolidColorBrush _brushTransparent = new(Colors.Transparent);
    private static readonly Dictionary<int, SolidColorBrush> _brushCache = [];
    // ── Job screenshot pool (reuse stream + bitmap across job switches) ──
    private readonly MemoryStream _pooledScreenshotStream = new(capacity: 512 * 1024);
    private Microsoft.UI.Xaml.Media.Imaging.BitmapImage? _pooledBitmap;
    private StackPanel? _screenshotContainer;
    private TextBlock? _screenshotLabel;
    private Image? _screenshotImage;

    // ── Job log row pool (avoids per-switch GC pressure) ──
    private readonly record struct JobLogRow(Grid Root, TextBlock Timestamp, TextBlock Level, TextBlock Message);
    private readonly List<JobLogRow> _jobLogPool = [];
    private int _jobLogPoolUsed;
    private DispatcherTimer? _loadingTimer;
    private int _loadingFrame;
    private TextBlock? _loadingMsgBlock;

    // ── Chat bubble pool (avoids per-history-load GC pressure) ──
    private readonly record struct ChatBubbleRow(Border Root, TextBlock Role, TextBlock Time, TextBlock Content);
    private readonly List<ChatBubbleRow> _chatBubblePool = [];
    private int _chatBubblePoolUsed;

    // ── Streaming pool (reuse StringBuilder across send cycles) ──
    private readonly StringBuilder _pooledStreamBuilder = new(capacity: 4096);

    // ── ComboBox item pools (reuse across selector rebuilds) ──
    private readonly List<ComboBoxItem> _agentItemPool = [];
    private int _agentItemPoolUsed;
    private readonly List<ComboBoxItem> _threadItemPool = [];
    private int _threadItemPoolUsed;
    private readonly ComboBoxItem _threadNoSelItem = new() { Content = "[No thread]" };
    private readonly List<ComboBoxItem> _jobItemPool = [];
    private int _jobItemPoolUsed;
    private readonly ComboBoxItem _jobNoSelItem = new() { Content = "(none — show chat)" };

    // ── Reusable scratch collections ──
    private readonly HashSet<Guid> _sidebarContextChannelIds = [];
    private readonly List<string> _jobTimestampParts = new(3);

    // ── Resource lookup cache (populated per settings load) ──
    private readonly Dictionary<string, List<ResourceItemDto>> _resourceLookupCache = new(13);

    // ── Transcription mic state ──
    private Guid? _activeTranscriptionJobId;
    private CancellationTokenSource? _transcriptionPollCts;
    private readonly StringBuilder _transcriptionAccumulator = new(capacity: 2048);

    private const string TranscriptionAgentKey = "TranscriptionAgentId";
    private const string SelectedAudioDeviceKey = "SelectedAudioDeviceId";

    private static SolidColorBrush Brush(int rgb)
    {
        if (!_brushCache.TryGetValue(rgb, out var brush))
        {
            brush = new SolidColorBrush(ColorFrom(rgb));
            _brushCache[rgb] = brush;
        }
        return brush;
    }

    private static string DetectedClientTypeName => _clientType switch
    {
        0 => "CLI", 1 => "API", 2 => "Telegram", 3 => "Discord",
        4 => "WhatsApp", 5 => "VisualStudio", 6 => "VisualStudioCode",
        7 => "UnoWindows", 8 => "UnoAndroid", 9 => "UnoMacOS",
        10 => "UnoLinux", 11 => "UnoBrowser", _ => "Other",
    };

    /// <summary>
    /// Returns the <c>ChatClientType</c> enum integer value matching the
    /// current Uno Platform runtime host.
    /// </summary>
    private static int DetectClientType()
    {
        // ChatClientType enum values:
        // 7 = UnoWindows, 8 = UnoAndroid, 9 = UnoMacOS,
        // 10 = UnoLinux, 11 = UnoBrowser
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Create("BROWSER")))
            return 11; // UnoBrowser
        if (OperatingSystem.IsAndroid())
            return 8;  // UnoAndroid
        if (OperatingSystem.IsMacOS() || OperatingSystem.IsMacCatalyst())
            return 9;  // UnoMacOS
        if (OperatingSystem.IsLinux())
            return 10; // UnoLinux
        if (OperatingSystem.IsWindows())
            return 7;  // UnoWindows
        return 12; // Other
    }

    public MainPage()
    {
        this.InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (App.Services is null) return;
        await LoadUserInfoAsync();
        await LoadRolesAsync();
        var channels = await LoadSidebarAsync();
        await LoadAgentsAsync(null, null);

        // Auto-select the most recently created channel
        if (_selectedChannelId is null && channels.Count > 0)
        {
            var mostRecent = channels.OrderByDescending(ch => ch.CreatedAt).First();
            await SelectChannelAsync(mostRecent.Id, mostRecent.Title, mostRecent.AgentName);
        }

        UpdateCursor();
    }

    // ── User & Roles ────────────────────────────────────────────

    private async Task LoadUserInfoAsync()
    {
        var api = App.Services!.GetRequiredService<SharpClawApiClient>();
        try
        {
            using var resp = await api.GetAsync("/auth/me");
            if (resp.IsSuccessStatusCode)
            {
                using var stream = await resp.Content.ReadAsStreamAsync();
                using var doc = await JsonDocument.ParseAsync(stream);
                _currentUsername = doc.RootElement.GetProperty("username").GetString() ?? "user";
                if (doc.RootElement.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.String)
                    _currentUserId = idProp.GetGuid();
                if (doc.RootElement.TryGetProperty("roleId", out var rp) && rp.ValueKind == JsonValueKind.String)
                    _currentUserRoleId = rp.GetGuid();
                else
                    _currentUserRoleId = null;
            }
        }
        catch { /* API not reachable */ }
    }

    private async Task LoadRolesAsync()
    {
        var api = App.Services!.GetRequiredService<SharpClawApiClient>();
        try
        {
            using var resp = await api.GetAsync("/roles");
            if (resp.IsSuccessStatusCode)
            {
                using var stream = await resp.Content.ReadAsStreamAsync();
                _allRoles = await JsonSerializer.DeserializeAsync<List<RoleDto>>(stream, Json) ?? [];
            }
        }
        catch { _allRoles = []; }
    }

    // ── Sidebar ──────────────────────────────────────────────────

    private async Task<List<ChannelDto>> LoadSidebarAsync()
    {
        var api = App.Services!.GetRequiredService<SharpClawApiClient>();

        List<ContextDto>? contexts = null;
        List<ChannelDto>? channels = null;

        try
        {
            using var ctxResp = await api.GetAsync("/channel-contexts");
            if (ctxResp.IsSuccessStatusCode)
            {
                using var ctxStream = await ctxResp.Content.ReadAsStreamAsync();
                contexts = await JsonSerializer.DeserializeAsync<List<ContextDto>>(ctxStream, Json);
            }

            using var chResp = await api.GetAsync("/channels");
            if (chResp.IsSuccessStatusCode)
            {
                using var chStream = await chResp.Content.ReadAsStreamAsync();
                channels = await JsonSerializer.DeserializeAsync<List<ChannelDto>>(chStream, Json);
            }
        }
        catch { /* API not reachable — leave empty */ }

        contexts ??= [];
        channels ??= [];

        _sidebarContextChannelIds.Clear();
        SidebarPanel.Children.Clear();

        // Group channels under their context, sorted by newest channel
        foreach (var ctx in contexts.OrderByDescending(c =>
            channels.Where(ch => ch.ContextId == c.Id).Max(ch => (DateTimeOffset?)ch.CreatedAt) ?? c.CreatedAt))
        {
            var ctxChannels = channels
                .Where(ch => ch.ContextId == ctx.Id)
                .OrderByDescending(ch => ch.CreatedAt)
                .ToList();

            foreach (var ch in ctxChannels)
                _sidebarContextChannelIds.Add(ch.Id);

            var isExpanded = _expandedContexts.GetValueOrDefault(ctx.Id, true);
            AddContextGroup(ctx, ctxChannels, isExpanded);
        }

        // Ungrouped channels (no context)
        var ungrouped = channels
            .Where(ch => ch.ContextId is null && !_sidebarContextChannelIds.Contains(ch.Id))
            .OrderByDescending(ch => ch.CreatedAt)
            .ToList();

        if (ungrouped.Count > 0)
        {
            AddSectionLabel("Ungrouped");
            foreach (var ch in ungrouped)
                AddChannelButton(ch);
        }

        return channels;
    }

    private void AddContextGroup(ContextDto ctx, List<ChannelDto> channels, bool expanded)
    {
        var header = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Background = _brushTransparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(8, 6, 8, 6),
            Tag = ctx.Id,
        };

        var arrow = expanded ? "▾" : "▸";
        var headerPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        headerPanel.Children.Add(new TextBlock
        {
            Text = arrow,
            FontFamily = _monoFont,
            FontSize = 11,
            Foreground = Brush(0x00FF00),
        });
        headerPanel.Children.Add(new TextBlock
        {
            Text = ctx.Name,
            FontFamily = _monoFont,
            FontSize = 12,
            Foreground = Brush(0xCCCCCC),
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        });
        headerPanel.Children.Add(new TextBlock
        {
            Text = $"({channels.Count})",
            FontFamily = _monoFont,
            FontSize = 10,
            Foreground = Brush(0x777777),
            VerticalAlignment = VerticalAlignment.Center,
        });

        header.Content = headerPanel;

        var channelContainer = new StackPanel
        {
            Spacing = 0,
            Visibility = expanded ? Visibility.Visible : Visibility.Collapsed,
            Margin = new Thickness(12, 0, 0, 0),
        };

        header.Click += (_, _) =>
        {
            var nowExpanded = channelContainer.Visibility == Visibility.Collapsed;
            channelContainer.Visibility = nowExpanded ? Visibility.Visible : Visibility.Collapsed;
            _expandedContexts[ctx.Id] = nowExpanded;
            ((headerPanel.Children[0] as TextBlock)!).Text = nowExpanded ? "▾" : "▸";
        };

        SidebarPanel.Children.Add(header);

        foreach (var ch in channels)
            AddChannelButton(ch, channelContainer);

        SidebarPanel.Children.Add(channelContainer);
    }

    private void AddSectionLabel(string text)
    {
        SidebarPanel.Children.Add(new TextBlock
        {
            Text = $"── {text} ──",
            FontFamily = _monoFont,
            FontSize = 10,
            Foreground = Brush(0x777777),
            Margin = new Thickness(8, 10, 0, 4),
        });
    }

    private void AddChannelButton(ChannelDto ch, StackPanel? parent = null)
    {
        var isSelected = ch.Id == _selectedChannelId;

        var row = new Grid { HorizontalAlignment = HorizontalAlignment.Stretch };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // ── Main channel button ──
        var btn = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Background = _brushTransparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(8, 5, 8, 5),
            Tag = ch.Id,
        };

        var nameBlock = new TextBlock
        {
            Text = Truncate(ch.Title, 24),
            FontFamily = _monoFont,
            FontSize = 12,
            Foreground = Brush(isSelected ? 0xE0E0E0 : 0x999999),
        };

        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        panel.Children.Add(new TextBlock
        {
            Text = "#",
            FontFamily = _monoFont,
            FontSize = 12,
            Foreground = Brush(isSelected ? 0x00FF00 : 0x555555),
        });
        panel.Children.Add(nameBlock);
        btn.Content = panel;

        btn.PointerEntered += (_, _) => Cursor.SetCommand($"sharpclaw channel select {ch.Id} ");
        btn.PointerExited += (_, _) => UpdateCursor();
        btn.Click += async (_, _) => await SelectChannelAsync(ch.Id, ch.Title, ch.AgentName);

        Grid.SetColumn(btn, 0);
        row.Children.Add(btn);

        // ── Action buttons (edit + delete) ──
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0,
            Margin = new Thickness(0, 0, 4, 0),
        };

        var editBtn = new Button
        {
            Content = new TextBlock
            {
                Text = "✎",
                FontFamily = _monoFont,
                FontSize = 11,
                Foreground = Brush(0x00FF00),
            },
            Background = _brushTransparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(4, 2, 4, 2),
            MinWidth = 0,
            MinHeight = 0,
        };

        var deleteBtn = new Button
        {
            Content = new TextBlock
            {
                Text = "✕",
                FontFamily = _monoFont,
                FontSize = 11,
                Foreground = Brush(0xFF4444),
            },
            Background = _brushTransparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(4, 2, 4, 2),
            MinWidth = 0,
            MinHeight = 0,
        };

        editBtn.Click += (_, _) => BeginChannelRename(ch, row, btn, actions);
        deleteBtn.Click += async (_, _) => await DeleteChannelAsync(ch.Id);

        actions.Children.Add(editBtn);
        actions.Children.Add(deleteBtn);

        Grid.SetColumn(actions, 1);
        row.Children.Add(actions);

        // Show/hide actions on hover
        row.PointerEntered += (_, _) => actions.Opacity = 1;
        row.PointerExited += (_, _) => actions.Opacity = 0;

        (parent ?? SidebarPanel).Children.Add(row);
    }

    private void BeginChannelRename(ChannelDto ch, Grid row, Button btn, StackPanel actions)
    {
        btn.Visibility = Visibility.Collapsed;
        actions.Opacity = 0;

        var renameBox = new TextBox
        {
            Text = ch.Title,
            FontFamily = _monoFont,
            FontSize = 12,
            Foreground = Brush(0x00FF00),
            Background = Brush(0x1A1A1A),
            BorderBrush = Brush(0x333333),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(6, 4, 6, 4),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MinHeight = 0,
        };

        Grid.SetColumn(renameBox, 0);
        Grid.SetColumnSpan(renameBox, 2);
        row.Children.Add(renameBox);

        renameBox.SelectAll();
        renameBox.Focus(FocusState.Programmatic);

        var committed = false;

        async Task CommitRename()
        {
            if (committed) return;
            committed = true;

            var newTitle = renameBox.Text.Trim();
            row.Children.Remove(renameBox);
            btn.Visibility = Visibility.Visible;

            if (string.IsNullOrEmpty(newTitle) || newTitle == ch.Title) return;

            var api = App.Services!.GetRequiredService<SharpClawApiClient>();
            try
            {
                var body = JsonSerializer.Serialize(new { title = newTitle }, Json);
                var content = new StringContent(body, Encoding.UTF8, "application/json");
                var resp = await api.PutAsync($"/channels/{ch.Id}", content);
                if (resp.IsSuccessStatusCode)
                {
                    // Update the visible label
                    if (btn.Content is StackPanel sp && sp.Children.Count > 1
                        && sp.Children[1] is TextBlock tb)
                        tb.Text = Truncate(newTitle, 24);

                    // Update header if this channel is selected
                    if (_selectedChannelId == ch.Id)
                        ChatTitleBlock.Text = $"# {newTitle}";
                }
            }
            catch { /* swallow */ }
        }

        renameBox.KeyDown += async (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                e.Handled = true;
                await CommitRename();
            }
            else if (e.Key == Windows.System.VirtualKey.Escape)
            {
                e.Handled = true;
                committed = true;
                row.Children.Remove(renameBox);
                btn.Visibility = Visibility.Visible;
            }
        };

        renameBox.LostFocus += async (_, _) => await CommitRename();
    }

    private async Task DeleteChannelAsync(Guid channelId)
    {
        var api = App.Services!.GetRequiredService<SharpClawApiClient>();
        try
        {
            var resp = await api.DeleteAsync($"/channels/{channelId}");
            if (resp.IsSuccessStatusCode)
            {
                if (_selectedChannelId == channelId)
                {
                    _selectedChannelId = null;
                    _selectedThreadId = null;
                    _selectedJobId = null;
                    _pendingNewThread = false;
                    ChatTitleBlock.Text = "> Select or create a channel";
                    ChannelTabBar.Visibility = Visibility.Collapsed;
                    _settingsMode = false;
                    SettingsScroller.Visibility = Visibility.Collapsed;
                    ThreadSelectorPanel.Visibility = Visibility.Collapsed;
                    JobSelectorPanel.Visibility = Visibility.Collapsed;
                    OneOffWarning.Visibility = Visibility.Collapsed;
                    ShowChatView();
                    _chatBubblePoolUsed = 0;
                    MessagesPanel.Children.Clear();
                }

                await LoadSidebarAsync();
                UpdateCursor();
            }
        }
        catch { /* swallow */ }
    }

    /// <summary>
    /// Updates selected-channel highlighting in the sidebar without rebuilding
    /// the element tree — zero allocations, only cached brush reassignment.
    /// </summary>
    private void UpdateSidebarHighlight()
    {
        foreach (var child in SidebarPanel.Children)
        {
            if (child is StackPanel container)
            {
                foreach (var inner in container.Children)
                    TryHighlightChannelRow(inner);
            }
            else
            {
                TryHighlightChannelRow(child);
            }
        }
    }

    private void TryHighlightChannelRow(UIElement element)
    {
        if (element is not Grid row || row.Children.Count == 0
            || row.Children[0] is not Button { Tag: Guid channelId } btn
            || btn.Content is not StackPanel sp || sp.Children.Count < 2)
            return;

        var isSelected = channelId == _selectedChannelId;
        if (sp.Children[0] is TextBlock hash)
            hash.Foreground = Brush(isSelected ? 0x00FF00 : 0x555555);
        if (sp.Children[1] is TextBlock name)
            name.Foreground = Brush(isSelected ? 0xE0E0E0 : 0x999999);
    }

    private async Task SelectChannelAsync(Guid id, string title, string? agentName)
    {
        _selectedChannelId = id;
        _selectedThreadId = null;
        _selectedJobId = null;
        _pendingNewThread = false;
        ChatTitleBlock.Text = $"# {title}";
        ChannelTabBar.Visibility = Visibility.Visible;
        if (_settingsMode)
        {
            _settingsMode = false;
            UpdateTabHighlight();
        }
        UpdateCursor();

        // Fetch full channel details for allowed agents
        Guid? channelAgentId = null;
        List<Guid>? allowedAgentIds = null;
        var api = App.Services!.GetRequiredService<SharpClawApiClient>();
        try
        {
            using var chResp = await api.GetAsync($"/channels/{id}");
            if (chResp.IsSuccessStatusCode)
            {
                using var chDetailStream = await chResp.Content.ReadAsStreamAsync();
                using var doc = await JsonDocument.ParseAsync(chDetailStream);
                if (doc.RootElement.TryGetProperty("agent", out var aProp) && aProp.ValueKind == JsonValueKind.Object
                    && aProp.TryGetProperty("id", out var agentIdProp) && agentIdProp.ValueKind == JsonValueKind.String)
                    channelAgentId = agentIdProp.GetGuid();
                if (doc.RootElement.TryGetProperty("allowedAgents", out var aaProp) && aaProp.ValueKind == JsonValueKind.Array)
                    allowedAgentIds = aaProp.EnumerateArray()
                        .Where(e => e.ValueKind == JsonValueKind.Object && e.TryGetProperty("id", out _))
                        .Select(e => e.GetProperty("id").GetGuid()).ToList();
            }
        }
        catch { /* swallow */ }

        _selectedAgentId = channelAgentId;
        UpdateSidebarHighlight();
        await LoadAgentsAsync(channelAgentId, allowedAgentIds);
        await LoadThreadsAsync(id);
        await LoadJobsAsync(id);
        ShowChatView();
        await LoadHistoryAsync(id);
        UpdateMicState();

        DispatcherQueue.TryEnqueue(() => MessageInput.Focus(FocusState.Programmatic));
    }

    // ── Agents ─────────────────────────────────────────────────

    private async Task LoadAgentsAsync(Guid? channelAgentId, IReadOnlyList<Guid>? allowedAgentIds)
    {
        AgentSelector.SelectionChanged -= OnAgentSelectionChanged;
        AgentSelector.Items.Clear();

        var api = App.Services!.GetRequiredService<SharpClawApiClient>();

        try
        {
            using var resp = await api.GetAsync("/agents");
            if (resp.IsSuccessStatusCode)
            {
                using var agentStream = await resp.Content.ReadAsStreamAsync();
                _allAgents = await JsonSerializer.DeserializeAsync<List<AgentDto>>(agentStream, Json) ?? [];
            }
        }
        catch { _allAgents = []; }

        List<AgentDto> visible;
        if (_selectedChannelId is not null && channelAgentId is not null)
        {
            // Inside a channel: show only default + allowed agents
            var allowed = new HashSet<Guid> { channelAgentId.Value };
            if (allowedAgentIds is not null)
                foreach (var id in allowedAgentIds)
                    allowed.Add(id);
            visible = _allAgents.Where(a => allowed.Contains(a.Id)).ToList();
        }
        else
        {
            // No channel selected: show all agents
            visible = _allAgents;
        }

        _agentItemPoolUsed = 0;
        foreach (var agent in visible)
        {
            ComboBoxItem item;
            if (_agentItemPoolUsed < _agentItemPool.Count)
                item = _agentItemPool[_agentItemPoolUsed++];
            else
            {
                item = new ComboBoxItem();
                _agentItemPool.Add(item);
                _agentItemPoolUsed++;
            }
            item.Content = $"{agent.Name}  ({agent.ProviderName}/{agent.ModelName})";
            item.Tag = agent.Id;
            AgentSelector.Items.Add(item);
        }

        // Restore or default selection
        var selectedIndex = -1;
        var targetId = _selectedAgentId ?? channelAgentId;
        if (targetId is { } tgt)
        {
            for (var i = 0; i < AgentSelector.Items.Count; i++)
            {
                if (AgentSelector.Items[i] is ComboBoxItem ci && ci.Tag is Guid g && g == tgt)
                { selectedIndex = i; break; }
            }
        }
        AgentSelector.SelectedIndex = selectedIndex >= 0 ? selectedIndex : (AgentSelector.Items.Count > 0 ? 0 : -1);

        if (AgentSelector.SelectedItem is ComboBoxItem sel && sel.Tag is Guid selId)
            _selectedAgentId = selId;
        else
            _selectedAgentId = null;

        AgentSelector.SelectionChanged += OnAgentSelectionChanged;
    }

    private void OnAgentSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AgentSelector.SelectedItem is ComboBoxItem { Tag: Guid agentId })
            _selectedAgentId = agentId;
        else
            _selectedAgentId = null;

        UpdateCursor();
    }

    // ── Threads ──────────────────────────────────────────────────

    private async Task LoadThreadsAsync(Guid channelId)
    {
        ThreadSelectorPanel.Visibility = Visibility.Visible;
        _suppressThreadSelection = true;
        ThreadSelector.Items.Clear();

        ThreadSelector.Items.Add(_threadNoSelItem);

        List<ThreadDto>? threads = null;
        var api = App.Services!.GetRequiredService<SharpClawApiClient>();
        try
        {
            using var resp = await api.GetAsync($"/channels/{channelId}/threads");
            if (resp.IsSuccessStatusCode)
            {
                using var threadStream = await resp.Content.ReadAsStreamAsync();
                threads = await JsonSerializer.DeserializeAsync<List<ThreadDto>>(threadStream, Json);
                if (threads is not null)
                {
                    _threadItemPoolUsed = 0;
                    foreach (var t in threads)
                    {
                        ComboBoxItem item;
                        if (_threadItemPoolUsed < _threadItemPool.Count)
                            item = _threadItemPool[_threadItemPoolUsed++];
                        else
                        {
                            item = new ComboBoxItem();
                            item.PointerEntered += OnThreadItemHover;
                            item.PointerExited += OnSelectorItemPointerExited;
                            _threadItemPool.Add(item);
                            _threadItemPoolUsed++;
                        }
                        item.Content = t.Name;
                        item.Tag = t.Id;
                        ThreadSelector.Items.Add(item);
                    }
                }
            }
        }
        catch { /* API not reachable */ }

        // If no thread is pre-selected, pick the most recently updated one
        if (_selectedThreadId is null && threads is { Count: > 0 })
        {
            var mostRecent = threads.OrderByDescending(t => t.UpdatedAt).First();
            _selectedThreadId = mostRecent.Id;
        }

        var selectedIndex = 0;
        if (_selectedThreadId is { } tid)
        {
            for (var i = 0; i < ThreadSelector.Items.Count; i++)
            {
                if (ThreadSelector.Items[i] is ComboBoxItem ci && ci.Tag is Guid g && g == tid)
                { selectedIndex = i; break; }
            }
        }

        ThreadSelector.SelectedIndex = selectedIndex;
        _suppressThreadSelection = false;

        OneOffWarning.Visibility = _selectedThreadId is null && !_pendingNewThread
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private async void OnThreadSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressThreadSelection) return;

        // If user picks a real item, leave pending-new-thread mode and remove placeholder
        if (_pendingNewThread)
        {
            _pendingNewThread = false;
            RemovePendingThreadPlaceholder();
        }

        if (ThreadSelector.SelectedItem is ComboBoxItem { Tag: Guid tid })
            _selectedThreadId = tid;
        else
            _selectedThreadId = null;

        OneOffWarning.Visibility = _selectedThreadId is null
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (_selectedChannelId is { } channelId)
        {
            await LoadHistoryAsync(channelId);
            UpdateCursor();
        }
    }

    private void RemovePendingThreadPlaceholder()
    {
        for (var i = ThreadSelector.Items.Count - 1; i >= 0; i--)
        {
            if (ThreadSelector.Items[i] is ComboBoxItem { Tag: string tag } && tag == "__pending__")
            {
                _suppressThreadSelection = true;
                ThreadSelector.Items.RemoveAt(i);
                _suppressThreadSelection = false;
                break;
            }
        }
    }

    private void OnThreadItemHover(object sender, PointerRoutedEventArgs e)
    {
        if (sender is ComboBoxItem { Tag: Guid id })
            Cursor.SetCommand($"sharpclaw thread select {id} ");
    }

    private void OnSelectorItemPointerExited(object sender, PointerRoutedEventArgs e)
        => UpdateCursor();

    private void OnAddThreadClick(object sender, RoutedEventArgs e)
    {
        if (_selectedChannelId is null) return;

        _selectedThreadId = null;
        _pendingNewThread = true;
        _chatBubblePoolUsed = 0;
        MessagesPanel.Children.Clear();
        OneOffWarning.Visibility = Visibility.Collapsed;

        // Remove any existing pending placeholder, then add a new one and select it
        RemovePendingThreadPlaceholder();
        _suppressThreadSelection = true;
        var placeholder = new ComboBoxItem
        {
            Content = "[Send a message to create new thread]",
            Tag = "__pending__",
            FontStyle = Windows.UI.Text.FontStyle.Italic,
        };
        ThreadSelector.Items.Add(placeholder);
        ThreadSelector.SelectedItem = placeholder;
        _suppressThreadSelection = false;

        UpdateCursor();
        DispatcherQueue.TryEnqueue(() => MessageInput.Focus(FocusState.Programmatic));
    }

    // ── Jobs ─────────────────────────────────────────────────────

    private async Task LoadJobsAsync(Guid channelId)
    {
        JobSelectorPanel.Visibility = Visibility.Visible;
        _suppressJobSelection = true;
        JobSelector.Items.Clear();

        JobSelector.Items.Add(_jobNoSelItem);

        var api = App.Services!.GetRequiredService<SharpClawApiClient>();
        try
        {
            // Use the lightweight summaries endpoint — no ResultData, logs, or segments
            using var resp = await api.GetAsync($"/channels/{channelId}/jobs/summaries");
            if (resp.IsSuccessStatusCode)
            {
                using var jobStream = await resp.Content.ReadAsStreamAsync();
                _channelJobs = await JsonSerializer.DeserializeAsync<List<JobDto>>(jobStream, Json) ?? [];

                _jobItemPoolUsed = 0;
                foreach (var job in _channelJobs)
                {
                    var label = $"[{job.Status}] {job.ActionType}";
                    if (job.CreatedAt != default)
                        label += $"  {job.CreatedAt.LocalDateTime:MM/dd HH:mm}";
                    ComboBoxItem item;
                    if (_jobItemPoolUsed < _jobItemPool.Count)
                        item = _jobItemPool[_jobItemPoolUsed++];
                    else
                    {
                        item = new ComboBoxItem();
                        _jobItemPool.Add(item);
                        _jobItemPoolUsed++;
                    }
                    item.Content = label;
                    item.Tag = job.Id;
                    JobSelector.Items.Add(item);
                }
            }
        }
        catch { _channelJobs = []; }

        // Restore selection or default to "(none)"
        var selectedIndex = 0;
        if (_selectedJobId is { } jid)
        {
            for (var i = 0; i < JobSelector.Items.Count; i++)
            {
                if (JobSelector.Items[i] is ComboBoxItem ci && ci.Tag is Guid g && g == jid)
                { selectedIndex = i; break; }
            }
        }

        JobSelector.SelectedIndex = selectedIndex;
        _suppressJobSelection = false;
    }

    private async void OnJobSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressJobSelection) return;

        if (JobSelector.SelectedItem is ComboBoxItem { Tag: Guid jobId })
        {
            _selectedJobId = jobId;
            await ShowJobViewAsync(jobId);
        }
        else
        {
            _selectedJobId = null;
            DeallocateJobView();
            ShowChatView();
        }
    }

    private async void OnRefreshJobsClick(object sender, RoutedEventArgs e)
    {
        if (_selectedChannelId is { } channelId)
            await LoadJobsAsync(channelId);

        if (_selectedJobId is { } jobId)
            await ShowJobViewAsync(jobId);
    }

    private async Task ShowJobViewAsync(Guid jobId)
    {
        MessagesScroller.Visibility = Visibility.Collapsed;
        ChatInputArea.Visibility = Visibility.Collapsed;
        JobViewPanel.Visibility = Visibility.Visible;

        // Show loading indicator while the full job is fetched on demand
        DeallocateJobView();
        JobStatusBlock.Text = "loading";
        JobStatusBlock.Foreground = Brush(0x999999);
        JobActionBlock.Text = string.Empty;
        JobTimestampBlock.Text = string.Empty;
        StartLoadingAnimation();

        if (_selectedChannelId is not { } channelId) return;

        var api = App.Services!.GetRequiredService<SharpClawApiClient>();
        try
        {
            using var resp = await api.GetAsync($"/channels/{channelId}/jobs/{jobId}");
            if (!resp.IsSuccessStatusCode)
            {
                StopLoadingAnimation();
                _jobLogPoolUsed = 0;
                JobLogsPanel.Children.Clear();
                JobStatusBlock.Text = "error";
                JobStatusBlock.Foreground = Brush(0xFF4444);
                AppendJobLog("error", $"Failed to load job: {(int)resp.StatusCode} {resp.ReasonPhrase}", null);
                return;
            }

            var job = await JsonSerializer.DeserializeAsync<JobDetailDto>(
                await resp.Content.ReadAsStreamAsync(), Json);
            if (job is null)
            {
                StopLoadingAnimation();
                _jobLogPoolUsed = 0;
                JobLogsPanel.Children.Clear();
                AppendJobLog("error", "Job response was null.", null);
                return;
            }

            // Clear the loading indicator now that we have real data
            StopLoadingAnimation();
            _jobLogPoolUsed = 0;
            JobLogsPanel.Children.Clear();

            // Header info
            JobStatusBlock.Text = $"status: {job.Status}";
            JobStatusBlock.Foreground = Brush(job.Status switch
            {
                "Completed" => 0x00FF00,
                "Failed" or "Denied" => 0xFF4444,
                "Executing" => 0x00AAFF,
                "AwaitingApproval" => 0xFFAA00,
                "Cancelled" => 0x888888,
                _ => 0xCCCCCC,
            });
            JobActionBlock.Text = $"action: {job.ActionType}";
            _jobTimestampParts.Clear();
            if (job.CreatedAt != default) _jobTimestampParts.Add($"created: {job.CreatedAt.LocalDateTime:yyyy-MM-dd HH:mm:ss}");
            if (job.StartedAt.HasValue) _jobTimestampParts.Add($"started: {job.StartedAt.Value.LocalDateTime:HH:mm:ss}");
            if (job.CompletedAt.HasValue) _jobTimestampParts.Add($"completed: {job.CompletedAt.Value.LocalDateTime:HH:mm:ss}");
            JobTimestampBlock.Text = string.Join("  |  ", _jobTimestampParts);

            // Logs
            if (job.Logs is { Count: > 0 })
            {
                foreach (var log in job.Logs)
                    AppendJobLog(log.Level, TruncateForDisplay(log.Message), log.Timestamp);
            }

            // Transcription segments
            if (job.Segments is { Count: > 0 })
            {
                AppendJobLog("info", $"── transcription segments ({job.Segments.Count}) ──", null);
                foreach (var seg in job.Segments)
                {
                    var timeRange = $"[{FormatSegmentTime(seg.StartTime)} → {FormatSegmentTime(seg.EndTime)}]";
                    var conf = seg.Confidence.HasValue ? $"  ({seg.Confidence.Value:P0})" : "";
                    var prov = seg.IsProvisional ? "  [provisional]" : "";
                    AppendJobLog("result", $"{timeRange}{conf}{prov}  {seg.Text}", seg.Timestamp);
                }
            }

            // Result / Error data
            if (!string.IsNullOrWhiteSpace(job.ResultData))
                await AppendJobResultAsync(job.ResultData);
            if (!string.IsNullOrWhiteSpace(job.ErrorLog))
                AppendJobLog("error", TruncateForDisplay(job.ErrorLog), null);

            if (job.Logs is { Count: 0 } && job.Segments is null or { Count: 0 }
                && string.IsNullOrWhiteSpace(job.ResultData) && string.IsNullOrWhiteSpace(job.ErrorLog))
                AppendJobLog("info", "(no log entries yet)", null);

            // Show appropriate controls based on status
            if (job.Status == "AwaitingApproval")
            {
                JobApproveButton.Visibility = Visibility.Visible;
                JobCancelButton.Visibility = Visibility.Visible;
            }
            else if (job.Status is "Queued" or "Executing")
            {
                JobCancelButton.Visibility = Visibility.Visible;
                JobStopButton.Visibility = Visibility.Visible;
            }
        }
        catch (Exception ex)
        {
            StopLoadingAnimation();
            _jobLogPoolUsed = 0;
            JobLogsPanel.Children.Clear();
            JobStatusBlock.Text = "error";
            JobStatusBlock.Foreground = Brush(0xFF4444);
            AppendJobLog("error", $"Failed to load job: {ex.Message}", null);
        }

        JobLogsScroller.UpdateLayout();
        JobLogsScroller.ChangeView(null, JobLogsScroller.ScrollableHeight, null);
    }

    /// <summary>
    /// Clears job view content and returns all pooled resources so the next
    /// job load can reuse them without fresh allocations.
    /// </summary>
    private void DeallocateJobView()
    {
        StopLoadingAnimation();

        // Detach from the rendering pipeline but keep _pooledBitmap alive.
        // Uno's BitmapImage.TryOpenSourceAsync roots an async state machine
        // in a static Dictionary<Int32, Task>; creating a new BitmapImage
        // each time adds entries that are never removed.  By keeping a single
        // instance, there is at most one entry in that dictionary and the
        // next SetSourceAsync replaces the native bitmap in-place.
        if (_screenshotImage is not null)
            _screenshotImage.Source = null;

        // Reclaim the pooled MemoryStream buffer; trim if a previous
        // screenshot inflated the internal buffer beyond 2 MB.
        _pooledScreenshotStream.SetLength(0);
        if (_pooledScreenshotStream.Capacity > 2 * 1024 * 1024)
            _pooledScreenshotStream.Capacity = 512 * 1024;

        _jobLogPoolUsed = 0;
        JobLogsPanel.Children.Clear();
        JobApproveButton.Visibility = Visibility.Collapsed;
        JobCancelButton.Visibility = Visibility.Collapsed;
        JobStopButton.Visibility = Visibility.Collapsed;
    }

    private void StartLoadingAnimation()
    {
        StopLoadingAnimation();
        var row = AcquireJobLogRow();
        row.Timestamp.Visibility = Visibility.Collapsed;
        row.Level.Text = "[info]";
        row.Level.Foreground = Brush(0x00AAFF);
        row.Message.Text = "loading.";
        JobLogsPanel.Children.Add(row.Root);

        _loadingMsgBlock = row.Message;
        _loadingFrame = 0;
        if (_loadingTimer is null)
        {
            _loadingTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            _loadingTimer.Tick += OnLoadingTick;
        }
        _loadingTimer.Start();
    }

    private void OnLoadingTick(object? sender, object e)
    {
        if (_loadingMsgBlock is null) return;
        _loadingFrame = (_loadingFrame + 1) % 3;
        _loadingMsgBlock.Text = _loadingFrame switch
        {
            0 => "loading.",
            1 => "loading..",
            _ => "loading...",
        };
    }

    private void StopLoadingAnimation()
    {
        _loadingTimer?.Stop();
        _loadingMsgBlock = null;
    }

    private void ShowChatView()
    {
        JobViewPanel.Visibility = Visibility.Collapsed;
        SettingsScroller.Visibility = Visibility.Collapsed;
        AgentSelectorPanel.Visibility = Visibility.Visible;
        MessagesScroller.Visibility = Visibility.Visible;
        ChatInputArea.Visibility = Visibility.Visible;
    }

    private const string ScreenshotMarker = "[SCREENSHOT_BASE64]";

    /// <summary>
    /// Appends job result data. If the result contains a <c>[SCREENSHOT_BASE64]</c>
    /// marker, renders the text portion as a log entry and the base64 data as an
    /// actual <see cref="Image"/> element. Otherwise falls back to truncated text.
    /// All byte buffers are rented from <see cref="ArrayPool{T}"/> and the
    /// <see cref="MemoryStream"/> is reused across job switches.
    /// </summary>
    private async Task AppendJobResultAsync(string resultData)
    {
        var markerIndex = resultData.IndexOf(ScreenshotMarker, StringComparison.Ordinal);
        if (markerIndex >= 0)
        {
            var textPart = resultData[..markerIndex].TrimEnd();
            if (!string.IsNullOrWhiteSpace(textPart))
                AppendJobLog("result", textPart, null);

            // Use a span over the original string to avoid allocating a substring.
            var base64Span = resultData.AsSpan(markerIndex + ScreenshotMarker.Length);
            try
            {
                // Rent a decode buffer from the shared pool instead of letting
                // Convert.FromBase64String allocate a fresh byte[] every time.
                var maxBytes = (base64Span.Length * 3 / 4) + 4;
                var rented = ArrayPool<byte>.Shared.Rent(maxBytes);
                int written;
                try
                {
                    if (!Convert.TryFromBase64Chars(base64Span, rented, out written))
                        throw new FormatException("Invalid base64 screenshot data.");

                    // Reuse the pooled MemoryStream: reset, write, seek.
                    _pooledScreenshotStream.SetLength(0);
                    _pooledScreenshotStream.Write(rented, 0, written);
                    _pooledScreenshotStream.Position = 0;
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(rented);
                }

                // Reuse or create a single BitmapImage; SetSourceAsync replaces
                // the previous native bitmap so the old decoded data is freed.
                _pooledBitmap ??= new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
                await _pooledBitmap.SetSourceAsync(
                    _pooledScreenshotStream.AsRandomAccessStream());

                if (_screenshotContainer is null)
                {
                    _screenshotLabel = new TextBlock
                    {
                        FontFamily = _monoFont,
                        FontSize = 10,
                        Foreground = Brush(0x00FF00),
                    };
                    _screenshotImage = new Image
                    {
                        MaxWidth = 640,
                        MaxHeight = 480,
                        Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform,
                        HorizontalAlignment = HorizontalAlignment.Left,
                        Margin = new Thickness(0, 4, 0, 4),
                    };
                    _screenshotContainer = new StackPanel { Spacing = 4 };
                    _screenshotContainer.Children.Add(_screenshotLabel);
                    _screenshotContainer.Children.Add(_screenshotImage);
                }

                _screenshotLabel!.Text = $"[screenshot] {written / 1024}KB";
                _screenshotImage!.Source = _pooledBitmap;
                JobLogsPanel.Children.Add(_screenshotContainer);
            }
            catch
            {
                AppendJobLog("result", TruncateForDisplay(resultData), null);
            }
        }
        else
        {
            AppendJobLog("result", TruncateForDisplay(resultData), null);
        }
    }

    private JobLogRow AcquireJobLogRow()
    {
        if (_jobLogPoolUsed < _jobLogPool.Count)
            return _jobLogPool[_jobLogPoolUsed++];

        var root = new Grid { ColumnSpacing = 8 };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var ts = new TextBlock
        {
            FontFamily = _monoFont,
            FontSize = 11,
            Foreground = Brush(0x555555),
            VerticalAlignment = VerticalAlignment.Top,
        };
        Grid.SetColumn(ts, 0);
        root.Children.Add(ts);

        var lv = new TextBlock
        {
            FontFamily = _monoFont,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Top,
            MinWidth = 60,
        };
        Grid.SetColumn(lv, 1);
        root.Children.Add(lv);

        var msg = new TextBlock
        {
            FontFamily = _monoFont,
            FontSize = 11,
            Foreground = Brush(0xCCCCCC),
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
        };
        Grid.SetColumn(msg, 2);
        root.Children.Add(msg);

        var entry = new JobLogRow(root, ts, lv, msg);
        _jobLogPool.Add(entry);
        _jobLogPoolUsed++;
        return entry;
    }

    private void AppendJobLog(string level, string message, DateTimeOffset? timestamp)
    {
        var row = AcquireJobLogRow();

        if (timestamp.HasValue)
        {
            row.Timestamp.Text = timestamp.Value.LocalDateTime.ToString("HH:mm:ss");
            row.Timestamp.Visibility = Visibility.Visible;
        }
        else
        {
            row.Timestamp.Visibility = Visibility.Collapsed;
        }

        row.Level.Text = $"[{level}]";
        row.Level.Foreground = Brush(level.ToLowerInvariant() switch
        {
            "error" => 0xFF4444,
            "warning" or "warn" => 0xFFAA00,
            "result" => 0x00FF00,
            _ => 0x00AAFF,
        });
        row.Message.Text = message;

        JobLogsPanel.Children.Add(row.Root);
    }

    private static string FormatSegmentTime(double seconds)
    {
        var ts = TimeSpan.FromSeconds(seconds);
        return ts.TotalHours >= 1
            ? ts.ToString(@"h\:mm\:ss\.f")
            : ts.ToString(@"m\:ss\.f");
    }

    private static string TruncateForDisplay(string text, int maxLength = 2000)
    {
        if (text.Length <= maxLength) return text;
        return text[..maxLength] + $"\n… [{text.Length:N0} chars total — truncated for display]";
    }

    private async void OnJobApproveClick(object sender, RoutedEventArgs e)
    {
        if (_selectedChannelId is not { } channelId || _selectedJobId is not { } jobId) return;

        var api = App.Services!.GetRequiredService<SharpClawApiClient>();
        try
        {
            var body = JsonSerializer.Serialize(new { }, Json);
            var content = new StringContent(body, Encoding.UTF8, "application/json");
            var resp = await api.PostAsync($"/channels/{channelId}/jobs/{jobId}/approve", content);
            if (resp.IsSuccessStatusCode)
                await ShowJobViewAsync(jobId);
            else
                AppendJobLog("error", $"Approve failed: {(int)resp.StatusCode} {resp.ReasonPhrase}", DateTimeOffset.Now);
        }
        catch (Exception ex) { AppendJobLog("error", $"Approve failed: {ex.Message}", DateTimeOffset.Now); }
    }

    private async void OnJobCancelClick(object sender, RoutedEventArgs e)
    {
        if (_selectedChannelId is not { } channelId || _selectedJobId is not { } jobId) return;

        var api = App.Services!.GetRequiredService<SharpClawApiClient>();
        try
        {
            var resp = await api.PostAsync($"/channels/{channelId}/jobs/{jobId}/cancel", null);
            if (resp.IsSuccessStatusCode)
                await ShowJobViewAsync(jobId);
            else
                AppendJobLog("error", $"Cancel failed: {(int)resp.StatusCode} {resp.ReasonPhrase}", DateTimeOffset.Now);
        }
        catch (Exception ex) { AppendJobLog("error", $"Cancel failed: {ex.Message}", DateTimeOffset.Now); }
    }

    private async void OnJobStopClick(object sender, RoutedEventArgs e)
    {
        if (_selectedChannelId is not { } channelId || _selectedJobId is not { } jobId) return;

        var api = App.Services!.GetRequiredService<SharpClawApiClient>();
        try
        {
            var resp = await api.PostAsync($"/channels/{channelId}/jobs/{jobId}/stop", null);
            if (resp.IsSuccessStatusCode)
                await ShowJobViewAsync(jobId);
            else
                AppendJobLog("error", $"Stop failed: {(int)resp.StatusCode} {resp.ReasonPhrase}", DateTimeOffset.Now);
        }
        catch (Exception ex) { AppendJobLog("error", $"Stop failed: {ex.Message}", DateTimeOffset.Now); }
    }

    private void OnJobBackToChatClick(object sender, RoutedEventArgs e)
    {
        _selectedJobId = null;
        _suppressJobSelection = true;
        JobSelector.SelectedIndex = 0;
        _suppressJobSelection = false;
        DeallocateJobView();
        ShowChatView();
    }

    // ── Microphone / voice input ────────────────────────────────

    private void UpdateMicState()
    {
        var agentId = LoadLocalSetting(TranscriptionAgentKey);
        var deviceId = LoadLocalSetting(SelectedAudioDeviceKey);
        var configured = agentId is not null && Guid.TryParse(agentId, out _)
                      && deviceId is not null && Guid.TryParse(deviceId, out _)
                      && _selectedChannelId is not null;
        var isActive = _activeTranscriptionJobId is not null;

        MicButton.Opacity = configured || isActive ? 1.0 : 0.4;

        if (isActive)
            ToolTipService.SetToolTip(MicButton, "Stop transcription");
        else if (configured)
            ToolTipService.SetToolTip(MicButton, "Start voice input");
        else
            ToolTipService.SetToolTip(MicButton, "Set a transcription agent and audio device in channel settings to enable voice input");
    }

    private async void OnMicClick(object sender, RoutedEventArgs e)
    {
        if (_selectedChannelId is not { } channelId) return;

        // If a job is active, stop it
        if (_activeTranscriptionJobId is { } activeJobId)
        {
            await StopTranscriptionAsync(channelId, activeJobId);
            return;
        }

        var agentIdStr = LoadLocalSetting(TranscriptionAgentKey);
        var deviceIdStr = LoadLocalSetting(SelectedAudioDeviceKey);
        if (agentIdStr is null || !Guid.TryParse(agentIdStr, out var agentId)
            || deviceIdStr is null || !Guid.TryParse(deviceIdStr, out var deviceId))
            return;

        MicButton.Opacity = 0.6;

        var api = App.Services!.GetRequiredService<SharpClawApiClient>();
        try
        {
            var body = JsonSerializer.Serialize(new
            {
                actionType = "TranscribeFromAudioDevice",
                resourceId = deviceId,
                agentId,
            }, Json);
            var resp = await api.PostAsync($"/channels/{channelId}/jobs",
                new StringContent(body, Encoding.UTF8, "application/json"));
            if (!resp.IsSuccessStatusCode)
            {
                string errorDetail = $"{(int)resp.StatusCode} {resp.ReasonPhrase}";
                try
                {
                    using var errStream = await resp.Content.ReadAsStreamAsync();
                    var errMsg = await TryExtractErrorAsync(errStream);
                    if (errMsg is not null)
                        errorDetail = errMsg;
                }
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

            // Stream live transcription segments via SSE
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

    /// <summary>
    /// Streams live transcription segments via SSE from
    /// <c>/jobs/{jobId}/stream</c>.  Accumulates segment text into
    /// <see cref="MessageInput"/> so the user sees the transcription
    /// grow in real time.  Handles finalization (same ID, updated text)
    /// and retraction (empty text tombstone).
    /// </summary>
    private async Task StreamTranscriptionSegmentsAsync(Guid jobId, string prefixText, CancellationToken ct)
    {
        var api = App.Services!.GetRequiredService<SharpClawApiClient>();
        var dispatcher = DispatcherQueue;
        await Task.Delay(1000, ct).ConfigureAwait(false);

        var segments = new Dictionary<Guid, (string Text, double StartTime)>();
        var lastUiUpdate = 0L;
        var throttleTicks = System.Diagnostics.Stopwatch.Frequency * 150 / 1000; // ~150ms
        var lastDispatchedLength = 0;

        try
        {
            using var resp = await api.GetStreamAsync($"/jobs/{jobId}/stream", ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                dispatcher.TryEnqueue(() =>
                {
                    AppendMessage("system",
                        $"\u2717 Failed to connect to transcription stream: {(int)resp.StatusCode}",
                        DateTimeOffset.Now, senderName: "system");
                    ScrollToBottom();
                });
                return;
            }

            using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var reader = new StreamReader(stream);

            while (!reader.EndOfStream && !ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (line is null) break;

                if (line.StartsWith("event:"))
                {
                    if (line.AsSpan(6).Trim() is "done")
                        break;
                    continue;
                }

                if (!line.StartsWith("data: "))
                    continue;

                try
                {
                    var seg = JsonSerializer.Deserialize<TranscriptionSegmentDto>(
                        line.AsMemory(6).Span, Json);
                    if (seg is null) continue;

                    // Retractions (empty text) are ignored on the client
                    // side — once text has been accumulated into the
                    // input box it must never silently disappear.
                    // Finalization (same ID, updated text) replaces in-place.
                    if (!string.IsNullOrEmpty(seg.Text))
                        segments[seg.Id] = (seg.Text, seg.StartTime);

                    // Throttle UI dispatches to avoid flooding the
                    // dispatcher queue and starving the UI thread.
                    var now = System.Diagnostics.Stopwatch.GetTimestamp();
                    if (now - lastUiUpdate < throttleTicks)
                        continue;
                    lastUiUpdate = now;

                    _transcriptionAccumulator.Clear();
                    if (prefixText.Length > 0)
                    {
                        _transcriptionAccumulator.Append(prefixText);
                        _transcriptionAccumulator.Append(' ');
                    }
                    var first = true;
                    foreach (var s in segments.Values.OrderBy(s => s.StartTime))
                    {
                        if (!first) _transcriptionAccumulator.Append(' ');
                        _transcriptionAccumulator.Append(s.Text);
                        first = false;
                    }
                    var snapshot = _transcriptionAccumulator.ToString();
                    lastDispatchedLength = snapshot.Length;

                    dispatcher.TryEnqueue(() =>
                    {
                        MessageInput.Text = snapshot;
                    });
                }
                catch { /* malformed SSE line */ }
            }

            // Final flush for any content throttled in the last interval
            if (segments.Count > 0)
            {
                _transcriptionAccumulator.Clear();
                if (prefixText.Length > 0)
                {
                    _transcriptionAccumulator.Append(prefixText);
                    _transcriptionAccumulator.Append(' ');
                }
                var first = true;
                foreach (var s in segments.Values.OrderBy(s => s.StartTime))
                {
                    if (!first) _transcriptionAccumulator.Append(' ');
                    _transcriptionAccumulator.Append(s.Text);
                    first = false;
                }
                var finalSnapshot = _transcriptionAccumulator.ToString();
                if (finalSnapshot.Length >= lastDispatchedLength)
                    dispatcher.TryEnqueue(() => MessageInput.Text = finalSnapshot);
            }
        }
        catch (OperationCanceledException) { /* expected on stop */ }
        catch (Exception ex)
        {
            dispatcher.TryEnqueue(() =>
            {
                AppendMessage("system",
                    $"\u2717 Transcription stream error: {ex.Message}",
                    DateTimeOffset.Now, senderName: "system");
                ScrollToBottom();
            });
        }

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

    /// <summary>
    /// Toggles the chat input area between transcription-active (read-only,
    /// send disabled) and normal (editable, send enabled) states.
    /// </summary>
    private void SetTranscriptionInputState(bool active)
    {
        MessageInput.IsReadOnly = active;
        SendButton.IsEnabled = !active;

        if (active)
        {
            MessageInput.PlaceholderText = "Listening...";
            MessageInput.Opacity = 0.7;
        }
        else
        {
            MessageInput.PlaceholderText = "Type a message...";
            MessageInput.Opacity = 1.0;
        }
    }

    private static string? LoadLocalSetting(string key)
    {
        var settings = Windows.Storage.ApplicationData.Current.LocalSettings;
        return settings.Values.TryGetValue(key, out var val) ? val as string : null;
    }

    private static void SaveLocalSetting(string key, string? value)
    {
        var settings = Windows.Storage.ApplicationData.Current.LocalSettings;
        if (value is null)
            settings.Values.Remove(key);
        else
            settings.Values[key] = value;
    }

    // ── Messages ─────────────────────────────────────────────────

    private async Task LoadHistoryAsync(Guid channelId)
    {
        _chatBubblePoolUsed = 0;
        MessagesPanel.Children.Clear();
        var api = App.Services!.GetRequiredService<SharpClawApiClient>();

        try
        {
            // No history for thread-less (one-off) mode — each message is isolated
            if (_selectedThreadId is not { } tid) return;

            var url = $"/channels/{channelId}/chat/threads/{tid}";
            using var resp = await api.GetAsync(url);
            if (!resp.IsSuccessStatusCode) return;

            // Stream-deserialize: avoids materializing the full JSON as an
            // intermediate string — the deserializer reads directly from the
            // HTTP response stream.
            using var contentStream = await resp.Content.ReadAsStreamAsync();
            var messages = await JsonSerializer.DeserializeAsync<List<ChatMessageDto>>(
                contentStream, Json);

            if (messages is null) return;

            var fallbackAgentName = _allAgents.FirstOrDefault(a => a.Id == _selectedAgentId)?.Name;
            foreach (var msg in messages)
            {
                var isUser = msg.Role.Equals("user", StringComparison.OrdinalIgnoreCase);
                var senderName = isUser
                    ? msg.SenderUsername
                    : (msg.SenderAgentName ?? fallbackAgentName);
                var agentId = isUser ? null : (msg.SenderAgentId ?? _selectedAgentId);
                AppendMessage(msg.Role, msg.Content, msg.Timestamp,
                    senderName: senderName,
                    agentId: agentId,
                    senderUserId: msg.SenderUserId,
                    clientType: msg.ClientType);
            }
        }
        catch { /* swallow */ }

        ScrollToBottom();
    }

    private ChatBubbleRow AcquireChatBubble()
    {
        if (_chatBubblePoolUsed < _chatBubblePool.Count)
            return _chatBubblePool[_chatBubblePoolUsed++];

        var role = new TextBlock
        {
            FontFamily = _monoFont,
            FontSize = 10,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
        };

        var time = new TextBlock
        {
            FontFamily = _monoFont,
            FontSize = 10,
            Foreground = Brush(0x444444),
        };

        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        header.Children.Add(role);
        header.Children.Add(time);

        var content = new TextBlock
        {
            FontFamily = _monoFont,
            FontSize = 13,
            Foreground = Brush(0xCCCCCC),
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
        };

        var stack = new StackPanel { Spacing = 4 };
        stack.Children.Add(header);
        stack.Children.Add(content);

        var root = new Border
        {
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 8, 12, 8),
            MaxWidth = 600,
            Margin = new Thickness(0, 2, 0, 2),
            Child = stack,
        };

        var entry = new ChatBubbleRow(root, role, time, content);
        _chatBubblePool.Add(entry);
        _chatBubblePoolUsed++;
        return entry;
    }

    private void AppendMessage(string role, string content, DateTimeOffset? timestamp,
        string? senderName = null, Guid? agentId = null,
        Guid? senderUserId = null, string? clientType = null)
    {
        var isUser = role.Equals("user", StringComparison.OrdinalIgnoreCase);
        var isSystem = role.Equals("system", StringComparison.OrdinalIgnoreCase);
        var isCurrentUser = isUser && senderUserId.HasValue && senderUserId.Value == _currentUserId;
        var row = AcquireChatBubble();

        row.Root.Background = Brush(isSystem ? 0x111111 : isUser ? (isCurrentUser ? 0x1A2A1A : 0x1A1A2A) : 0x1A1A1A);
        row.Root.HorizontalAlignment = isSystem ? HorizontalAlignment.Center
            : isUser ? HorizontalAlignment.Right : HorizontalAlignment.Left;

        var label = isUser
            ? (isCurrentUser ? "you" : (senderName ?? "user"))
            : isSystem ? "system" : (senderName ?? "assistant");
        if (isUser && clientType is not null)
            label += $" - ({clientType})";
        row.Role.Text = label;
        row.Role.Foreground = Brush(isSystem ? 0x777777 : isUser ? (isCurrentUser ? 0x00FF00 : 0x4488FF) : 0x00AAFF);

        if (timestamp.HasValue)
        {
            row.Time.Text = timestamp.Value.LocalDateTime.ToString("HH:mm");
            row.Time.Visibility = Visibility.Visible;
        }
        else
        {
            row.Time.Visibility = Visibility.Collapsed;
        }

        row.Content.Text = content;
        row.Content.Foreground = Brush(isSystem ? 0x999999 : 0xCCCCCC);

        // No context flyout for system messages
        row.Root.ContextFlyout = isSystem ? null : BuildRoleMenuFlyout(isUser, agentId);

        MessagesPanel.Children.Add(row.Root);
    }

    private void ScrollToBottom()
    {
        MessagesScroller.UpdateLayout();
        MessagesScroller.ChangeView(null, MessagesScroller.ScrollableHeight, null);
    }

    // ── Send ─────────────────────────────────────────────────────

    private void OnMessageKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter && !string.IsNullOrWhiteSpace(MessageInput.Text))
        {
            e.Handled = true;
            _ = SendMessageAsync();
        }
    }

    private void OnSendClick(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(MessageInput.Text))
            _ = SendMessageAsync();
    }

    private async Task SendMessageAsync()
    {
        var text = MessageInput.Text.Trim();
        if (string.IsNullOrEmpty(text)) return;

        MessageInput.Text = string.Empty;
        var api = App.Services!.GetRequiredService<SharpClawApiClient>();

        // Auto-create channel + thread when none is selected
        if (_selectedChannelId is null)
        {
            try
            {
                var title = Truncate(text, 10);
                var chBody = JsonSerializer.Serialize(new { title, agentId = _selectedAgentId }, Json);
                var chContent = new StringContent(chBody, Encoding.UTF8, "application/json");
                var chResp = await api.PostAsync("/channels", chContent);
                if (!chResp.IsSuccessStatusCode)
                {
                    AppendMessage("assistant",
                        $"✗ Failed to create channel: {(int)chResp.StatusCode} {chResp.ReasonPhrase}",
                        DateTimeOffset.Now);
                    return;
                }

                using var chCreateStream = await chResp.Content.ReadAsStreamAsync();
                using var chDoc = await JsonDocument.ParseAsync(chCreateStream);
                var chId = chDoc.RootElement.GetProperty("id").GetGuid();
                var chTitle = chDoc.RootElement.GetProperty("title").GetString() ?? title;

                var thBody = JsonSerializer.Serialize(new { name = "Default" }, Json);
                var thContent = new StringContent(thBody, Encoding.UTF8, "application/json");
                var thResp = await api.PostAsync($"/channels/{chId}/threads", thContent);
                if (thResp.IsSuccessStatusCode)
                {
                    using var thCreateStream = await thResp.Content.ReadAsStreamAsync();
                    using var thDoc = await JsonDocument.ParseAsync(thCreateStream);
                    _selectedThreadId = thDoc.RootElement.GetProperty("id").GetGuid();
                }

                _selectedChannelId = chId;
                ChatTitleBlock.Text = $"# {chTitle}";
                ChannelTabBar.Visibility = Visibility.Visible;
                await LoadSidebarAsync();
                await LoadAgentsAsync(_selectedAgentId, null);
                await LoadThreadsAsync(chId);
                await LoadJobsAsync(chId);
                UpdateCursor();
            }
            catch (Exception ex)
            {
                AppendMessage("assistant", $"✗ {ex.Message}", DateTimeOffset.Now);
                return;
            }
        }

        // Auto-create thread when the "+" button was pressed
        if (_pendingNewThread && _selectedChannelId is { } pendingChId)
        {
            _pendingNewThread = false;
            try
            {
                var threadName = Truncate(text, 10);
                var thBody = JsonSerializer.Serialize(new { name = threadName }, Json);
                var thContent = new StringContent(thBody, Encoding.UTF8, "application/json");
                var thResp = await api.PostAsync($"/channels/{pendingChId}/threads", thContent);
                if (thResp.IsSuccessStatusCode)
                {
                    using var pendingThStream = await thResp.Content.ReadAsStreamAsync();
                    var thread = await JsonSerializer.DeserializeAsync<ThreadDto>(pendingThStream, Json);
                    if (thread is not null)
                    {
                        _selectedThreadId = thread.Id;

                        // Replace pending placeholder with the real thread item
                        _suppressThreadSelection = true;
                        RemovePendingThreadPlaceholder();
                        var newItem = new ComboBoxItem { Content = thread.Name, Tag = thread.Id };
                        newItem.PointerEntered += (_, _) => Cursor.SetCommand($"sharpclaw thread select {thread.Id} ");
                        newItem.PointerExited += (_, _) => UpdateCursor();
                        ThreadSelector.Items.Add(newItem);
                        ThreadSelector.SelectedItem = newItem;
                        OneOffWarning.Visibility = Visibility.Collapsed;
                        _suppressThreadSelection = false;
                    }
                }
                else
                {
                    AppendMessage("assistant",
                        $"✗ Failed to create thread: {(int)thResp.StatusCode} {thResp.ReasonPhrase}",
                        DateTimeOffset.Now);
                    return;
                }
            }
            catch (Exception ex)
            {
                AppendMessage("assistant", $"✗ Failed to create thread: {ex.Message}", DateTimeOffset.Now);
                return;
            }
        }

        var channelId = _selectedChannelId!.Value;

        var agentName = _allAgents.FirstOrDefault(a => a.Id == _selectedAgentId)?.Name;
        AppendMessage("user", text, DateTimeOffset.Now,
            senderName: _currentUsername,
            senderUserId: _currentUserId,
            clientType: DetectedClientTypeName);
        ScrollToBottom();

        UpdateCursor(text);

        var streamBubble = AcquireChatBubble();
        streamBubble.Root.Background = Brush(0x1A1A1A);
        streamBubble.Root.HorizontalAlignment = HorizontalAlignment.Left;
        streamBubble.Role.Text = agentName ?? "assistant";
        streamBubble.Role.Foreground = Brush(0x00AAFF);
        streamBubble.Time.Text = DateTimeOffset.Now.LocalDateTime.ToString("HH:mm");
        streamBubble.Time.Visibility = Visibility.Visible;
        streamBubble.Content.Text = "▍";
        streamBubble.Content.Foreground = Brush(0xCCCCCC);
        streamBubble.Root.ContextFlyout = BuildRoleMenuFlyout(isUser: false, _selectedAgentId);
        MessagesPanel.Children.Add(streamBubble.Root);
        var assistantContent = streamBubble.Content;
        ScrollToBottom();

        // Reuse the pooled StringBuilder; trim if a previous response
        // inflated it beyond 32 KB.
        _pooledStreamBuilder.Clear();
        if (_pooledStreamBuilder.Capacity > 32 * 1024)
            _pooledStreamBuilder.Capacity = 4096;
        var accumulated = _pooledStreamBuilder;
        var dispatcher = DispatcherQueue;

        try
        {
            var body = JsonSerializer.Serialize(new { message = text, agentId = _selectedAgentId, clientType = _clientType }, Json);
            var content = new StringContent(body, Encoding.UTF8, "application/json");
            var streamUrl = _selectedThreadId is { } tid
                ? $"/channels/{channelId}/chat/threads/{tid}/stream"
                : $"/channels/{channelId}/chat/stream";

            using var resp = await api.PostStreamAsync(streamUrl, content);

            if (!resp.IsSuccessStatusCode)
            {
                assistantContent.Text = $"✗ Error {(int)resp.StatusCode}: {resp.ReasonPhrase}";
                assistantContent.Foreground = Brush(0xFF4444);
                return;
            }

            using var stream = await resp.Content.ReadAsStreamAsync();
            using var reader = new StreamReader(stream);

            ReadOnlyMemory<char> eventTypeMem = default;
            var lastWasToolEvent = false;
            // Track accumulated length at last UI flush so we only snapshot
            // the StringBuilder when new content has actually been appended.
            var lastFlushedLength = 0;

            await Task.Run(async () =>
            {
                while (true)
                {
                    var line = await reader.ReadLineAsync();
                    if (line is null) break;

                    if (line.StartsWith("event: "))
                    {
                        // Slice avoids a substring allocation; kept as Memory
                        // so it survives across the next ReadLineAsync.
                        eventTypeMem = line.AsMemory(7);
                    }
                    else if (line.StartsWith("data: ") && eventTypeMem.Length > 0)
                    {
                        var evtSpan = eventTypeMem.Span;

                        if (evtSpan.SequenceEqual("TextDelta"))
                        {
                            using var doc = JsonDocument.Parse(line.AsMemory(6));
                            if (doc.RootElement.TryGetProperty("delta", out var dp)
                                && dp.GetString() is { } delta)
                            {
                                if (lastWasToolEvent)
                                {
                                    accumulated.Append('\n');
                                    lastWasToolEvent = false;
                                }
                                accumulated.Append(delta);

                                // Only create a snapshot string when the builder
                                // has grown since the last flush — avoids an
                                // allocation when the delta was empty or when
                                // back-to-back events arrive before the UI
                                // thread picks up the previous enqueue.
                                if (accumulated.Length > lastFlushedLength)
                                {
                                    lastFlushedLength = accumulated.Length;
                                    var snapshot = accumulated.ToString();
                                    dispatcher.TryEnqueue(() =>
                                    {
                                        assistantContent.Text = snapshot + "▍";
                                        ScrollToBottom();
                                    });
                                }
                            }
                        }
                        else if (evtSpan.SequenceEqual("ToolCallStart"))
                        {
                            using var doc = JsonDocument.Parse(line.AsMemory(6));
                            var action = "tool";
                            if (doc.RootElement.TryGetProperty("job", out var jobProp)
                                && jobProp.TryGetProperty("actionType", out var atProp))
                                action = atProp.GetString() ?? "tool";
                            accumulated.Append($"\n⚙ [{action}] running…");
                            lastWasToolEvent = true;
                            lastFlushedLength = accumulated.Length;
                            var snap = accumulated.ToString();
                            dispatcher.TryEnqueue(() =>
                            {
                                assistantContent.Text = snap + "▍";
                                ScrollToBottom();
                            });
                        }
                        else if (evtSpan.SequenceEqual("ToolCallResult"))
                        {
                            using var doc = JsonDocument.Parse(line.AsMemory(6));
                            var status = "done";
                            if (doc.RootElement.TryGetProperty("result", out var resProp)
                                && resProp.TryGetProperty("status", out var stProp))
                                status = stProp.GetString() ?? "done";
                            accumulated.Append($" → {status}");
                            lastWasToolEvent = true;
                            lastFlushedLength = accumulated.Length;
                            var snap = accumulated.ToString();
                            dispatcher.TryEnqueue(() =>
                            {
                                assistantContent.Text = snap + "▍";
                                ScrollToBottom();
                            });
                        }
                        else if (evtSpan.SequenceEqual("ApprovalRequired"))
                        {
                            using var doc = JsonDocument.Parse(line.AsMemory(6));
                            var action = "action";
                            if (doc.RootElement.TryGetProperty("pendingJob", out var pjProp)
                                && pjProp.TryGetProperty("actionType", out var atProp2))
                                action = atProp2.GetString() ?? "action";
                            accumulated.Append($"\n⏳ [{action}] awaiting approval…");
                            lastWasToolEvent = true;
                            lastFlushedLength = accumulated.Length;
                            var snap = accumulated.ToString();
                            dispatcher.TryEnqueue(() =>
                            {
                                assistantContent.Text = snap + "▍";
                                ScrollToBottom();
                            });
                        }
                        else if (evtSpan.SequenceEqual("ApprovalResult"))
                        {
                            using var doc = JsonDocument.Parse(line.AsMemory(6));
                            var status = "resolved";
                            if (doc.RootElement.TryGetProperty("approvalOutcome", out var aoProp)
                                && aoProp.TryGetProperty("status", out var stProp2))
                                status = stProp2.GetString() ?? "resolved";
                            accumulated.Append($" → {status}");
                            lastWasToolEvent = true;
                            lastFlushedLength = accumulated.Length;
                            var snap = accumulated.ToString();
                            dispatcher.TryEnqueue(() =>
                            {
                                assistantContent.Text = snap + "▍";
                                ScrollToBottom();
                            });
                        }
                        else if (evtSpan.SequenceEqual("Error"))
                        {
                            using var doc = JsonDocument.Parse(line.AsMemory(6));
                            if (doc.RootElement.TryGetProperty("error", out var ep))
                            {
                                var errorMsg = ep.GetString();
                                dispatcher.TryEnqueue(() =>
                                {
                                    assistantContent.Text = $"✗ {errorMsg}";
                                    assistantContent.Foreground = Brush(0xFF4444);
                                });
                            }
                        }
                        else if (evtSpan.SequenceEqual("Done"))
                        {
                            var finalText = accumulated.Length > 0
                                ? accumulated.ToString()
                                : "(empty response)";
                            dispatcher.TryEnqueue(() =>
                            {
                                assistantContent.Text = finalText;
                            });
                        }

                        eventTypeMem = default;
                    }
                }
            });

            // Clean up trailing cursor if stream ended without a Done event
            if (assistantContent.Text.EndsWith("▍"))
                assistantContent.Text = accumulated.Length > 0
                    ? accumulated.ToString()
                    : "(empty response)";
        }
        catch (Exception ex)
        {
            assistantContent.Text = $"✗ {ex.Message}";
            assistantContent.Foreground = Brush(0xFF4444);
        }
        finally
        {
            ScrollToBottom();
            UpdateCursor();
            DispatcherQueue.TryEnqueue(() => MessageInput.Focus(FocusState.Programmatic));
        }
    }

    // ── Channel Settings ─────────────────────────────────────────

    private static readonly (string ApiName, string DisplayName)[] _resourceAccessTypes =
    [
        ("dangerousShellAccesses", "Dangerous Shell"),
        ("safeShellAccesses", "Safe Shell"),
        ("containerAccesses", "Containers"),
        ("websiteAccesses", "Websites"),
        ("searchEngineAccesses", "Search Engines"),
        ("localInfoStoreAccesses", "Local Info Stores"),
        ("externalInfoStoreAccesses", "External Info Stores"),
        ("audioDeviceAccesses", "Audio Devices"),
        ("displayDeviceAccesses", "Display Devices"),
        ("editorSessionAccesses", "Editor Sessions"),
        ("agentAccesses", "Agent Management"),
        ("taskAccesses", "Task Management"),
        ("skillAccesses", "Skill Management"),
    ];

    private static readonly string[] _globalFlagNames =
        ["canCreateSubAgents", "canCreateContainers", "canRegisterInfoStores",
         "canAccessLocalhostInBrowser", "canAccessLocalhostCli",
         "canClickDesktop", "canTypeOnDesktop"];

    private static readonly Dictionary<string, string> _globalFlagTooltips = new()
    {
        ["canCreateSubAgents"] = "Allow the agent to spawn child agents on its own",
        ["canCreateContainers"] = "Allow the agent to create sandboxed execution containers",
        ["canRegisterInfoStores"] = "Allow the agent to register local or external information stores",
        ["canAccessLocalhostInBrowser"] = "Allow the agent to open localhost URLs in a headless browser",
        ["canAccessLocalhostCli"] = "Allow the agent to make direct HTTP requests to localhost",
        ["canClickDesktop"] = "Allow the agent to simulate mouse clicks on the desktop",
        ["canTypeOnDesktop"] = "Allow the agent to simulate keyboard input on the desktop",
    };

    private static readonly Dictionary<string, string> _resourceAccessTooltips = new()
    {
        ["dangerousShellAccesses"] = "Unrestricted shell commands \u2014 use with extreme caution",
        ["safeShellAccesses"] = "Shell commands restricted to the mk8.shell allowlist",
        ["containerAccesses"] = "Access to sandboxed execution containers",
        ["websiteAccesses"] = "Access to registered website resources",
        ["searchEngineAccesses"] = "Access to registered search engine resources",
        ["localInfoStoreAccesses"] = "Access to local information store files",
        ["externalInfoStoreAccesses"] = "Access to external information store endpoints",
        ["audioDeviceAccesses"] = "Access to audio capture devices for transcription",
        ["displayDeviceAccesses"] = "Access to display devices for screen capture",
        ["editorSessionAccesses"] = "Access to IDE editor sessions via the editor bridge",
        ["agentAccesses"] = "Manage other agents (create, update, delete)",
        ["taskAccesses"] = "Manage scheduled tasks and jobs",
        ["skillAccesses"] = "Access registered skills and their definitions",
    };

    private static readonly Guid AllResourcesId = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff");

    // Settings UI element references (populated during BuildPermissionsUI)
    private readonly Dictionary<string, CheckBox> _settingsGlobalFlags = new(7);
    private readonly Dictionary<string, StackPanel> _settingsResourcePanels = new(13);

    private void OnTabChatClick(object sender, RoutedEventArgs e)
    {
        if (!_settingsMode) return;
        _settingsMode = false;
        UpdateTabHighlight();
        SettingsScroller.Visibility = Visibility.Collapsed;
        AgentSelectorPanel.Visibility = Visibility.Visible;
        if (_selectedChannelId is not null)
        {
            ThreadSelectorPanel.Visibility = Visibility.Visible;
            JobSelectorPanel.Visibility = Visibility.Visible;
        }
        OneOffWarning.Visibility = _selectedThreadId is null && !_pendingNewThread && _selectedChannelId is not null
            ? Visibility.Visible : Visibility.Collapsed;
        if (_selectedJobId is not null)
            _ = ShowJobViewAsync(_selectedJobId.Value);
        else
            ShowChatView();
        UpdateMicState();
    }

    private async void OnTabSettingsClick(object sender, RoutedEventArgs e)
    {
        if (_settingsMode || _selectedChannelId is null) return;
        _settingsMode = true;
        UpdateTabHighlight();
        MessagesScroller.Visibility = Visibility.Collapsed;
        ChatInputArea.Visibility = Visibility.Collapsed;
        JobViewPanel.Visibility = Visibility.Collapsed;
        AgentSelectorPanel.Visibility = Visibility.Collapsed;
        ThreadSelectorPanel.Visibility = Visibility.Collapsed;
        JobSelectorPanel.Visibility = Visibility.Collapsed;
        OneOffWarning.Visibility = Visibility.Collapsed;
        SettingsScroller.Visibility = Visibility.Visible;
        await LoadChannelSettingsAsync(_selectedChannelId.Value);
    }

    private void UpdateTabHighlight()
    {
        if (TabChatButton.Content is TextBlock c) c.Foreground = Brush(_settingsMode ? 0x666666 : 0x00FF00);
        if (TabSettingsButton.Content is TextBlock s) s.Foreground = Brush(_settingsMode ? 0x00FF00 : 0x666666);
    }

    private async Task LoadChannelSettingsAsync(Guid channelId)
    {
        SettingsPanel.Children.Clear();
        _settingsGlobalFlags.Clear();
        _settingsResourcePanels.Clear();

        var api = App.Services!.GetRequiredService<SharpClawApiClient>();

        bool disableChatHeader = false;
        List<(Guid Id, string Name, string ProviderModel)> allowedAgents = [];
        Guid? channelPermSetId = null;
        Guid? channelDefaultAgentId = null;

        try
        {
            using var chResp = await api.GetAsync($"/channels/{channelId}");
            if (!chResp.IsSuccessStatusCode)
            {
                AddSettingsLabel("✗ Failed to load channel settings", 0xFF4444);
                return;
            }

            using var chStream = await chResp.Content.ReadAsStreamAsync();
            using var chDoc = await JsonDocument.ParseAsync(chStream);
            var root = chDoc.RootElement;

            disableChatHeader = root.TryGetProperty("disableChatHeader", out var dch) && dch.GetBoolean();
            if (root.TryGetProperty("permissionSetId", out var psi) && psi.ValueKind == JsonValueKind.String)
                channelPermSetId = psi.GetGuid();

            if (root.TryGetProperty("agent", out var agentProp) && agentProp.ValueKind == JsonValueKind.Object
                && agentProp.TryGetProperty("id", out var defAgentId) && defAgentId.ValueKind == JsonValueKind.String)
                channelDefaultAgentId = defAgentId.GetGuid();

            if (root.TryGetProperty("allowedAgents", out var aaProp) && aaProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in aaProp.EnumerateArray())
                {
                    if (a.TryGetProperty("id", out var idP) && idP.ValueKind == JsonValueKind.String)
                    {
                        var id = idP.GetGuid();
                        var name = a.TryGetProperty("name", out var np) ? np.GetString() ?? "?" : "?";
                        var model = a.TryGetProperty("modelName", out var mp) ? mp.GetString() ?? "" : "";
                        var provider = a.TryGetProperty("providerName", out var pp) ? pp.GetString() ?? "" : "";
                        allowedAgents.Add((id, name, $"{provider}/{model}"));
                    }
                }
            }
        }
        catch
        {
            AddSettingsLabel("✗ Failed to load settings", 0xFF4444);
            return;
        }

        Guid? permRoleId = null;
        PermSettingsData? permData = null;

        if (channelPermSetId is not null)
        {
            permRoleId = _allRoles.FirstOrDefault(r => r.PermissionSetId == channelPermSetId)?.Id;
            if (permRoleId is not null)
            {
                try
                {
                    using var permResp = await api.GetAsync($"/roles/{permRoleId}/permissions");
                    if (permResp.IsSuccessStatusCode)
                    {
                        using var pStream = await permResp.Content.ReadAsStreamAsync();
                        using var pDoc = await JsonDocument.ParseAsync(pStream);
                        permData = ParsePermissions(pDoc.RootElement);
                    }
                }
                catch { /* swallow */ }
            }
        }

        // Fetch resource lookups for all access types in parallel
        _resourceLookupCache.Clear();
        HashSet<Guid> transcriptionModelIds = [];
        try
        {
            var lookupTasks = _resourceAccessTypes.Select(async t =>
            {
                try
                {
                    using var resp = await api.GetAsync($"/resources/lookup/{t.ApiName}");
                    if (resp.IsSuccessStatusCode)
                    {
                        using var s = await resp.Content.ReadAsStreamAsync();
                        var items = await JsonSerializer.DeserializeAsync<List<ResourceItemDto>>(s, Json);
                        return (t.ApiName, Items: items ?? []);
                    }
                }
                catch { /* swallow */ }
                return (t.ApiName, Items: new List<ResourceItemDto>());
            });
            foreach (var (apiName, items) in await Task.WhenAll(lookupTasks))
                _resourceLookupCache[apiName] = items;
        }
        catch { /* swallow — settings still work without lookups */ }

        // Fetch models to determine which agents use transcription-capable models
        try
        {
            using var modelsResp = await api.GetAsync("/models");
            if (modelsResp.IsSuccessStatusCode)
            {
                using var modelsStream = await modelsResp.Content.ReadAsStreamAsync();
                using var modelsDoc = await JsonDocument.ParseAsync(modelsStream);
                foreach (var m in modelsDoc.RootElement.EnumerateArray())
                {
                    if (m.TryGetProperty("capabilities", out var cap)
                        && cap.GetString() is { } capStr
                        && capStr.Contains("Transcription", StringComparison.OrdinalIgnoreCase))
                    {
                        if (m.TryGetProperty("id", out var mid) && mid.ValueKind == JsonValueKind.String)
                            transcriptionModelIds.Add(mid.GetGuid());
                    }
                }
            }
        }
        catch { /* swallow — all agents shown if models unavailable */ }

        BuildSettingsPanel(channelId, disableChatHeader, allowedAgents, channelDefaultAgentId, permRoleId, permData, transcriptionModelIds);
    }

    private static PermSettingsData ParsePermissions(JsonElement root)
    {
        var data = new PermSettingsData();

        foreach (var flag in _globalFlagNames)
            data.GlobalFlags[flag] = root.TryGetProperty(flag, out var fp) && fp.GetBoolean();

        foreach (var (apiName, _) in _resourceAccessTypes)
        {
            var grants = new List<Guid>();
            if (root.TryGetProperty(apiName, out var ap) && ap.ValueKind == JsonValueKind.Array)
            {
                foreach (var g in ap.EnumerateArray())
                {
                    if (g.TryGetProperty("resourceId", out var rid) && rid.ValueKind == JsonValueKind.String)
                        grants.Add(rid.GetGuid());
                }
            }
            data.ResourceAccesses[apiName] = grants;
        }
        return data;
    }

    private void BuildSettingsPanel(
        Guid channelId,
        bool disableChatHeader,
        List<(Guid Id, string Name, string ProviderModel)> allowedAgents,
        Guid? channelDefaultAgentId,
        Guid? permRoleId,
        PermSettingsData? permData,
        HashSet<Guid> transcriptionModelIds)
    {
        SettingsPanel.Children.Clear();

        // ── General ──
        AddSettingsSection("General", "Basic channel configuration");

        var headerRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        headerRow.Children.Add(new TextBlock
        {
            Text = "disable chat header:",
            FontFamily = _monoFont, FontSize = 11,
            Foreground = Brush(0xCCCCCC),
            VerticalAlignment = VerticalAlignment.Center,
        });
        var toggle = new ToggleSwitch { IsOn = disableChatHeader, OnContent = "yes", OffContent = "no" };
        ToolTipService.SetToolTip(toggle, "Suppress the metadata header (time, user, role, bio) prepended to each message sent to the model");
        toggle.Toggled += async (_, _) =>
        {
            var api = App.Services!.GetRequiredService<SharpClawApiClient>();
            try
            {
                var body = JsonSerializer.Serialize(new { disableChatHeader = toggle.IsOn }, Json);
                await api.PutAsync($"/channels/{channelId}",
                    new StringContent(body, Encoding.UTF8, "application/json"));
            }
            catch { /* swallow */ }
        };
        headerRow.Children.Add(toggle);
        SettingsPanel.Children.Add(headerRow);

        // ── Allowed Agents ──
        AddSettingsSection("Allowed Agents", "Additional agents permitted to respond in this channel besides the default");

        var agentsList = new StackPanel { Spacing = 4 };
        foreach (var agent in allowedAgents)
        {
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var lbl = new TextBlock
            {
                Text = $"• {agent.Name}  ({agent.ProviderModel})",
                FontFamily = _monoFont, FontSize = 12,
                Foreground = Brush(0xE0E0E0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(lbl, 0);
            row.Children.Add(lbl);

            var rmBtn = new Button
            {
                Content = new TextBlock { Text = "✕", FontFamily = _monoFont, FontSize = 10, Foreground = Brush(0xFF4444) },
                Background = _brushTransparent, BorderThickness = new Thickness(0),
                Padding = new Thickness(4, 2, 4, 2), MinWidth = 0, MinHeight = 0,
                Tag = agent.Id,
            };
            rmBtn.Click += async (s, _) =>
            {
                if (s is Button { Tag: Guid aid })
                {
                    var api = App.Services!.GetRequiredService<SharpClawApiClient>();
                    try { await api.DeleteAsync($"/channels/{channelId}/agents/{aid}"); }
                    catch { /* swallow */ }
                    await ReloadSettingsAndAgentsAsync(channelId);
                }
            };
            Grid.SetColumn(rmBtn, 1);
            row.Children.Add(rmBtn);
            agentsList.Children.Add(row);
        }

        if (allowedAgents.Count == 0)
            agentsList.Children.Add(new TextBlock
            {
                Text = "(no additional agents)",
                FontFamily = _monoFont, FontSize = 11,
                Foreground = Brush(0x777777),
                FontStyle = Windows.UI.Text.FontStyle.Italic,
            });
        SettingsPanel.Children.Add(agentsList);

        var currentIds = new HashSet<Guid>(allowedAgents.Select(a => a.Id));
        var availableAgents = _allAgents.Where(a => !currentIds.Contains(a.Id)).ToList();
        var agentDisplayMap = new Dictionary<string, Guid>(availableAgents.Count);
        foreach (var a in availableAgents)
            agentDisplayMap[$"{a.Name}  ({a.ProviderName}/{a.ModelName})"] = a.Id;

        var agentSearch = new AutoSuggestBox
        {
            PlaceholderText = "Search agents to add\u2026",
            FontFamily = _monoFont, FontSize = 11,
            MinWidth = 300,
            Margin = new Thickness(0, 4, 0, 0),
        };
        ToolTipService.SetToolTip(agentSearch, "Type to filter, then click a suggestion to add the agent");

        agentSearch.TextChanged += (sender, args) =>
        {
            if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
            var q = sender.Text.Trim();
            sender.ItemsSource = string.IsNullOrEmpty(q)
                ? agentDisplayMap.Keys.ToList()
                : agentDisplayMap.Keys
                    .Where(k => k.Contains(q, StringComparison.OrdinalIgnoreCase))
                    .ToList();
        };

        agentSearch.QuerySubmitted += async (sender, args) =>
        {
            var chosen = args.ChosenSuggestion?.ToString();
            if (chosen is null || !agentDisplayMap.TryGetValue(chosen, out var aid)) return;
            sender.Text = string.Empty;
            var api = App.Services!.GetRequiredService<SharpClawApiClient>();
            try
            {
                var body = JsonSerializer.Serialize(new { agentId = aid }, Json);
                await api.PostAsync($"/channels/{channelId}/agents",
                    new StringContent(body, Encoding.UTF8, "application/json"));
            }
            catch { /* swallow */ }
            await ReloadSettingsAndAgentsAsync(channelId);
        };

        SettingsPanel.Children.Add(agentSearch);

        // ── Transcription Agent (for mic button) ──
        AddSettingsSection("Transcription Agent", "Agent used for voice-to-text input via the microphone button (must be the channel default or an allowed agent)");

        // Only show agents that are eligible for this channel AND have transcription-capable models.
        var channelAgentIds = new HashSet<Guid>(allowedAgents.Select(a => a.Id));
        if (channelDefaultAgentId is { } defId)
            channelAgentIds.Add(defId);
        var txAgents = _allAgents
            .Where(a => channelAgentIds.Contains(a.Id)
                     && (transcriptionModelIds.Count == 0 || transcriptionModelIds.Contains(a.ModelId)))
            .ToList();
        var txDisplayMap = new Dictionary<string, Guid>(txAgents.Count);
        foreach (var a in txAgents)
            txDisplayMap.TryAdd($"{a.Name}  ({a.ProviderName}/{a.ModelName})", a.Id);

        var savedTxAgent = LoadLocalSetting(TranscriptionAgentKey);
        AgentDto? currentTxAgent = null;
        if (savedTxAgent is not null && Guid.TryParse(savedTxAgent, out var savedTxId))
            currentTxAgent = _allAgents.FirstOrDefault(a => a.Id == savedTxId);

        var txCurrentRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        txCurrentRow.Children.Add(new TextBlock
        {
            Text = "current:",
            FontFamily = _monoFont, FontSize = 11,
            Foreground = Brush(0xCCCCCC),
            VerticalAlignment = VerticalAlignment.Center,
        });
        var txCurrentLabel = new TextBlock
        {
            Text = currentTxAgent is not null
                ? $"{currentTxAgent.Name}  ({currentTxAgent.ProviderName}/{currentTxAgent.ModelName})"
                : "(none)",
            FontFamily = _monoFont, FontSize = 11,
            Foreground = Brush(currentTxAgent is not null ? 0xE0E0E0 : 0x777777),
            FontStyle = currentTxAgent is null ? Windows.UI.Text.FontStyle.Italic : Windows.UI.Text.FontStyle.Normal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        txCurrentRow.Children.Add(txCurrentLabel);

        if (currentTxAgent is not null)
        {
            var txClearBtn = new Button
            {
                Content = new TextBlock { Text = "\u2715", FontFamily = _monoFont, FontSize = 10, Foreground = Brush(0xFF4444) },
                Background = _brushTransparent, BorderThickness = new Thickness(0),
                Padding = new Thickness(4, 2, 4, 2), MinWidth = 0, MinHeight = 0,
            };
            txClearBtn.Click += (_, _) =>
            {
                SaveLocalSetting(TranscriptionAgentKey, null);
                txCurrentLabel.Text = "(none)";
                txCurrentLabel.Foreground = Brush(0x777777);
                txCurrentLabel.FontStyle = Windows.UI.Text.FontStyle.Italic;
                if (txCurrentRow.Children.Count > 2)
                    txCurrentRow.Children.RemoveAt(2);
                UpdateMicState();
            };
            txCurrentRow.Children.Add(txClearBtn);
        }

        SettingsPanel.Children.Add(txCurrentRow);

        var txSearch = new AutoSuggestBox
        {
            PlaceholderText = txAgents.Count > 0
                ? "Search transcription agents..."
                : "No transcription-capable agents available",
            FontFamily = _monoFont, FontSize = 11,
            MinWidth = 300,
            Margin = new Thickness(0, 4, 0, 0),
            IsEnabled = txAgents.Count > 0,
        };
        ToolTipService.SetToolTip(txSearch, "Type to filter agents with transcription-capable models");

        txSearch.TextChanged += (sender, args) =>
        {
            if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
            var q = sender.Text.Trim();
            sender.ItemsSource = string.IsNullOrEmpty(q)
                ? txDisplayMap.Keys.ToList()
                : txDisplayMap.Keys
                    .Where(k => k.Contains(q, StringComparison.OrdinalIgnoreCase))
                    .ToList();
        };

        txSearch.QuerySubmitted += (sender, args) =>
        {
            var chosen = args.ChosenSuggestion?.ToString();
            if (chosen is null || !txDisplayMap.TryGetValue(chosen, out var aid)) return;
            sender.Text = string.Empty;
            SaveLocalSetting(TranscriptionAgentKey, aid.ToString());
            txCurrentLabel.Text = chosen;
            txCurrentLabel.Foreground = Brush(0xE0E0E0);
            txCurrentLabel.FontStyle = Windows.UI.Text.FontStyle.Normal;

            // Add clear button if not already present
            if (txCurrentRow.Children.Count <= 2)
            {
                var clearBtn = new Button
                {
                    Content = new TextBlock { Text = "\u2715", FontFamily = _monoFont, FontSize = 10, Foreground = Brush(0xFF4444) },
                    Background = _brushTransparent, BorderThickness = new Thickness(0),
                    Padding = new Thickness(4, 2, 4, 2), MinWidth = 0, MinHeight = 0,
                };
                clearBtn.Click += (_, _) =>
                {
                    SaveLocalSetting(TranscriptionAgentKey, null);
                    txCurrentLabel.Text = "(none)";
                    txCurrentLabel.Foreground = Brush(0x777777);
                    txCurrentLabel.FontStyle = Windows.UI.Text.FontStyle.Italic;
                    if (txCurrentRow.Children.Count > 2)
                        txCurrentRow.Children.RemoveAt(2);
                    UpdateMicState();
                };
                txCurrentRow.Children.Add(clearBtn);
            }

            UpdateMicState();
        };

        SettingsPanel.Children.Add(txSearch);

        // ── Audio Device (for mic button) ──
        AddSettingsSection("Audio Device", "Audio capture device used for voice-to-text transcription (saved locally per device)");

        var audioDevices = _resourceLookupCache.TryGetValue("audioDeviceAccesses", out var adItems) ? adItems : [];
        var adDisplayMap = new Dictionary<string, Guid>(audioDevices.Count);
        foreach (var d in audioDevices)
            adDisplayMap.TryAdd($"{d.Name}  ({d.Id.ToString()[..8]}…)", d.Id);

        var savedAdId = LoadLocalSetting(SelectedAudioDeviceKey);
        ResourceItemDto? currentAd = null;
        if (savedAdId is not null && Guid.TryParse(savedAdId, out var savedAdGuid))
            currentAd = audioDevices.FirstOrDefault(d => d.Id == savedAdGuid);

        var adCurrentRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        adCurrentRow.Children.Add(new TextBlock
        {
            Text = "current:",
            FontFamily = _monoFont, FontSize = 11,
            Foreground = Brush(0xCCCCCC),
            VerticalAlignment = VerticalAlignment.Center,
        });
        var adCurrentLabel = new TextBlock
        {
            Text = currentAd is not null
                ? $"{currentAd.Name}  ({currentAd.Id.ToString()[..8]}…)"
                : "(none)",
            FontFamily = _monoFont, FontSize = 11,
            Foreground = Brush(currentAd is not null ? 0xE0E0E0 : 0x777777),
            FontStyle = currentAd is null ? Windows.UI.Text.FontStyle.Italic : Windows.UI.Text.FontStyle.Normal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        adCurrentRow.Children.Add(adCurrentLabel);

        if (currentAd is not null)
        {
            var adClearBtn = new Button
            {
                Content = new TextBlock { Text = "\u2715", FontFamily = _monoFont, FontSize = 10, Foreground = Brush(0xFF4444) },
                Background = _brushTransparent, BorderThickness = new Thickness(0),
                Padding = new Thickness(4, 2, 4, 2), MinWidth = 0, MinHeight = 0,
            };
            adClearBtn.Click += (_, _) =>
            {
                SaveLocalSetting(SelectedAudioDeviceKey, null);
                adCurrentLabel.Text = "(none)";
                adCurrentLabel.Foreground = Brush(0x777777);
                adCurrentLabel.FontStyle = Windows.UI.Text.FontStyle.Italic;
                if (adCurrentRow.Children.Count > 2)
                    adCurrentRow.Children.RemoveAt(2);
                UpdateMicState();
            };
            adCurrentRow.Children.Add(adClearBtn);
        }

        SettingsPanel.Children.Add(adCurrentRow);

        var adSearch = new AutoSuggestBox
        {
            PlaceholderText = audioDevices.Count > 0
                ? "Search audio devices..."
                : "No audio devices available",
            FontFamily = _monoFont, FontSize = 11,
            MinWidth = 300,
            Margin = new Thickness(0, 4, 0, 0),
            IsEnabled = audioDevices.Count > 0,
        };
        ToolTipService.SetToolTip(adSearch, "Type to filter, then click a suggestion to select the audio device");

        adSearch.TextChanged += (sender, args) =>
        {
            if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
            var q = sender.Text.Trim();
            sender.ItemsSource = string.IsNullOrEmpty(q)
                ? adDisplayMap.Keys.ToList()
                : adDisplayMap.Keys
                    .Where(k => k.Contains(q, StringComparison.OrdinalIgnoreCase))
                    .ToList();
        };

        adSearch.QuerySubmitted += (sender, args) =>
        {
            var chosen = args.ChosenSuggestion?.ToString();
            if (chosen is null || !adDisplayMap.TryGetValue(chosen, out var did)) return;
            sender.Text = string.Empty;
            SaveLocalSetting(SelectedAudioDeviceKey, did.ToString());
            adCurrentLabel.Text = chosen;
            adCurrentLabel.Foreground = Brush(0xE0E0E0);
            adCurrentLabel.FontStyle = Windows.UI.Text.FontStyle.Normal;

            if (adCurrentRow.Children.Count <= 2)
            {
                var clearBtn = new Button
                {
                    Content = new TextBlock { Text = "\u2715", FontFamily = _monoFont, FontSize = 10, Foreground = Brush(0xFF4444) },
                    Background = _brushTransparent, BorderThickness = new Thickness(0),
                    Padding = new Thickness(4, 2, 4, 2), MinWidth = 0, MinHeight = 0,
                };
                clearBtn.Click += (_, _) =>
                {
                    SaveLocalSetting(SelectedAudioDeviceKey, null);
                    adCurrentLabel.Text = "(none)";
                    adCurrentLabel.Foreground = Brush(0x777777);
                    adCurrentLabel.FontStyle = Windows.UI.Text.FontStyle.Italic;
                    if (adCurrentRow.Children.Count > 2)
                        adCurrentRow.Children.RemoveAt(2);
                    UpdateMicState();
                };
                adCurrentRow.Children.Add(clearBtn);
            }

            UpdateMicState();
        };

        SettingsPanel.Children.Add(adSearch);

        // ── Channel Permissions ──
        AddSettingsSection("Channel Permissions", "Pre-authorization overrides that let the agent act without requiring user approval");

        SettingsPanel.Children.Add(new TextBlock
        {
            Text = "⚠ These are pre-approvals. The agent still needs the permission on its own role "
                 + "to perform an action — pre-approval only means you won't be asked to manually "
                 + "approve it each time.",
            FontFamily = _monoFont, FontSize = 10,
            Foreground = Brush(0xFFAA00),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 6),
            MaxWidth = 520,
        });

        BuildPermissionsUI(permRoleId, permData ?? new PermSettingsData(), channelId);
    }

    private void BuildPermissionsUI(Guid? roleId, PermSettingsData permData, Guid channelId)
    {
        // Global flags
        AddSettingsSubSection("Global Flags");
        var flagsPanel = new StackPanel { Spacing = 4 };
        _settingsGlobalFlags.Clear();
        foreach (var flag in _globalFlagNames)
        {
            var cb = new CheckBox
            {
                IsChecked = permData.GlobalFlags.GetValueOrDefault(flag),
                MinWidth = 0, MinHeight = 0,
                Padding = new Thickness(4, 0, 0, 0),
                Content = new TextBlock
                {
                    Text = FormatFlagName(flag),
                    FontFamily = _monoFont, FontSize = 11,
                    Foreground = Brush(0xE0E0E0),
                },
            };
            if (_globalFlagTooltips.TryGetValue(flag, out var flagTip))
                ToolTipService.SetToolTip(cb, flagTip);
            _settingsGlobalFlags[flag] = cb;
            flagsPanel.Children.Add(cb);
        }
        SettingsPanel.Children.Add(flagsPanel);

        // Resource accesses
        AddSettingsSubSection("Resource Accesses");
        _settingsResourcePanels.Clear();
        var resContainer = new StackPanel { Spacing = 10 };
        foreach (var (apiName, displayName) in _resourceAccessTypes)
        {
            var grants = permData.ResourceAccesses.GetValueOrDefault(apiName, []);
            var section = new StackPanel { Spacing = 2 };
            var resHeader = new TextBlock
            {
                Text = displayName,
                FontFamily = _monoFont, FontSize = 12,
                Foreground = Brush(0x00CCFF),
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            };
            if (_resourceAccessTooltips.TryGetValue(apiName, out var resTip))
                ToolTipService.SetToolTip(resHeader, resTip);
            section.Children.Add(resHeader);

            var gPanel = new StackPanel { Spacing = 2, Margin = new Thickness(12, 0, 0, 0) };
            _settingsResourcePanels[apiName] = gPanel;

            foreach (var resId in grants)
                AddGrantRow(gPanel, resId, "Independent", apiName);

            section.Children.Add(gPanel);

            // ── Action buttons row: + wildcard and + add resource ──
            var actionsRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };

            var addWc = new Button
            {
                Content = new TextBlock { Text = "+ wildcard", FontFamily = _monoFont, FontSize = 10, Foreground = Brush(0x00FF00) },
                Background = _brushTransparent, BorderThickness = new Thickness(0),
                Padding = new Thickness(12, 2, 4, 2), MinWidth = 0, MinHeight = 0,
                Tag = apiName,
            };
            addWc.Click += (s, _) =>
            {
                if (s is Button { Tag: string type } && _settingsResourcePanels.TryGetValue(type, out var panel))
                {
                    AddGrantRow(panel, AllResourcesId, "Independent", type);
                }
            };
            actionsRow.Children.Add(addWc);

            // Resource picker (only shown when the lookup returned items)
            if (_resourceLookupCache.TryGetValue(apiName, out var lookupItems) && lookupItems.Count > 0)
            {
                var capturedApiName = apiName;
                var capturedItems = lookupItems;
                var resDisplayMap = new Dictionary<string, Guid>(capturedItems.Count);
                foreach (var r in capturedItems)
                    resDisplayMap.TryAdd($"{r.Name}  ({r.Id.ToString()[..8]}…)", r.Id);

                var resSearch = new AutoSuggestBox
                {
                    PlaceholderText = "+ add resource…",
                    FontFamily = _monoFont, FontSize = 10,
                    MinWidth = 200,
                };
                ToolTipService.SetToolTip(resSearch, "Search for a specific resource to grant access to");

                resSearch.TextChanged += (sender, args) =>
                {
                    if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
                    var q = sender.Text.Trim();
                    sender.ItemsSource = string.IsNullOrEmpty(q)
                        ? resDisplayMap.Keys.ToList()
                        : resDisplayMap.Keys
                            .Where(k => k.Contains(q, StringComparison.OrdinalIgnoreCase))
                            .ToList();
                };

                resSearch.QuerySubmitted += (sender, args) =>
                {
                    var chosen = args.ChosenSuggestion?.ToString();
                    if (chosen is null || !resDisplayMap.TryGetValue(chosen, out var resId)) return;
                    sender.Text = string.Empty;
                    if (_settingsResourcePanels.TryGetValue(capturedApiName, out var panel))
                        AddGrantRow(panel, resId, "Independent", capturedApiName);
                };

                actionsRow.Children.Add(resSearch);
            }

            section.Children.Add(actionsRow);
            resContainer.Children.Add(section);
        }
        SettingsPanel.Children.Add(resContainer);

        // Save button
        var saveBtn = new Button
        {
            Content = new TextBlock { Text = "Save Permissions", FontFamily = _monoFont, FontSize = 12, Foreground = Brush(0x00FF00) },
            Background = Brush(0x1A2A1A),
            BorderBrush = Brush(0x00FF00), BorderThickness = new Thickness(1),
            Padding = new Thickness(16, 8), Margin = new Thickness(0, 8, 0, 0),
        };
        saveBtn.Click += async (_, _) => await SavePermissionsAsync(roleId, channelId);
        SettingsPanel.Children.Add(saveBtn);
    }

    private void AddGrantRow(StackPanel grantsPanel, Guid resourceId, string clearance, string? apiName = null)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };

        string idText;
        if (resourceId == AllResourcesId)
            idText = "* (all)";
        else if (apiName is not null
            && _resourceLookupCache.TryGetValue(apiName, out var lookupItems)
            && lookupItems.FirstOrDefault(r => r.Id == resourceId) is { } match)
            idText = $"{match.Name}  ({resourceId.ToString()[..8]}…)";
        else
            idText = resourceId.ToString()[..8] + "…";

        var idBlock = new TextBlock
        {
            Text = idText,
            FontFamily = _monoFont, FontSize = 11,
            Foreground = Brush(0xE0E0E0),
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 80, Tag = resourceId,
        };
        if (resourceId != AllResourcesId)
            ToolTipService.SetToolTip(idBlock, resourceId.ToString());
        row.Children.Add(idBlock);

        var rmBtn = new Button
        {
            Content = new TextBlock { Text = "✕", FontFamily = _monoFont, FontSize = 9, Foreground = Brush(0xFF4444) },
            Background = _brushTransparent, BorderThickness = new Thickness(0),
            Padding = new Thickness(2), MinWidth = 0, MinHeight = 0,
        };
        rmBtn.Click += (_, _) => grantsPanel.Children.Remove(row);
        row.Children.Add(rmBtn);
        grantsPanel.Children.Add(row);
    }

    private async Task SavePermissionsAsync(Guid? roleId, Guid channelId)
    {
        var request = new Dictionary<string, object?>();

        foreach (var (flag, cb) in _settingsGlobalFlags)
            request[flag] = cb.IsChecked == true;

        foreach (var (apiName, panel) in _settingsResourcePanels)
        {
            var grants = new List<object>();
            foreach (var child in panel.Children)
            {
                if (child is StackPanel row && row.Children.Count >= 1
                    && row.Children[0] is TextBlock { Tag: Guid resId })
                {
                    grants.Add(new { resourceId = resId, clearance = "Independent" });
                }
            }
            request[apiName] = grants;
        }

        var api = App.Services!.GetRequiredService<SharpClawApiClient>();
        try
        {
            // Channel has no permission set yet — create a dedicated role to host one
            if (roleId is null)
            {
                var createBody = JsonSerializer.Serialize(
                    new { name = $"channel-{channelId.ToString()[..8]}" }, Json);
                var createResp = await api.PostAsync("/roles",
                    new StringContent(createBody, Encoding.UTF8, "application/json"));
                if (!createResp.IsSuccessStatusCode)
                {
                    using var errStream = await createResp.Content.ReadAsStreamAsync();
                    var errMsg = await TryExtractErrorAsync(errStream)
                        ?? $"{(int)createResp.StatusCode} {createResp.ReasonPhrase}";
                    AddSettingsLabel($"✗ Failed to create permission set: {errMsg}", 0xFF4444);
                    return;
                }

                using var createStream = await createResp.Content.ReadAsStreamAsync();
                using var createDoc = await JsonDocument.ParseAsync(createStream);
                roleId = createDoc.RootElement.GetProperty("id").GetGuid();
                var permSetId = createDoc.RootElement.GetProperty("permissionSetId").GetGuid();

                // Link the new permission set to this channel
                var assignBody = JsonSerializer.Serialize(new { permissionSetId = permSetId }, Json);
                var assignResp = await api.PutAsync($"/channels/{channelId}",
                    new StringContent(assignBody, Encoding.UTF8, "application/json"));
                if (!assignResp.IsSuccessStatusCode)
                {
                    using var errStream = await assignResp.Content.ReadAsStreamAsync();
                    var errMsg = await TryExtractErrorAsync(errStream)
                        ?? $"{(int)assignResp.StatusCode} {assignResp.ReasonPhrase}";
                    AddSettingsLabel($"✗ Failed to assign permission set: {errMsg}", 0xFF4444);
                    return;
                }

                await LoadRolesAsync();
            }

            var body = JsonSerializer.Serialize(request, Json);
            var resp = await api.PutAsync($"/roles/{roleId}/permissions",
                new StringContent(body, Encoding.UTF8, "application/json"));
            if (resp.IsSuccessStatusCode)
            {
                await LoadChannelSettingsAsync(channelId);
            }
            else
            {
                using var errStream = await resp.Content.ReadAsStreamAsync();
                var errMsg = await TryExtractErrorAsync(errStream) ?? $"{(int)resp.StatusCode} {resp.ReasonPhrase}";
                AddSettingsLabel($"✗ Save failed: {errMsg}", 0xFF4444);
            }
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
        var block = new TextBlock
        {
            Text = $"── {text} ──",
            FontFamily = _monoFont, FontSize = 12,
            Foreground = Brush(0x00FF00),
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        };
        if (tooltip is not null)
            ToolTipService.SetToolTip(block, tooltip);
        SettingsPanel.Children.Add(block);
    }

    private void AddSettingsSubSection(string text) =>
        SettingsPanel.Children.Add(new TextBlock
        {
            Text = text,
            FontFamily = _monoFont, FontSize = 11,
            Foreground = Brush(0xBBBBBB),
            Margin = new Thickness(0, 4, 0, 0),
        });

    private void AddSettingsLabel(string text, int color) =>
        SettingsPanel.Children.Add(new TextBlock
        {
            Text = text,
            FontFamily = _monoFont, FontSize = 11,
            Foreground = Brush(color),
        });

    private static string FormatFlagName(string camelCase)
    {
        var s = camelCase.AsSpan();
        if (s.StartsWith("can")) s = s[3..];
        var sb = new StringBuilder(s.Length + 4);
        for (var i = 0; i < s.Length; i++)
        {
            if (i > 0 && char.IsUpper(s[i]))
                sb.Append(' ');
            sb.Append(i == 0 ? char.ToUpper(s[i]) : s[i]);
        }
        return sb.ToString();
    }

    // ── Navigation ───────────────────────────────────────────────

    private async void OnNewChannelClick(object sender, RoutedEventArgs e)
    {
        _selectedChannelId = null;
        _selectedThreadId = null;
        _selectedAgentId = null;
        _selectedJobId = null;
        _pendingNewThread = false;
        ChatTitleBlock.Text = "> Select or create a channel";
        ChannelTabBar.Visibility = Visibility.Collapsed;
        _settingsMode = false;
        SettingsScroller.Visibility = Visibility.Collapsed;
        ThreadSelectorPanel.Visibility = Visibility.Collapsed;
        JobSelectorPanel.Visibility = Visibility.Collapsed;
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
        var navigator = services.GetRequiredService<INavigator>();
        _ = navigator.NavigateRouteAsync(this, "Settings");
    }

    private void OnLogoutClick(object sender, RoutedEventArgs e)
    {
        if (App.Services is not { } services) return;
        var api = services.GetRequiredService<SharpClawApiClient>();
        api.SetAccessToken(null!);
        var navigator = services.GetRequiredService<INavigator>();
        _ = navigator.NavigateRouteAsync(this, "Login", qualifier: Qualifiers.ClearBackStack);
    }

    private async void OnOfficialWebsiteClick(object sender, RoutedEventArgs e)
        => await Windows.System.Launcher.LaunchUriAsync(new Uri("https://sharpclaw.mkn8rn.com"));

    private async void OnMatrixCommunityClick(object sender, RoutedEventArgs e)
        => await Windows.System.Launcher.LaunchUriAsync(new Uri("https://matrix.to/#/#p1:matrix.mkn8rn.com"));

    private async void OnCreatorBlogClick(object sender, RoutedEventArgs e)
        => await Windows.System.Launcher.LaunchUriAsync(new Uri("https://blog.mkn8rn.com"));

    // ── Role assignment (right-click context menu) ─────────────

    private MenuFlyout? BuildRoleMenuFlyout(bool isUser, Guid? agentId)
    {
        if (_allRoles.Count == 0 && !isUser) return null;

        var flyout = new MenuFlyout();
        var sub = new MenuFlyoutSubItem
        {
            Text = isUser ? "Assign Role" : "Assign Role to Agent",
        };

        // "Remove role" option
        var removeItem = new MenuFlyoutItem
        {
            Text = "(none — remove role)",
            FontFamily = _monoFont,
            FontSize = 11,
        };
        removeItem.Click += async (_, _) =>
        {
            if (isUser)
                await AssignRoleToSelfAsync(Guid.Empty);
            else if (agentId.HasValue)
                await AssignRoleToAgentAsync(agentId.Value, Guid.Empty);
        };
        sub.Items.Add(removeItem);

        if (_allRoles.Count > 0)
        {
            sub.Items.Add(new MenuFlyoutSeparator());
            foreach (var role in _allRoles)
            {
                var roleId = role.Id;
                var roleName = role.Name;

                // Mark current role with a bullet
                var isCurrent = isUser
                    ? _currentUserRoleId == roleId
                    : _allAgents.FirstOrDefault(a => a.Id == agentId)?.RoleId == roleId;
                var prefix = isCurrent ? "● " : "";

                var item = new MenuFlyoutItem
                {
                    Text = $"{prefix}{roleName}",
                    FontFamily = _monoFont,
                    FontSize = 11,
                };
                item.Click += async (_, _) =>
                {
                    if (isUser)
                        await AssignRoleToSelfAsync(roleId);
                    else if (agentId.HasValue)
                        await AssignRoleToAgentAsync(agentId.Value, roleId);
                };
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
            var content = new StringContent(body, Encoding.UTF8, "application/json");
            var resp = await api.PutAsync($"/agents/{agentId}/role", content);
            if (resp.IsSuccessStatusCode)
            {
                // Refresh the cached agent list so future menus reflect the change
                await LoadAgentsAsync(_selectedAgentId, null);
                await LoadRolesAsync();
                var agent = _allAgents.FirstOrDefault(a => a.Id == agentId);
                var label = roleId == Guid.Empty
                    ? $"✓ Removed role from {agent?.Name ?? "agent"}"
                    : $"✓ Assigned role '{agent?.RoleName}' to {agent?.Name ?? "agent"}";
                AppendMessage("system", label, DateTimeOffset.Now, senderName: "system");
                ScrollToBottom();
            }
            else
            {
                using var errStream = await resp.Content.ReadAsStreamAsync();
                var errMsg = await TryExtractErrorAsync(errStream) ?? $"{(int)resp.StatusCode} {resp.ReasonPhrase}";
                AppendMessage("system", $"✗ Role assignment failed: {errMsg}", DateTimeOffset.Now, senderName: "system");
                ScrollToBottom();
            }
        }
        catch (Exception ex)
        {
            AppendMessage("system", $"✗ Role assignment failed: {ex.Message}", DateTimeOffset.Now, senderName: "system");
            ScrollToBottom();
        }
    }

    private async Task AssignRoleToSelfAsync(Guid roleId)
    {
        var api = App.Services!.GetRequiredService<SharpClawApiClient>();
        try
        {
            var body = JsonSerializer.Serialize(new { roleId }, Json);
            var content = new StringContent(body, Encoding.UTF8, "application/json");
            var resp = await api.PutAsync("/auth/me/role", content);
            if (resp.IsSuccessStatusCode)
            {
                // Refresh cached user info
                await LoadUserInfoAsync();
                await LoadRolesAsync();
                var roleName = _allRoles.FirstOrDefault(r => r.Id == roleId)?.Name;
                var label = roleId == Guid.Empty
                    ? $"✓ Removed your role"
                    : $"✓ Assigned role '{roleName}' to yourself";
                AppendMessage("system", label, DateTimeOffset.Now, senderName: "system");
                ScrollToBottom();
            }
            else
            {
                using var errStream = await resp.Content.ReadAsStreamAsync();
                var errMsg = await TryExtractErrorAsync(errStream) ?? $"{(int)resp.StatusCode} {resp.ReasonPhrase}";
                AppendMessage("system", $"✗ Role assignment failed: {errMsg}", DateTimeOffset.Now, senderName: "system");
                ScrollToBottom();
            }
        }
        catch (Exception ex)
        {
            AppendMessage("system", $"✗ Role assignment failed: {ex.Message}", DateTimeOffset.Now, senderName: "system");
            ScrollToBottom();
        }
    }

    private static async Task<string?> TryExtractErrorAsync(Stream stream)
    {
        try
        {
            using var doc = await JsonDocument.ParseAsync(stream);
            if (doc.RootElement.TryGetProperty("error", out var ep) && ep.ValueKind == JsonValueKind.String)
                return ep.GetString();
        }
        catch { /* not JSON */ }
        return null;
    }

    // ── Helpers ──────────────────────────────────────────────────

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max] + "…";

    private void OnMessageTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_activeTranscriptionJobId is not null) return;
        UpdateCursor();
    }

    private void UpdateCursor(string? overrideMessage = null)
    {
        var msg = overrideMessage ?? MessageInput.Text ?? string.Empty;
        string cmd;
        if (_selectedThreadId is { } tid)
            cmd = $"sharpclaw chat {tid}";
        else if (_selectedChannelId is { } cid)
            cmd = $"sharpclaw chat {cid}";
        else
            cmd = "sharpclaw chat new-channel";
        if (msg.Length > 0)
            cmd += " " + Truncate(msg.Trim(), 40);
        Cursor.SetCommand(cmd + " ");
    }

    private static Windows.UI.Color ColorFrom(int rgb)
        => Windows.UI.Color.FromArgb(255, (byte)((rgb >> 16) & 0xFF), (byte)((rgb >> 8) & 0xFF), (byte)(rgb & 0xFF));

    // ── DTOs for JSON deserialization ────────────────────────────

    [ImplicitKeys(IsEnabled = false)]
    private sealed partial record ContextDto(Guid Id, string Name, Guid AgentId, string AgentName, DateTimeOffset CreatedAt);
    [ImplicitKeys(IsEnabled = false)]
    private sealed partial record ChannelDto(Guid Id, string Title, Guid? AgentId, string? AgentName, Guid? ContextId, DateTimeOffset CreatedAt);
    private sealed record ChatMessageDto(
        string Role, string Content, DateTimeOffset Timestamp,
        Guid? SenderUserId = null, string? SenderUsername = null,
        Guid? SenderAgentId = null, string? SenderAgentName = null,
        string? ClientType = null);
    private sealed record ChatResponseDto(ChatMessageDto UserMessage, ChatMessageDto AssistantMessage);
    [ImplicitKeys(IsEnabled = false)]
    private sealed partial record ThreadDto(Guid Id, string Name, Guid ChannelId, int? MaxMessages, int? MaxCharacters, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
    [ImplicitKeys(IsEnabled = false)]
    private sealed partial record AgentDto(Guid Id, string Name, string? SystemPrompt, Guid ModelId, string ModelName, string ProviderName, Guid? RoleId = null, string? RoleName = null);
    [ImplicitKeys(IsEnabled = false)]
    private sealed partial record RoleDto(Guid Id, string Name, Guid? PermissionSetId = null);
    [ImplicitKeys(IsEnabled = false)]
    private sealed partial record JobDto(Guid Id, Guid ChannelId, string ActionType, string Status, DateTimeOffset CreatedAt);
    private sealed record JobLogDto(string Message, string Level, DateTimeOffset Timestamp);
    [ImplicitKeys(IsEnabled = false)]
    private sealed partial record ResourceItemDto(Guid Id, string Name);

    private sealed class PermSettingsData
    {
        public readonly Dictionary<string, bool> GlobalFlags = [];
        public readonly Dictionary<string, List<Guid>> ResourceAccesses = [];
    }

    [ImplicitKeys(IsEnabled = false)]
    private sealed record TranscriptionSegmentDto(
        Guid Id, string Text, double StartTime, double EndTime,
        double? Confidence, DateTimeOffset Timestamp, bool IsProvisional = false);

    [ImplicitKeys(IsEnabled = false)]
    private sealed partial record JobDetailDto(
        Guid Id, Guid ChannelId, Guid AgentId, string ActionType, Guid? ResourceId,
        string Status, string? ResultData, string? ErrorLog,
        IReadOnlyList<JobLogDto>? Logs,
        DateTimeOffset CreatedAt, DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt,
        IReadOnlyList<TranscriptionSegmentDto>? Segments = null);
}
