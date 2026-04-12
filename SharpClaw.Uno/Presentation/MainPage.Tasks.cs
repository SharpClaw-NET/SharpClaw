using System.Text;
using System.Text.Json;
using SharpClaw.Helpers;
using SharpClaw.Services;

namespace SharpClaw.Presentation;

// Tasks view: definitions, instances, editor, SSE streaming, validation.
public sealed partial class MainPage
{
    private const string DefaultTaskTemplate =
        "[Task(\"DemoTask\")]\n" +
        "[Description(\"Creates an agent, selects a model, chats, and outputs to a thread\")]\n" +
        "public class DemoTask\n" +
        "{\n" +
        "    public async Task RunAsync(CancellationToken ct)\n" +
        "    {\n" +
        "        // 1. Find the model\n" +
        "        var modelId = FindModel(\"gpt-5-mini-2025-08-07\");\n" +
        "\n" +
        "        // 2. Create a task-scoped agent with a custom ID for reuse\n" +
        "        var agentId = CreateAgent(\"Task test agent\", modelId, \"You are a helpful assistant.\", \"task-test-agent\");\n" +
        "        Log(\"Created agent: \" + agentId);\n" +
        "\n" +
        "        // 3. Create a thread in the originating channel for output\n" +
        "        var threadId = CreateThread(\"channel\", \"DemoTask Output\");\n" +
        "        Log(\"Created thread: \" + threadId);\n" +
        "\n" +
        "        // 4. Chat with the agent\n" +
        "        var reply = Chat(agentId, \"What kinds of tasks can SharpClaw automate? Give 3 examples.\");\n" +
        "        Log(\"Agent replied: \" + reply);\n" +
        "\n" +
        "        // 5. Write the reply into the output thread\n" +
        "        ChatToThread(threadId, reply, agentId);\n" +
        "\n" +
        "        // 6. Emit the result for SSE listeners\n" +
        "        await Emit(new { agentId, threadId, reply });\n" +
        "    }\n" +
        "}";

    // ── Tasks tab ────────────────────────────────────────────────

    private async void OnTabTasksClick(object sender, RoutedEventArgs e)
    {
        if (_tasksMode) return;
        _tasksMode = true;
        _settingsMode = false;
        _jobsMode = false;
        _botsMode = false;
        UpdateTabHighlight();

        MessagesScroller.Visibility = Visibility.Collapsed;
        ChatInputArea.Visibility = Visibility.Collapsed;
        JobViewPanel.Visibility = Visibility.Collapsed;
        DeallocateJobView();
        SettingsScroller.Visibility = Visibility.Collapsed;
        BotViewPanel.Visibility = Visibility.Collapsed;
        AgentSelectorPanel.Visibility = Visibility.Collapsed;
        ThreadSelectorPanel.Visibility = Visibility.Collapsed;
        OneOffWarning.Visibility = Visibility.Collapsed;

        TaskViewPanel.Visibility = Visibility.Visible;
        await LoadTaskDefinitionsAsync();
        await LoadAllTaskInstancesAsync();

        ShowTaskEditorOrLogs();
    }

    private async Task LoadTaskDefinitionsAsync()
    {
        _suppressTaskDefSelection = true;
        TaskDefinitionSelector.Items.Clear();

        var createNewItem = new ComboBoxItem { Content = "[Create new]", Tag = "create-new" };
        TaskDefinitionSelector.Items.Add(createNewItem);

        var api = App.Services!.GetRequiredService<SharpClawApiClient>();
        try
        {
            using var resp = await api.GetAsync("/tasks");
            if (resp.IsSuccessStatusCode)
            {
                using var stream = await resp.Content.ReadAsStreamAsync();
                _taskDefinitions = await JsonSerializer.DeserializeAsync<List<TaskDefinitionDto>>(stream, Json) ?? [];

                _taskDefItemPoolUsed = 0;
                foreach (var def in _taskDefinitions)
                {
                    var label = $"{def.Name}  ({(def.IsActive ? "active" : "inactive")})";
                    ComboBoxItem item;
                    if (_taskDefItemPoolUsed < _taskDefItemPool.Count)
                        item = _taskDefItemPool[_taskDefItemPoolUsed++];
                    else
                    {
                        item = new ComboBoxItem();
                        _taskDefItemPool.Add(item);
                        _taskDefItemPoolUsed++;
                    }
                    item.Content = label;
                    item.Tag = def.Id;
                    TaskDefinitionSelector.Items.Add(item);
                }
            }
        }
        catch { _taskDefinitions = []; }

        var selectedIndex = -1;
        if (_taskCreateNewMode)
        {
            selectedIndex = 0;
        }
        else if (_selectedTaskDefinitionId is { } defId)
        {
            for (var i = 0; i < TaskDefinitionSelector.Items.Count; i++)
            {
                if (TaskDefinitionSelector.Items[i] is ComboBoxItem ci && ci.Tag is Guid g && g == defId)
                { selectedIndex = i; break; }
            }
        }

        if (selectedIndex >= 0)
            TaskDefinitionSelector.SelectedIndex = selectedIndex;
        _suppressTaskDefSelection = false;
    }

    private async Task LoadAllTaskInstancesAsync()
    {
        _suppressTaskSelection = true;
        TaskSelector.Items.Clear();
        _allTaskInstances = [];

        if (_taskDefinitions.Count == 0)
        {
            _suppressTaskSelection = false;
            return;
        }

        var api = App.Services!.GetRequiredService<SharpClawApiClient>();
        try
        {
            var fetchTasks = _taskDefinitions.Select(async def =>
            {
                try
                {
                    using var resp = await api.GetAsync($"/tasks/{def.Id}/instances");
                    if (resp.IsSuccessStatusCode)
                    {
                        using var stream = await resp.Content.ReadAsStreamAsync();
                        return await JsonSerializer.DeserializeAsync<List<TaskInstanceSummaryDto>>(stream, Json) ?? [];
                    }
                }
                catch { /* swallow */ }
                return new List<TaskInstanceSummaryDto>();
            });
            var results = await Task.WhenAll(fetchTasks);
            _allTaskInstances = results.SelectMany(r => r)
                .OrderByDescending(i => i.CreatedAt)
                .ToList();
        }
        catch { _allTaskInstances = []; }

        _taskAllInstItemPoolUsed = 0;
        foreach (var inst in _allTaskInstances)
        {
            var label = $"[{inst.Status}] {inst.TaskName}";
            if (inst.CreatedAt != default)
                label += $"  {inst.CreatedAt.LocalDateTime:MM/dd HH:mm}";
            ComboBoxItem item;
            if (_taskAllInstItemPoolUsed < _taskAllInstItemPool.Count)
                item = _taskAllInstItemPool[_taskAllInstItemPoolUsed++];
            else
            {
                item = new ComboBoxItem();
                _taskAllInstItemPool.Add(item);
                _taskAllInstItemPoolUsed++;
            }
            item.Content = label;
            item.Tag = inst.Id;
            TaskSelector.Items.Add(item);
        }

        var selectedIndex = -1;
        if (_selectedTaskInstanceId is { } instId)
        {
            for (var i = 0; i < TaskSelector.Items.Count; i++)
            {
                if (TaskSelector.Items[i] is ComboBoxItem ci && ci.Tag is Guid g && g == instId)
                { selectedIndex = i; break; }
            }
        }

        if (selectedIndex >= 0)
            TaskSelector.SelectedIndex = selectedIndex;
        _suppressTaskSelection = false;
    }

    private async void OnTaskSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressTaskSelection) return;

        StopTaskStream();
        _taskCreateNewMode = false;

        if (TaskSelector.SelectedItem is ComboBoxItem { Tag: Guid instId })
        {
            var inst = _allTaskInstances.FirstOrDefault(i => i.Id == instId);
            if (inst is not null)
            {
                _selectedTaskDefinitionId = inst.TaskDefinitionId;
                _selectedTaskInstanceId = instId;

                _suppressTaskDefSelection = true;
                for (var i = 0; i < TaskDefinitionSelector.Items.Count; i++)
                {
                    if (TaskDefinitionSelector.Items[i] is ComboBoxItem ci && ci.Tag is Guid g && g == inst.TaskDefinitionId)
                    { TaskDefinitionSelector.SelectedIndex = i; break; }
                }
                _suppressTaskDefSelection = false;

                TaskExecuteButton.Visibility = Visibility.Visible;
                TaskInstanceSelectorPanel.Visibility = Visibility.Visible;
                await LoadTaskInstancesAsync(inst.TaskDefinitionId);
                ShowTaskEditorOrLogs();
                await ShowTaskInstanceViewAsync(inst.TaskDefinitionId, instId);
            }
        }
        else
        {
            _selectedTaskInstanceId = null;
            _currentTaskDetail = null;
            DeallocateTaskView();
            ShowTaskEditorOrLogs();
        }
    }

    private async void OnTaskDefinitionSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressTaskDefSelection) return;

        if (TaskDefinitionSelector.SelectedItem is ComboBoxItem { Tag: string tag } && tag == "create-new")
        {
            StopTaskStream();
            _taskCreateNewMode = true;
            _selectedTaskDefinitionId = null;
            _selectedTaskInstanceId = null;
            _currentTaskDetail = null;
            TaskExecuteButton.Visibility = Visibility.Collapsed;
            TaskInstanceSelectorPanel.Visibility = Visibility.Collapsed;

            _suppressTaskSelection = true;
            TaskSelector.SelectedIndex = -1;
            _suppressTaskSelection = false;

            TaskStatusBlock.Text = string.Empty;
            TaskNameBlock.Text = string.Empty;
            TaskTimestampBlock.Text = string.Empty;
            if (string.IsNullOrEmpty(TaskSourceEditor.Text))
                TaskSourceEditor.Text = DefaultTaskTemplate;

            ShowTaskEditorOrLogs();
            return;
        }

        if (TaskDefinitionSelector.SelectedItem is ComboBoxItem { Tag: Guid defId })
        {
            StopTaskStream();
            _taskCreateNewMode = false;
            _selectedTaskDefinitionId = defId;
            _selectedTaskInstanceId = null;
            _currentTaskDetail = null;
            TaskExecuteButton.Visibility = Visibility.Visible;

            _suppressTaskSelection = true;
            TaskSelector.SelectedIndex = -1;
            _suppressTaskSelection = false;

            var def = _taskDefinitions.FirstOrDefault(d => d.Id == defId);
            TaskNameBlock.Text = def is not null && !string.IsNullOrWhiteSpace(def.Description)
                ? def.Description : string.Empty;
            TaskStatusBlock.Text = string.Empty;
            TaskTimestampBlock.Text = string.Empty;

            await LoadTaskInstancesAsync(defId);
            ShowTaskEditorOrLogs();
        }
        else
        {
            StopTaskStream();
            _taskCreateNewMode = false;
            _selectedTaskDefinitionId = null;
            _selectedTaskInstanceId = null;
            _currentTaskDetail = null;
            TaskExecuteButton.Visibility = Visibility.Collapsed;
            TaskInstanceSelectorPanel.Visibility = Visibility.Collapsed;
            DeallocateTaskView();
            ShowTaskEditorOrLogs();
        }
    }

    private async Task LoadTaskInstancesAsync(Guid taskDefinitionId)
    {
        TaskInstanceSelectorPanel.Visibility = Visibility.Visible;
        _suppressTaskInstSelection = true;
        TaskInstanceSelector.Items.Clear();

        var api = App.Services!.GetRequiredService<SharpClawApiClient>();
        try
        {
            using var resp = await api.GetAsync($"/tasks/{taskDefinitionId}/instances");
            if (resp.IsSuccessStatusCode)
            {
                using var stream = await resp.Content.ReadAsStreamAsync();
                var instances = await JsonSerializer.DeserializeAsync<List<TaskInstanceSummaryDto>>(stream, Json) ?? [];

                _taskInstItemPoolUsed = 0;
                foreach (var inst in instances)
                {
                    var label = $"[{inst.Status}] {inst.TaskName}";
                    if (inst.CreatedAt != default)
                        label += $"  {inst.CreatedAt.LocalDateTime:MM/dd HH:mm}";
                    ComboBoxItem item;
                    if (_taskInstItemPoolUsed < _taskInstItemPool.Count)
                        item = _taskInstItemPool[_taskInstItemPoolUsed++];
                    else
                    {
                        item = new ComboBoxItem();
                        _taskInstItemPool.Add(item);
                        _taskInstItemPoolUsed++;
                    }
                    item.Content = label;
                    item.Tag = inst.Id;
                    TaskInstanceSelector.Items.Add(item);
                }
            }
        }
        catch { /* swallow */ }

        var selectedIndex = -1;
        if (_selectedTaskInstanceId is { } instId)
        {
            for (var i = 0; i < TaskInstanceSelector.Items.Count; i++)
            {
                if (TaskInstanceSelector.Items[i] is ComboBoxItem ci && ci.Tag is Guid g && g == instId)
                { selectedIndex = i; break; }
            }
        }

        if (selectedIndex >= 0)
            TaskInstanceSelector.SelectedIndex = selectedIndex;
        _suppressTaskInstSelection = false;
    }

    private async void OnTaskInstanceSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressTaskInstSelection) return;

        if (TaskInstanceSelector.SelectedItem is ComboBoxItem { Tag: Guid instId }
            && _selectedTaskDefinitionId is { } defId)
        {
            _selectedTaskInstanceId = instId;
            ShowTaskEditorOrLogs();
            await ShowTaskInstanceViewAsync(defId, instId);
        }
        else
        {
            _selectedTaskInstanceId = null;
            _currentTaskDetail = null;
            DeallocateTaskView();
            ShowTaskEditorOrLogs();
        }
    }

    private async Task ShowTaskInstanceViewAsync(Guid taskDefinitionId, Guid instanceId)
    {
        DeallocateTaskView();
        TaskStatusBlock.Text = "loading";
        TaskStatusBlock.Foreground = Brush(0x999999);
        TaskNameBlock.Text = string.Empty;
        TaskTimestampBlock.Text = string.Empty;

        var api = App.Services!.GetRequiredService<SharpClawApiClient>();
        try
        {
            using var resp = await api.GetAsync($"/tasks/{taskDefinitionId}/instances/{instanceId}");
            if (!resp.IsSuccessStatusCode)
            {
                _taskLogPoolUsed = 0;
                TaskLogsPanel.Children.Clear();
                TaskStatusBlock.Text = "error";
                TaskStatusBlock.Foreground = Brush(0xFF4444);
                AppendTaskLog("error", $"Failed to load instance: {(int)resp.StatusCode} {resp.ReasonPhrase}", null);
                return;
            }

            var detail = await JsonSerializer.DeserializeAsync<TaskInstanceDetailDto>(
                await resp.Content.ReadAsStreamAsync(), Json);
            if (detail is null)
            {
                _taskLogPoolUsed = 0;
                TaskLogsPanel.Children.Clear();
                AppendTaskLog("error", "Instance response was null.", null);
                return;
            }

            _taskLogPoolUsed = 0;
            TaskLogsPanel.Children.Clear();

            TaskStatusBlock.Text = $"status: {detail.Status}";
            TaskStatusBlock.Foreground = Brush(detail.Status switch
            {
                "Completed" => 0x00FF00,
                "Failed" or "Cancelled" => 0xFF4444,
                "Running" => 0x00AAFF,
                "Paused" => 0xFFAA00,
                "Queued" => 0xCCCCCC,
                _ => 0x999999,
            });
            TaskNameBlock.Text = $"task: {detail.TaskName}";

            _taskTimestampParts.Clear();
            if (detail.CreatedAt != default) _taskTimestampParts.Add($"created: {detail.CreatedAt.LocalDateTime:yyyy-MM-dd HH:mm:ss}");
            if (detail.StartedAt.HasValue) _taskTimestampParts.Add($"started: {detail.StartedAt.Value.LocalDateTime:HH:mm:ss}");
            if (detail.CompletedAt.HasValue) _taskTimestampParts.Add($"completed: {detail.CompletedAt.Value.LocalDateTime:HH:mm:ss}");
            TaskTimestampBlock.Text = string.Join("  |  ", _taskTimestampParts);

            var hasLogs = detail.Logs is { Count: > 0 };
            if (hasLogs)
            {
                AppendTaskLog("info", $"── execution logs ({detail.Logs!.Count}) ──", null);
                foreach (var log in detail.Logs)
                    AppendTaskLog(log.Level, TruncateForDisplay(log.Message), log.Timestamp);
            }

            var hasOutput = !string.IsNullOrWhiteSpace(detail.OutputSnapshotJson);
            var hasError = !string.IsNullOrWhiteSpace(detail.ErrorMessage);
            if (hasOutput || hasError)
            {
                AppendTaskLog("info", "── task output ──", null);
                if (hasOutput) AppendTaskLog("result", TruncateForDisplay(detail.OutputSnapshotJson!), null);
                if (hasError) AppendTaskLog("error", TruncateForDisplay(detail.ErrorMessage!), null);
            }

            if (!hasLogs && !hasOutput && !hasError)
                AppendTaskLog("info", "(no log entries yet)", null);

            if (detail.ChannelCost is { } taskCost)
                RenderInlineCost(taskCost, null);

            TaskStopButton.Visibility = Visibility.Collapsed;
            TaskCancelButton.Visibility = Visibility.Collapsed;
            if (detail.Status is "Queued" or "Running" or "Paused")
            {
                TaskCancelButton.Visibility = Visibility.Visible;
                TaskStopButton.Visibility = Visibility.Visible;
            }

            _currentTaskDetail = detail;
            TaskCopyLogsButton.Visibility = hasLogs ? Visibility.Visible : Visibility.Collapsed;
            TaskCopyResultButton.Visibility = hasOutput || hasError ? Visibility.Visible : Visibility.Collapsed;

            if (detail.Status is "Queued" or "Running" or "Paused")
            {
                StopTaskStream();
                _taskStreamCts = new CancellationTokenSource();
                _ = StreamTaskEventsAsync(taskDefinitionId, instanceId, _taskStreamCts.Token);
            }
        }
        catch (Exception ex)
        {
            _taskLogPoolUsed = 0;
            TaskLogsPanel.Children.Clear();
            TaskStatusBlock.Text = "error";
            TaskStatusBlock.Foreground = Brush(0xFF4444);
            AppendTaskLog("error", $"Failed to load instance: {ex.Message}", null);
        }

        TaskLogsScroller.UpdateLayout();
        TaskLogsScroller.ChangeView(null, TaskLogsScroller.ScrollableHeight, null);
    }

    private void DeallocateTaskView()
    {
        StopTaskStream();
        _taskLogPoolUsed = 0;
        TaskLogsPanel.Children.Clear();
        TaskStopButton.Visibility = Visibility.Collapsed;
        TaskCancelButton.Visibility = Visibility.Collapsed;
        TaskCopyLogsButton.Visibility = Visibility.Collapsed;
        TaskCopyResultButton.Visibility = Visibility.Collapsed;
        TaskSubmitButton.Visibility = Visibility.Collapsed;
        TaskNoInstancePlaceholder.Visibility = Visibility.Collapsed;
        _currentTaskDetail = null;
    }

    private JobLogRow AcquireTaskLogRow()
    {
        if (_taskLogPoolUsed < _taskLogPool.Count)
            return _taskLogPool[_taskLogPoolUsed++];

        var root = new Grid { ColumnSpacing = 8 };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var ts = new TextBlock { FontFamily = _monoFont, FontSize = 11, Foreground = Brush(0x555555), VerticalAlignment = VerticalAlignment.Top };
        Grid.SetColumn(ts, 0);
        root.Children.Add(ts);

        var lv = new TextBlock { FontFamily = _monoFont, FontSize = 11, VerticalAlignment = VerticalAlignment.Top, MinWidth = 60 };
        Grid.SetColumn(lv, 1);
        root.Children.Add(lv);

        var msg = new TextBlock { FontFamily = _monoFont, FontSize = 11, Foreground = Brush(0xCCCCCC), TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        Grid.SetColumn(msg, 2);
        root.Children.Add(msg);

        var entry = new JobLogRow(root, ts, lv, msg);
        _taskLogPool.Add(entry);
        _taskLogPoolUsed++;
        return entry;
    }

    private void AppendTaskLog(string level, string message, DateTimeOffset? timestamp)
    {
        var row = AcquireTaskLogRow();
        if (timestamp.HasValue) { row.Timestamp.Text = timestamp.Value.LocalDateTime.ToString("HH:mm:ss"); row.Timestamp.Visibility = Visibility.Visible; }
        else { row.Timestamp.Visibility = Visibility.Collapsed; }
        row.Level.Text = $"[{level}]";
        row.Level.Foreground = Brush(level.ToLowerInvariant() switch { "error" => 0xFF4444, "warning" or "warn" => 0xFFAA00, "result" => 0x00FF00, _ => 0x00AAFF });
        row.Message.Text = message;
        TaskLogsPanel.Children.Add(row.Root);
    }

    private async void OnTaskExecuteClick(object sender, RoutedEventArgs e)
    {
        if (_selectedTaskDefinitionId is not { } defId) return;
        var api = App.Services!.GetRequiredService<SharpClawApiClient>();
        try
        {
            var body = JsonSerializer.Serialize(new { TaskDefinitionId = defId, ChannelId = _selectedChannelId }, Json);
            var createResp = await api.PostAsync($"/tasks/{defId}/instances", new StringContent(body, Encoding.UTF8, "application/json"));
            if (!createResp.IsSuccessStatusCode) { AppendTaskLog("error", $"Create instance failed: {(int)createResp.StatusCode} {createResp.ReasonPhrase}", DateTimeOffset.Now); return; }

            using var createStream = await createResp.Content.ReadAsStreamAsync();
            var created = await JsonSerializer.DeserializeAsync<TaskInstanceDetailDto>(createStream, Json);
            if (created is null) { AppendTaskLog("error", "Created instance response was null.", DateTimeOffset.Now); return; }

            var startResp = await api.PostAsync($"/tasks/{defId}/instances/{created.Id}/start", null);
            if (!startResp.IsSuccessStatusCode) { AppendTaskLog("error", $"Start failed: {(int)startResp.StatusCode} {startResp.ReasonPhrase}", DateTimeOffset.Now); return; }

            _selectedTaskInstanceId = created.Id;
            await LoadTaskInstancesAsync(defId);
            ShowTaskEditorOrLogs();
            await ShowTaskInstanceViewAsync(defId, created.Id);
        }
        catch (Exception ex) { AppendTaskLog("error", $"Execute failed: {ex.Message}", DateTimeOffset.Now); }
    }

    private async void OnTaskStopClick(object sender, RoutedEventArgs e)
    {
        if (_selectedTaskDefinitionId is not { } defId || _selectedTaskInstanceId is not { } instId) return;
        var api = App.Services!.GetRequiredService<SharpClawApiClient>();
        try { var resp = await api.PostAsync($"/tasks/{defId}/instances/{instId}/stop", null); if (resp.IsSuccessStatusCode) await ShowTaskInstanceViewAsync(defId, instId); else AppendTaskLog("error", $"Stop failed: {(int)resp.StatusCode} {resp.ReasonPhrase}", DateTimeOffset.Now); }
        catch (Exception ex) { AppendTaskLog("error", $"Stop failed: {ex.Message}", DateTimeOffset.Now); }
    }

    private async void OnTaskCancelClick(object sender, RoutedEventArgs e)
    {
        if (_selectedTaskDefinitionId is not { } defId || _selectedTaskInstanceId is not { } instId) return;
        var api = App.Services!.GetRequiredService<SharpClawApiClient>();
        try { var resp = await api.PostAsync($"/tasks/{defId}/instances/{instId}/cancel", null); if (resp.IsSuccessStatusCode) await ShowTaskInstanceViewAsync(defId, instId); else AppendTaskLog("error", $"Cancel failed: {(int)resp.StatusCode} {resp.ReasonPhrase}", DateTimeOffset.Now); }
        catch (Exception ex) { AppendTaskLog("error", $"Cancel failed: {ex.Message}", DateTimeOffset.Now); }
    }

    private void OnTaskNewClick(object sender, RoutedEventArgs e)
    {
        StopTaskStream();
        _taskCreateNewMode = true;
        _selectedTaskDefinitionId = null;
        _selectedTaskInstanceId = null;
        _currentTaskDetail = null;
        TaskExecuteButton.Visibility = Visibility.Collapsed;
        TaskInstanceSelectorPanel.Visibility = Visibility.Collapsed;

        _suppressTaskSelection = true;
        TaskSelector.SelectedIndex = -1;
        _suppressTaskSelection = false;

        _suppressTaskDefSelection = true;
        TaskDefinitionSelector.SelectedIndex = 0;
        _suppressTaskDefSelection = false;

        TaskStatusBlock.Text = string.Empty;
        TaskNameBlock.Text = string.Empty;
        TaskTimestampBlock.Text = string.Empty;
        TaskSourceEditor.Text = DefaultTaskTemplate;

        ShowTaskEditorOrLogs();
    }

    private void ShowTaskEditorOrLogs()
    {
        if (_selectedTaskInstanceId is not null) { TaskEditorPanel.Visibility = Visibility.Collapsed; TaskLogsScroller.Visibility = Visibility.Visible; TaskNoInstancePlaceholder.Visibility = Visibility.Collapsed; TaskSubmitButton.Visibility = Visibility.Collapsed; }
        else if (_taskCreateNewMode) { TaskEditorPanel.Visibility = Visibility.Visible; TaskLogsScroller.Visibility = Visibility.Collapsed; TaskNoInstancePlaceholder.Visibility = Visibility.Collapsed; TaskSubmitButton.Visibility = Visibility.Visible; TaskStopButton.Visibility = Visibility.Collapsed; TaskCancelButton.Visibility = Visibility.Collapsed; TaskCopyLogsButton.Visibility = Visibility.Collapsed; TaskCopyResultButton.Visibility = Visibility.Collapsed; }
        else { TaskEditorPanel.Visibility = Visibility.Collapsed; TaskLogsScroller.Visibility = Visibility.Collapsed; TaskNoInstancePlaceholder.Visibility = Visibility.Visible; TaskSubmitButton.Visibility = Visibility.Collapsed; TaskStopButton.Visibility = Visibility.Collapsed; TaskCancelButton.Visibility = Visibility.Collapsed; TaskCopyLogsButton.Visibility = Visibility.Collapsed; TaskCopyResultButton.Visibility = Visibility.Collapsed; }
    }

    private void StopTaskStream()
    {
        _taskStreamCts?.Cancel();
        _taskStreamCts?.Dispose();
        _taskStreamCts = null;
    }

    private async void OnTaskSubmitClick(object sender, RoutedEventArgs e)
    {
        var source = TaskSourceEditor.Text?.Trim();
        if (string.IsNullOrEmpty(source)) { TaskStatusBlock.Text = "✗ Source text is empty"; TaskStatusBlock.Foreground = Brush(0xFF4444); return; }

        var validationErrors = ValidateTaskSource(source);
        if (validationErrors.Count > 0) { TaskStatusBlock.Text = "✗ " + string.Join("\n✗ ", validationErrors); TaskStatusBlock.Foreground = Brush(0xFF4444); return; }

        TaskStatusBlock.Text = "submitting...";
        TaskStatusBlock.Foreground = Brush(0x999999);

        var api = App.Services!.GetRequiredService<SharpClawApiClient>();
        try
        {
            var body = JsonSerializer.Serialize(new { sourceText = source }, Json);
            var resp = await api.PostAsync("/tasks", new StringContent(body, Encoding.UTF8, "application/json"));
            if (resp.IsSuccessStatusCode)
            {
                using var stream = await resp.Content.ReadAsStreamAsync();
                using var doc = await JsonDocument.ParseAsync(stream);
                var newId = doc.RootElement.GetProperty("id").GetGuid();
                var newName = doc.RootElement.TryGetProperty("name", out var np) ? np.GetString() ?? "task" : "task";
                TaskStatusBlock.Text = $"✓ Created: {newName}";
                TaskStatusBlock.Foreground = Brush(0x00FF00);
                TaskSourceEditor.Text = DefaultTaskTemplate;
                _taskCreateNewMode = false;
                _selectedTaskDefinitionId = newId;
                await LoadTaskDefinitionsAsync();
                await LoadAllTaskInstancesAsync();
                ShowTaskEditorOrLogs();
            }
            else
            {
                using var errStream = await resp.Content.ReadAsStreamAsync();
                var errMsg = await TryExtractErrorAsync(errStream) ?? $"{(int)resp.StatusCode} {resp.ReasonPhrase}";
                TaskStatusBlock.Text = $"✗ {errMsg}";
                TaskStatusBlock.Foreground = Brush(0xFF4444);
            }
        }
        catch (Exception ex) { TaskStatusBlock.Text = $"✗ {ex.Message}"; TaskStatusBlock.Foreground = Brush(0xFF4444); }
    }

    private void OnTaskSourceTextChanged(object sender, TextChangedEventArgs e)
    {
        var source = TaskSourceEditor.Text?.Trim();
        if (string.IsNullOrEmpty(source)) { TaskStatusBlock.Text = string.Empty; return; }
        var errors = ValidateTaskSource(source);
        if (errors.Count > 0) { TaskStatusBlock.Text = "✗ " + string.Join("\n✗ ", errors); TaskStatusBlock.Foreground = Brush(0xFF4444); }
        else { TaskStatusBlock.Text = "✓ Valid"; TaskStatusBlock.Foreground = Brush(0x00FF00); }
    }

    private static List<string> ValidateTaskSource(string source)
    {
        var errors = new List<string>();
        var lines = source.Split('\n');
        if (!source.Contains("[Task(", StringComparison.Ordinal)) errors.Add("Missing [Task(\"Name\")] attribute on the class");
        if (!source.Contains("class ", StringComparison.Ordinal)) errors.Add("Missing class declaration");
        if (!source.Contains("RunAsync", StringComparison.Ordinal)) errors.Add("Missing entry point: public async Task RunAsync(CancellationToken ct)");
        if (source.Contains(": SharpClawTask", StringComparison.Ordinal) || source.Contains(":SharpClawTask", StringComparison.Ordinal)) errors.Add(FindLineRef(lines, "SharpClawTask") + "No base class needed — remove ': SharpClawTask'");
        if (source.Contains("ExecuteAsync", StringComparison.Ordinal)) errors.Add(FindLineRef(lines, "ExecuteAsync") + "Use 'RunAsync(CancellationToken ct)' instead of 'ExecuteAsync'");
        if (source.Contains("TaskContext", StringComparison.Ordinal)) errors.Add(FindLineRef(lines, "TaskContext") + "Use 'CancellationToken ct' parameter instead of 'TaskContext'");
        for (var i = 0; i < lines.Length; i++) { if (lines[i].Contains("ctx.", StringComparison.Ordinal)) errors.Add($"Line {i + 1}: Call methods directly (e.g. Log, Emit) — no 'ctx.' prefix needed"); }
        return errors;
    }

    private static string FindLineRef(string[] lines, string token)
    {
        for (var i = 0; i < lines.Length; i++) { if (lines[i].Contains(token, StringComparison.Ordinal)) return $"Line {i + 1}: "; }
        return "";
    }

    private async void OnRefreshTasksClick(object sender, RoutedEventArgs e)
    {
        await LoadTaskDefinitionsAsync();
        await LoadAllTaskInstancesAsync();
        if (_selectedTaskDefinitionId is { } defId) { await LoadTaskInstancesAsync(defId); if (_selectedTaskInstanceId is { } instId) await ShowTaskInstanceViewAsync(defId, instId); }
    }

    private void OnCopyTaskLogsClick(object sender, RoutedEventArgs e)
    {
        if (_currentTaskDetail?.Logs is not { Count: > 0 } logs) return;
        var sb = new StringBuilder();
        foreach (var log in logs) sb.AppendLine($"[{log.Timestamp.LocalDateTime:yyyy-MM-dd HH:mm:ss}] [{log.Level}] {log.Message}");
        TerminalUI.CopyToClipboard(sb.ToString());
    }

    private void OnCopyTaskResultClick(object sender, RoutedEventArgs e)
    {
        if (_currentTaskDetail is not { } detail) return;
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(detail.OutputSnapshotJson)) sb.AppendLine(detail.OutputSnapshotJson);
        if (!string.IsNullOrWhiteSpace(detail.ErrorMessage)) { if (sb.Length > 0) sb.AppendLine(); sb.AppendLine($"[error] {detail.ErrorMessage}"); }
        if (sb.Length > 0) TerminalUI.CopyToClipboard(sb.ToString());
    }

    private async Task StreamTaskEventsAsync(Guid defId, Guid instanceId, CancellationToken ct)
    {
        var api = App.Services!.GetRequiredService<SharpClawApiClient>();
        var dispatcher = DispatcherQueue;
        await Task.Delay(500, ct).ConfigureAwait(false);

        try
        {
            using var resp = await api.GetStreamAsync($"/tasks/{defId}/instances/{instanceId}/stream", ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                if (!ct.IsCancellationRequested)
                    dispatcher.TryEnqueue(async () => { await LoadAllTaskInstancesAsync(); if (_selectedTaskDefinitionId is { } d) await LoadTaskInstancesAsync(d); if (_selectedTaskInstanceId == instanceId) await ShowTaskInstanceViewAsync(defId, instanceId); });
                return;
            }

            using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var reader = new StreamReader(stream);

            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (line is null) break;
                if (!line.StartsWith("data:")) continue;

                try
                {
                    var payload = line.AsMemory(5);
                    using var doc = JsonDocument.Parse(payload);
                    var root = doc.RootElement;
                    var evtType = root.TryGetProperty("type", out var tp) ? tp.GetString() ?? "" : "";
                    var data = root.TryGetProperty("data", out var dp) ? dp.GetString() : null;
                    DateTimeOffset? ts = root.TryGetProperty("timestamp", out var tsp) && tsp.ValueKind == JsonValueKind.String ? DateTimeOffset.Parse(tsp.GetString()!) : null;

                    switch (evtType)
                    {
                        case "Log": dispatcher.TryEnqueue(() => { AppendTaskLog("info", TruncateForDisplay(data ?? ""), ts); TaskLogsScroller.UpdateLayout(); TaskLogsScroller.ChangeView(null, TaskLogsScroller.ScrollableHeight, null); }); break;
                        case "Output": dispatcher.TryEnqueue(() => { AppendTaskLog("result", TruncateForDisplay(data ?? ""), ts); TaskLogsScroller.UpdateLayout(); TaskLogsScroller.ChangeView(null, TaskLogsScroller.ScrollableHeight, null); }); break;
                        case "StatusChange": dispatcher.TryEnqueue(() => { var statusText = data ?? "unknown"; AppendTaskLog("info", $"status → {statusText}", ts); TaskStatusBlock.Text = $"status: {statusText}"; TaskStatusBlock.Foreground = Brush(statusText switch { "Completed" => 0x00FF00, "Failed" or "Cancelled" => 0xFF4444, "Running" => 0x00AAFF, "Paused" => 0xFFAA00, _ => 0x999999 }); TaskLogsScroller.UpdateLayout(); TaskLogsScroller.ChangeView(null, TaskLogsScroller.ScrollableHeight, null); }); break;
                        case "Done": dispatcher.TryEnqueue(async () => { await LoadAllTaskInstancesAsync(); if (_selectedTaskDefinitionId is { } d) await LoadTaskInstancesAsync(d); await ShowTaskInstanceViewAsync(defId, instanceId); }); return;
                    }
                }
                catch { /* malformed SSE line */ }
            }

            if (!ct.IsCancellationRequested)
                dispatcher.TryEnqueue(async () => { await LoadAllTaskInstancesAsync(); if (_selectedTaskDefinitionId is { } d) await LoadTaskInstancesAsync(d); if (_selectedTaskInstanceId == instanceId) await ShowTaskInstanceViewAsync(defId, instanceId); });
        }
        catch (OperationCanceledException) { /* expected on stop */ }
        catch { /* stream error — silently stop */ }
    }
}
