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
    private bool _suppressThreadSelection;
    private bool _suppressJobSelection;
    private readonly Dictionary<Guid, bool> _expandedContexts = [];
    private List<AgentDto> _allAgents = [];
    private List<JobDto> _channelJobs = [];
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

    private static SolidColorBrush Brush(int rgb)
    {
        if (!_brushCache.TryGetValue(rgb, out var brush))
        {
            brush = new SolidColorBrush(ColorFrom(rgb));
            _brushCache[rgb] = brush;
        }
        return brush;
    }

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
                    ChatAgentBlock.Text = string.Empty;
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
        ChatAgentBlock.Text = agentName is not null ? $"agent: {agentName}" : string.Empty;
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

        // Update the agent subtitle
        var agent = _allAgents.FirstOrDefault(a => a.Id == _selectedAgentId);
        ChatAgentBlock.Text = agent is not null ? $"agent: {agent.Name}" : string.Empty;

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

            // Result / Error data
            if (!string.IsNullOrWhiteSpace(job.ResultData))
                await AppendJobResultAsync(job.ResultData);
            if (!string.IsNullOrWhiteSpace(job.ErrorLog))
                AppendJobLog("error", TruncateForDisplay(job.ErrorLog), null);

            if (job.Logs is { Count: 0 } && string.IsNullOrWhiteSpace(job.ResultData) && string.IsNullOrWhiteSpace(job.ErrorLog))
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

            foreach (var msg in messages)
                AppendMessage(msg.Role, msg.Content, msg.Timestamp);
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

    private void AppendMessage(string role, string content, DateTimeOffset? timestamp)
    {
        var isUser = role.Equals("user", StringComparison.OrdinalIgnoreCase);
        var row = AcquireChatBubble();

        row.Root.Background = Brush(isUser ? 0x1A2A1A : 0x1A1A1A);
        row.Root.HorizontalAlignment = isUser ? HorizontalAlignment.Right : HorizontalAlignment.Left;

        row.Role.Text = isUser ? "you" : "assistant";
        row.Role.Foreground = Brush(isUser ? 0x00FF00 : 0x00AAFF);

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
        row.Content.Foreground = Brush(0xCCCCCC);

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
                var agentName = chDoc.RootElement.TryGetProperty("agent", out var agentProp)
                    && agentProp.ValueKind == JsonValueKind.Object
                    && agentProp.TryGetProperty("name", out var anProp)
                    ? anProp.GetString() : null;

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
                ChatAgentBlock.Text = agentName is not null ? $"agent: {agentName}" : string.Empty;
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

        AppendMessage("user", text, DateTimeOffset.Now);
        ScrollToBottom();

        UpdateCursor(text);

        var streamBubble = AcquireChatBubble();
        streamBubble.Root.Background = Brush(0x1A1A1A);
        streamBubble.Root.HorizontalAlignment = HorizontalAlignment.Left;
        streamBubble.Role.Text = "assistant";
        streamBubble.Role.Foreground = Brush(0x00AAFF);
        streamBubble.Time.Text = DateTimeOffset.Now.LocalDateTime.ToString("HH:mm");
        streamBubble.Time.Visibility = Visibility.Visible;
        streamBubble.Content.Text = "▍";
        streamBubble.Content.Foreground = Brush(0xCCCCCC);
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

    // ── Navigation ───────────────────────────────────────────────

    private async void OnNewChannelClick(object sender, RoutedEventArgs e)
    {
        _selectedChannelId = null;
        _selectedThreadId = null;
        _selectedAgentId = null;
        _selectedJobId = null;
        _pendingNewThread = false;
        ChatTitleBlock.Text = "> Select or create a channel";
        ChatAgentBlock.Text = string.Empty;
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

    private void OnDashboardClick(object sender, RoutedEventArgs e)
    {
        if (App.Services is not { } services) return;
        var navigator = services.GetRequiredService<INavigator>();
        _ = navigator.NavigateRouteAsync(this, "Dashboard");
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

    // ── Helpers ──────────────────────────────────────────────────

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max] + "…";

    private void OnMessageTextChanged(object sender, TextChangedEventArgs e) => UpdateCursor();

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
    private sealed record ChatMessageDto(string Role, string Content, DateTimeOffset Timestamp);
    private sealed record ChatResponseDto(ChatMessageDto UserMessage, ChatMessageDto AssistantMessage);
    [ImplicitKeys(IsEnabled = false)]
    private sealed partial record ThreadDto(Guid Id, string Name, Guid ChannelId, int? MaxMessages, int? MaxCharacters, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
    [ImplicitKeys(IsEnabled = false)]
    private sealed partial record AgentDto(Guid Id, string Name, string? SystemPrompt, Guid ModelId, string ModelName, string ProviderName, Guid? RoleId = null, string? RoleName = null);
    [ImplicitKeys(IsEnabled = false)]
    private sealed partial record JobDto(Guid Id, Guid ChannelId, string ActionType, string Status, DateTimeOffset CreatedAt);
    private sealed record JobLogDto(string Message, string Level, DateTimeOffset Timestamp);
    [ImplicitKeys(IsEnabled = false)]
    private sealed partial record JobDetailDto(
        Guid Id, Guid ChannelId, Guid AgentId, string ActionType, Guid? ResourceId,
        string Status, string? ResultData, string? ErrorLog,
        IReadOnlyList<JobLogDto>? Logs,
        DateTimeOffset CreatedAt, DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt);
}
