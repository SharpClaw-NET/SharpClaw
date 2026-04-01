using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using SharpClaw.Helpers;
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
    private bool _tasksMode;
    private bool _jobsMode;
    private bool _botsMode;
    private bool _isSending;
    private bool _isThreadBusy;
    private CancellationTokenSource? _threadWatchCts;
    private bool _suppressThreadSelection;
    private bool _suppressJobSelection;
    private readonly Dictionary<Guid, bool> _expandedContexts = [];
    private readonly Dictionary<Guid, Guid> _channelAgentOverrides = [];
    private List<AgentDto> _allAgents = [];
    private List<JobDto> _channelJobs = [];
    private List<RoleDto> _allRoles = [];
    private string _currentUsername = "user";
    private Guid _currentUserId;
    private Guid? _currentUserRoleId;
    private static readonly int _clientType = DetectClientType();

    // ── Cached UI resources (avoids per-rebuild native allocations) ──
    private static readonly FontFamily _monoFont = TerminalUI.Mono;
    private static readonly SolidColorBrush _brushTransparent = TerminalUI.Transparent;
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
    private JobDetailDto? _currentJobDetail;

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

    // ── Task view state ──
    private List<TaskDefinitionDto> _taskDefinitions = [];
    private Guid? _selectedTaskDefinitionId;
    private Guid? _selectedTaskInstanceId;
    private TaskInstanceDetailDto? _currentTaskDetail;
    private bool _suppressTaskDefSelection;
    private bool _suppressTaskInstSelection;
    private readonly List<ComboBoxItem> _taskDefItemPool = [];
    private int _taskDefItemPoolUsed;
    private readonly List<ComboBoxItem> _taskInstItemPool = [];
    private int _taskInstItemPoolUsed;
    private readonly List<JobLogRow> _taskLogPool = [];
    private int _taskLogPoolUsed;
    private readonly List<string> _taskTimestampParts = new(3);
    private List<TaskInstanceSummaryDto> _allTaskInstances = [];
    private bool _suppressTaskSelection;
    private readonly List<ComboBoxItem> _taskAllInstItemPool = [];
    private int _taskAllInstItemPoolUsed;
    private bool _taskCreateNewMode;
    private CancellationTokenSource? _taskStreamCts;

    // ── Reusable scratch collections ──
    private readonly HashSet<Guid> _sidebarContextChannelIds = [];
    private readonly List<string> _jobTimestampParts = new(3);

    // ── Resource lookup cache (populated per settings load) ──
    private readonly Dictionary<string, List<ResourceItemDto>> _resourceLookupCache = new(13);

    // ── Transcription mic state ──
    private Guid? _activeTranscriptionJobId;
    private CancellationTokenSource? _transcriptionPollCts;
    private readonly StringBuilder _transcriptionAccumulator = new(capacity: 2048);

    private static SolidColorBrush Brush(int rgb) => TerminalUI.Brush(rgb);
    private static string Truncate(string s, int max) => TerminalUI.Truncate(s, max);

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
                    _tasksMode = false;
                    _jobsMode = false;
                    SettingsScroller.Visibility = Visibility.Collapsed;
                    TaskViewPanel.Visibility = Visibility.Collapsed;
                    DeallocateTaskView();
                    JobViewPanel.Visibility = Visibility.Collapsed;
                    DeallocateJobView();
                    ThreadSelectorPanel.Visibility = Visibility.Collapsed;
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
        DisconnectThreadWatch();
        _selectedChannelId = id;
        _selectedThreadId = null;
        _selectedJobId = null;
        _pendingNewThread = false;
        ChatTitleBlock.Text = $"# {title}";
        ChannelTabBar.Visibility = Visibility.Visible;
        if (_settingsMode || _tasksMode || _jobsMode)
        {
            _settingsMode = false;
            _tasksMode = false;
            _jobsMode = false;
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

        // Restore a previously remembered agent override for this channel,
        // falling back to the channel's default agent.
        _selectedAgentId = _channelAgentOverrides.TryGetValue(id, out var overrideAgent)
            ? overrideAgent
            : channelAgentId;
        UpdateSidebarHighlight();
        await LoadAgentsAsync(channelAgentId, allowedAgentIds);
        await LoadThreadsAsync(id);
        await LoadJobsAsync(id);
        ShowChatView();
        await LoadHistoryAsync(id);
        await LoadCostAsync(id);
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
        {
            _selectedAgentId = agentId;
            if (_selectedChannelId is { } chId)
                _channelAgentOverrides[chId] = agentId;
        }
        else
        {
            _selectedAgentId = null;
            if (_selectedChannelId is { } chId)
                _channelAgentOverrides.Remove(chId);
        }

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

        // Connect/disconnect the real-time thread watch SSE
        if (_selectedChannelId is { } chId && _selectedThreadId is { } watchTid)
            ConnectThreadWatch(chId, watchTid);
        else
            DisconnectThreadWatch();

        if (_selectedChannelId is { } channelId)
        {
            await LoadHistoryAsync(channelId);
            await LoadCostAsync(channelId);
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

    // -- Cost bars, messages, jobs, tasks, settings, navigation --
    // Split into partial-class files: MainPage.Chat.cs, MainPage.Jobs.cs,
    // MainPage.Tasks.cs, MainPage.ChannelSettings.cs, MainPage.Navigation.cs

    private void ShowChatView()
    {
        JobViewPanel.Visibility = Visibility.Collapsed;
        TaskViewPanel.Visibility = Visibility.Collapsed;
        SettingsScroller.Visibility = Visibility.Collapsed;
        BotViewPanel.Visibility = Visibility.Collapsed;
        AgentSelectorPanel.Visibility = Visibility.Visible;
        ThreadSelectorPanel.Visibility = _selectedChannelId is not null ? Visibility.Visible : Visibility.Collapsed;
        MessagesScroller.Visibility = Visibility.Visible;
        ChatInputArea.Visibility = Visibility.Visible;
    }

    private void OnTabChatClick(object sender, RoutedEventArgs e)
    {
        if (!_settingsMode && !_tasksMode && !_jobsMode && !_botsMode) return;
        _settingsMode = false;
        _tasksMode = false;
        _jobsMode = false;
        _botsMode = false;
        _taskCreateNewMode = false;
        UpdateTabHighlight();
        SettingsScroller.Visibility = Visibility.Collapsed;
        TaskViewPanel.Visibility = Visibility.Collapsed;
        DeallocateTaskView();
        JobViewPanel.Visibility = Visibility.Collapsed;
        DeallocateJobView();
        BotViewPanel.Visibility = Visibility.Collapsed;
        AgentSelectorPanel.Visibility = Visibility.Visible;
        if (_selectedChannelId is not null)
        {
            ThreadSelectorPanel.Visibility = Visibility.Visible;
        }
        OneOffWarning.Visibility = _selectedThreadId is null && !_pendingNewThread && _selectedChannelId is not null
            ? Visibility.Visible : Visibility.Collapsed;
        ShowChatView();
        UpdateMicState();
    }

    private void UpdateTabHighlight()
    {
        var chatActive = !_settingsMode && !_tasksMode && !_jobsMode && !_botsMode;
        if (TabChatButton.Content is TextBlock c) c.Foreground = Brush(chatActive ? 0x00FF00 : 0x666666);
        if (TabTasksButton.Content is TextBlock t) t.Foreground = Brush(_tasksMode ? 0x00FF00 : 0x666666);
        if (TabJobsButton.Content is TextBlock j) j.Foreground = Brush(_jobsMode ? 0x00FF00 : 0x666666);
        if (TabSettingsButton.Content is TextBlock s) s.Foreground = Brush(_settingsMode ? 0x00FF00 : 0x666666);
        if (TabBotsButton.Content is TextBlock b) b.Foreground = Brush(_botsMode ? 0x00FF00 : 0x666666);
    }

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
        IReadOnlyList<TranscriptionSegmentDto>? Segments = null,
        ChannelCostDto? ChannelCost = null);

    // ── Task DTOs ────────────────────────────────────────────────
    [ImplicitKeys(IsEnabled = false)]
    private sealed partial record TaskDefinitionDto(
        Guid Id, string Name, string? Description, string? OutputTypeName,
        bool IsActive, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

    [ImplicitKeys(IsEnabled = false)]
    private sealed partial record TaskInstanceSummaryDto(
        Guid Id, Guid TaskDefinitionId, string TaskName, string Status,
        DateTimeOffset CreatedAt, DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt);

    private sealed record TaskLogDto(string Message, string Level, DateTimeOffset Timestamp);

    [ImplicitKeys(IsEnabled = false)]
    private sealed partial record TaskInstanceDetailDto(
        Guid Id, Guid TaskDefinitionId, string TaskName, string Status,
        string? OutputSnapshotJson, string? ErrorMessage,
        IReadOnlyList<TaskLogDto>? Logs,
        DateTimeOffset CreatedAt, DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt,
        Guid? ChannelId = null, ChannelCostDto? ChannelCost = null);

    // ── Cost DTOs ────────────────────────────────────────────────
    private sealed record AgentTokenBreakdownDto(
        Guid AgentId, string AgentName,
        int PromptTokens, int CompletionTokens, int TotalTokens);

    private sealed record ChannelCostDto(
        Guid ChannelId,
        int TotalPromptTokens, int TotalCompletionTokens, int TotalTokens,
        IReadOnlyList<AgentTokenBreakdownDto> AgentBreakdown);

    private sealed record ThreadCostDto(
        Guid ThreadId, Guid ChannelId,
        int TotalPromptTokens, int TotalCompletionTokens, int TotalTokens,
        IReadOnlyList<AgentTokenBreakdownDto> AgentBreakdown);
}
