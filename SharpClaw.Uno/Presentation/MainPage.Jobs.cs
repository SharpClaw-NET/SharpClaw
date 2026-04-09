using System.Buffers;
using System.Text;
using System.Text.Json;
using SharpClaw.Helpers;
using SharpClaw.Services;

namespace SharpClaw.Presentation;

// Jobs view: loading, display, controls, clipboard copy, loading animation.
public sealed partial class MainPage
{
    // ── Jobs ─────────────────────────────────────────────────────

    private async Task LoadJobsAsync(Guid channelId)
    {
        _suppressJobSelection = true;
        JobSelector.Items.Clear();

        JobSelector.Items.Add(_jobNoSelItem);

        var api = App.Services!.GetRequiredService<SharpClawApiClient>();
        try
        {
            using var resp = await api.GetAsync($"/channels/{channelId}/jobs/summaries");
            if (resp.IsSuccessStatusCode)
            {
                using var jobStream = await resp.Content.ReadAsStreamAsync();
                _channelJobs = await JsonSerializer.DeserializeAsync<List<JobDto>>(jobStream, Json) ?? [];

                _jobItemPoolUsed = 0;
                foreach (var job in _channelJobs)
                {
                    var label = $"[{job.Status}] {job.ActionKey}";
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

            StopLoadingAnimation();
            _jobLogPoolUsed = 0;
            JobLogsPanel.Children.Clear();

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
            JobActionBlock.Text = $"action: {job.ActionKey}";
            _jobTimestampParts.Clear();
            if (job.CreatedAt != default) _jobTimestampParts.Add($"created: {job.CreatedAt.LocalDateTime:yyyy-MM-dd HH:mm:ss}");
            if (job.StartedAt.HasValue) _jobTimestampParts.Add($"started: {job.StartedAt.Value.LocalDateTime:HH:mm:ss}");
            if (job.CompletedAt.HasValue) _jobTimestampParts.Add($"completed: {job.CompletedAt.Value.LocalDateTime:HH:mm:ss}");
            JobTimestampBlock.Text = string.Join("  |  ", _jobTimestampParts);

            if (job.Logs is { Count: > 0 })
            {
                foreach (var log in job.Logs)
                    AppendJobLog(log.Level, TruncateForDisplay(log.Message), log.Timestamp);
            }

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

            if (!string.IsNullOrWhiteSpace(job.ResultData))
                await AppendJobResultAsync(job.ResultData);
            if (!string.IsNullOrWhiteSpace(job.ErrorLog))
                AppendJobLog("error", TruncateForDisplay(job.ErrorLog), null);

            if (job.Logs is { Count: 0 } && job.Segments is null or { Count: 0 }
                && string.IsNullOrWhiteSpace(job.ResultData) && string.IsNullOrWhiteSpace(job.ErrorLog))
                AppendJobLog("info", "(no log entries yet)", null);

            if (job.ChannelCost is { } jobCost)
                RenderInlineCost(jobCost, null);

            if (job.Status == "AwaitingApproval")
            {
                JobApproveButton.Visibility = Visibility.Visible;
                JobCancelButton.Visibility = Visibility.Visible;
            }
            else if (job.Status is "Queued" or "Executing")
            {
                JobCancelButton.Visibility = Visibility.Visible;
                JobStopButton.Visibility = Visibility.Visible;
                JobPauseButton.Visibility = Visibility.Visible;
            }
            else if (job.Status == "Paused")
            {
                JobResumeButton.Visibility = Visibility.Visible;
                JobCancelButton.Visibility = Visibility.Visible;
            }

            _currentJobDetail = job;
            CopyLogsButton.Visibility = job.Logs is { Count: > 0 } ? Visibility.Visible : Visibility.Collapsed;
            CopySegmentsButton.Visibility = job.Segments is { Count: > 0 } ? Visibility.Visible : Visibility.Collapsed;
            CopyResultButton.Visibility = !string.IsNullOrWhiteSpace(job.ResultData) || !string.IsNullOrWhiteSpace(job.ErrorLog)
                ? Visibility.Visible : Visibility.Collapsed;
            CopyAllButton.Visibility = Visibility.Visible;
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

    private void DeallocateJobView()
    {
        StopLoadingAnimation();

        if (_screenshotImage is not null)
            _screenshotImage.Source = null;

        _pooledScreenshotStream.SetLength(0);
        if (_pooledScreenshotStream.Capacity > 2 * 1024 * 1024)
            _pooledScreenshotStream.Capacity = 512 * 1024;

        _jobLogPoolUsed = 0;
        JobLogsPanel.Children.Clear();
        JobApproveButton.Visibility = Visibility.Collapsed;
        JobCancelButton.Visibility = Visibility.Collapsed;
        JobStopButton.Visibility = Visibility.Collapsed;
        JobPauseButton.Visibility = Visibility.Collapsed;
        JobResumeButton.Visibility = Visibility.Collapsed;
        CopyLogsButton.Visibility = Visibility.Collapsed;
        CopySegmentsButton.Visibility = Visibility.Collapsed;
        CopyResultButton.Visibility = Visibility.Collapsed;
        CopyAllButton.Visibility = Visibility.Collapsed;
        _currentJobDetail = null;
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

    private const string ScreenshotMarker = "[SCREENSHOT_BASE64]";

    private async Task AppendJobResultAsync(string resultData)
    {
        var markerIndex = resultData.IndexOf(ScreenshotMarker, StringComparison.Ordinal);
        if (markerIndex >= 0)
        {
            var textPart = resultData[..markerIndex].TrimEnd();
            if (!string.IsNullOrWhiteSpace(textPart))
                AppendJobLog("result", textPart, null);

            var base64Span = resultData.AsSpan(markerIndex + ScreenshotMarker.Length);
            try
            {
                var maxBytes = (base64Span.Length * 3 / 4) + 4;
                var rented = ArrayPool<byte>.Shared.Rent(maxBytes);
                int written;
                try
                {
                    if (!Convert.TryFromBase64Chars(base64Span, rented, out written))
                        throw new FormatException("Invalid base64 screenshot data.");

                    _pooledScreenshotStream.SetLength(0);
                    _pooledScreenshotStream.Write(rented, 0, written);
                    _pooledScreenshotStream.Position = 0;
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(rented);
                }

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

    // ── Job action buttons ──────────────────────────────────────

    private async void OnJobApproveClick(object sender, RoutedEventArgs e)
    {
        if (_selectedChannelId is not { } channelId || _selectedJobId is not { } jobId) return;
        var api = App.Services!.GetRequiredService<SharpClawApiClient>();
        try
        {
            var body = JsonSerializer.Serialize(new { }, Json);
            var content = new StringContent(body, Encoding.UTF8, "application/json");
            var resp = await api.PostAsync($"/channels/{channelId}/jobs/{jobId}/approve", content);
            if (resp.IsSuccessStatusCode) await ShowJobViewAsync(jobId);
            else AppendJobLog("error", $"Approve failed: {(int)resp.StatusCode} {resp.ReasonPhrase}", DateTimeOffset.Now);
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
            if (resp.IsSuccessStatusCode) await ShowJobViewAsync(jobId);
            else AppendJobLog("error", $"Cancel failed: {(int)resp.StatusCode} {resp.ReasonPhrase}", DateTimeOffset.Now);
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
            if (resp.IsSuccessStatusCode) await ShowJobViewAsync(jobId);
            else AppendJobLog("error", $"Stop failed: {(int)resp.StatusCode} {resp.ReasonPhrase}", DateTimeOffset.Now);
        }
        catch (Exception ex) { AppendJobLog("error", $"Stop failed: {ex.Message}", DateTimeOffset.Now); }
    }

    private async void OnJobPauseClick(object sender, RoutedEventArgs e)
    {
        if (_selectedChannelId is not { } channelId || _selectedJobId is not { } jobId) return;
        var api = App.Services!.GetRequiredService<SharpClawApiClient>();
        try
        {
            var resp = await api.PutAsync($"/channels/{channelId}/jobs/{jobId}/pause", null);
            if (resp.IsSuccessStatusCode) await ShowJobViewAsync(jobId);
            else AppendJobLog("error", $"Pause failed: {(int)resp.StatusCode} {resp.ReasonPhrase}", DateTimeOffset.Now);
        }
        catch (Exception ex) { AppendJobLog("error", $"Pause failed: {ex.Message}", DateTimeOffset.Now); }
    }

    private async void OnJobResumeClick(object sender, RoutedEventArgs e)
    {
        if (_selectedChannelId is not { } channelId || _selectedJobId is not { } jobId) return;
        var api = App.Services!.GetRequiredService<SharpClawApiClient>();
        try
        {
            var resp = await api.PutAsync($"/channels/{channelId}/jobs/{jobId}/resume", null);
            if (resp.IsSuccessStatusCode) await ShowJobViewAsync(jobId);
            else AppendJobLog("error", $"Resume failed: {(int)resp.StatusCode} {resp.ReasonPhrase}", DateTimeOffset.Now);
        }
        catch (Exception ex) { AppendJobLog("error", $"Resume failed: {ex.Message}", DateTimeOffset.Now); }
    }

    private void OnTabJobsClick(object sender, RoutedEventArgs e)
    {
        if (_jobsMode || _selectedChannelId is null) return;
        _jobsMode = true;
        _settingsMode = false;
        _tasksMode = false;
        _botsMode = false;
        UpdateTabHighlight();

        MessagesScroller.Visibility = Visibility.Collapsed;
        ChatInputArea.Visibility = Visibility.Collapsed;
        SettingsScroller.Visibility = Visibility.Collapsed;
        TaskViewPanel.Visibility = Visibility.Collapsed;
        DeallocateTaskView();
        BotViewPanel.Visibility = Visibility.Collapsed;
        AgentSelectorPanel.Visibility = Visibility.Collapsed;
        ThreadSelectorPanel.Visibility = Visibility.Collapsed;
        OneOffWarning.Visibility = Visibility.Collapsed;

        JobViewPanel.Visibility = Visibility.Visible;

        if (JobTemplateSelector.Items.Count == 0)
        {
            JobTemplateSelector.Items.Add(new ComboBoxItem
            {
                Content = "(not implemented)",
                IsEnabled = false,
            });
            JobTemplateSelector.SelectedIndex = 0;
        }

        if (_selectedJobId is null && _channelJobs.Count > 0)
        {
            var mostRecent = _channelJobs[0];
            _selectedJobId = mostRecent.Id;
            _suppressJobSelection = true;
            for (var i = 0; i < JobSelector.Items.Count; i++)
            {
                if (JobSelector.Items[i] is ComboBoxItem ci && ci.Tag is Guid g && g == mostRecent.Id)
                { JobSelector.SelectedIndex = i; break; }
            }
            _suppressJobSelection = false;
            _ = ShowJobViewAsync(mostRecent.Id);
        }
        else if (_selectedJobId is { } jid)
        {
            _ = ShowJobViewAsync(jid);
        }
    }

    // ── Job clipboard copy ──────────────────────────────────────

    private void OnCopyJobLogsClick(object sender, RoutedEventArgs e)
    {
        if (_currentJobDetail?.Logs is not { Count: > 0 } logs) return;
        var sb = new StringBuilder();
        foreach (var log in logs)
            sb.AppendLine($"[{log.Timestamp.LocalDateTime:yyyy-MM-dd HH:mm:ss}] [{log.Level}] {log.Message}");
        TerminalUI.CopyToClipboard(sb.ToString());
    }

    private void OnCopySegmentsClick(object sender, RoutedEventArgs e)
    {
        if (_currentJobDetail?.Segments is not { Count: > 0 } segments) return;
        var sb = new StringBuilder();
        foreach (var seg in segments)
        {
            var timeRange = $"[{FormatSegmentTime(seg.StartTime)} → {FormatSegmentTime(seg.EndTime)}]";
            var conf = seg.Confidence.HasValue ? $"  ({seg.Confidence.Value:P0})" : "";
            var prov = seg.IsProvisional ? "  [provisional]" : "";
            sb.AppendLine($"{timeRange}{conf}{prov}  {seg.Text}");
        }
        TerminalUI.CopyToClipboard(sb.ToString());
    }

    private void OnCopyResultClick(object sender, RoutedEventArgs e)
    {
        if (_currentJobDetail is not { } job) return;
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(job.ResultData))
        {
            var markerIndex = job.ResultData.IndexOf(ScreenshotMarker, StringComparison.Ordinal);
            sb.AppendLine(markerIndex >= 0 ? job.ResultData[..markerIndex].TrimEnd() : job.ResultData);
        }
        if (!string.IsNullOrWhiteSpace(job.ErrorLog))
        {
            if (sb.Length > 0) sb.AppendLine();
            sb.AppendLine($"[error] {job.ErrorLog}");
        }
        if (sb.Length > 0)
            TerminalUI.CopyToClipboard(sb.ToString());
    }

    private void OnCopyAllClick(object sender, RoutedEventArgs e)
    {
        if (_currentJobDetail is not { } job) return;
        var sb = new StringBuilder();

        sb.AppendLine($"Job: {job.Id}");
        sb.AppendLine($"Action: {job.ActionKey}  |  Status: {job.Status}");
        if (job.CreatedAt != default) sb.AppendLine($"Created: {job.CreatedAt.LocalDateTime:yyyy-MM-dd HH:mm:ss}");
        if (job.StartedAt.HasValue) sb.AppendLine($"Started: {job.StartedAt.Value.LocalDateTime:yyyy-MM-dd HH:mm:ss}");
        if (job.CompletedAt.HasValue) sb.AppendLine($"Completed: {job.CompletedAt.Value.LocalDateTime:yyyy-MM-dd HH:mm:ss}");

        if (job.Logs is { Count: > 0 } logs)
        {
            sb.AppendLine();
            sb.AppendLine("── Logs ──");
            foreach (var log in logs)
                sb.AppendLine($"[{log.Timestamp.LocalDateTime:yyyy-MM-dd HH:mm:ss}] [{log.Level}] {log.Message}");
        }

        if (job.Segments is { Count: > 0 } segments)
        {
            sb.AppendLine();
            sb.AppendLine($"── Transcription Segments ({segments.Count}) ──");
            foreach (var seg in segments)
            {
                var timeRange = $"[{FormatSegmentTime(seg.StartTime)} → {FormatSegmentTime(seg.EndTime)}]";
                var conf = seg.Confidence.HasValue ? $"  ({seg.Confidence.Value:P0})" : "";
                var prov = seg.IsProvisional ? "  [provisional]" : "";
                sb.AppendLine($"{timeRange}{conf}{prov}  {seg.Text}");
            }
        }

        if (!string.IsNullOrWhiteSpace(job.ResultData))
        {
            sb.AppendLine();
            sb.AppendLine("── Result ──");
            var markerIndex = job.ResultData.IndexOf(ScreenshotMarker, StringComparison.Ordinal);
            sb.AppendLine(markerIndex >= 0 ? job.ResultData[..markerIndex].TrimEnd() : job.ResultData);
        }

        if (!string.IsNullOrWhiteSpace(job.ErrorLog))
        {
            sb.AppendLine();
            sb.AppendLine("── Error ──");
            sb.AppendLine(job.ErrorLog);
        }

        TerminalUI.CopyToClipboard(sb.ToString());
    }
}
