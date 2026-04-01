using System.Text;
using System.Text.Json;
using SharpClaw.Helpers;
using SharpClaw.Services;

namespace SharpClaw.Presentation;

// Chat messages, history loading, sending, SSE streaming, cost bars,
// and thread activity watch (real-time updates from other clients).
public sealed partial class MainPage
{
    // ── Thread activity watch ────────────────────────────────────

    private void ConnectThreadWatch(Guid channelId, Guid threadId)
    {
        DisconnectThreadWatch();
        var cts = new CancellationTokenSource();
        _threadWatchCts = cts;
        _ = RunThreadWatchAsync(channelId, threadId, cts.Token);
    }

    private void DisconnectThreadWatch()
    {
        if (_threadWatchCts is not null)
        {
            _threadWatchCts.Cancel();
            _threadWatchCts.Dispose();
            _threadWatchCts = null;
        }
        _isThreadBusy = false;
    }

    private async Task RunThreadWatchAsync(Guid channelId, Guid threadId, CancellationToken ct)
    {
        var api = App.Services!.GetRequiredService<SharpClawApiClient>();
        try
        {
            using var resp = await api.GetStreamAsync(
                $"/channels/{channelId}/chat/threads/{threadId}/watch", ct);
            if (!resp.IsSuccessStatusCode) return;

            using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream);

            ReadOnlyMemory<char> eventTypeMem = default;

            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line is null) break;

                if (line.StartsWith("event: "))
                {
                    eventTypeMem = line.AsMemory(7);
                }
                else if (line.StartsWith("data: ") && eventTypeMem.Length > 0)
                {
                    var evtSpan = eventTypeMem.Span;

                    if (evtSpan.SequenceEqual("Processing"))
                    {
                        DispatcherQueue.TryEnqueue(() =>
                        {
                            _isThreadBusy = true;
                            if (!_isSending)
                            {
                                SendButton.IsEnabled = false;
                                MessageInput.IsEnabled = false;
                            }
                        });
                    }
                    else if (evtSpan.SequenceEqual("NewMessages"))
                    {
                        DispatcherQueue.TryEnqueue(async () =>
                        {
                            _isThreadBusy = false;
                            if (!_isSending)
                            {
                                SendButton.IsEnabled = true;
                                MessageInput.IsEnabled = true;
                            }
                            if (_selectedChannelId is { } chId)
                            {
                                await LoadHistoryAsync(chId);
                                await LoadCostAsync(chId);
                                ScrollToBottom();
                            }
                        });
                    }

                    eventTypeMem = default;
                }
            }
        }
        catch (OperationCanceledException) { /* normal disconnect */ }
        catch { /* swallow — server unreachable or stream ended */ }
    }
    // ── Cost bars ────────────────────────────────────────────────

    private async Task LoadCostAsync(Guid channelId)
    {
        var api = App.Services!.GetRequiredService<SharpClawApiClient>();

        ChannelCostDto? channelCost = null;
        try
        {
            using var resp = await api.GetAsync($"/channels/{channelId}/chat/cost");
            if (resp.IsSuccessStatusCode)
            {
                using var s = await resp.Content.ReadAsStreamAsync();
                channelCost = await JsonSerializer.DeserializeAsync<ChannelCostDto>(s, Json);
            }
        }
        catch { /* swallow */ }

        if (channelCost is null || channelCost.TotalTokens == 0)
        {
            CostPanel.Visibility = Visibility.Collapsed;
            return;
        }

        CostPanel.Visibility = Visibility.Visible;
        ChannelCostLabel.Text = $"channel tokens: {channelCost.TotalTokens:N0}  (prompt {channelCost.TotalPromptTokens:N0} + completion {channelCost.TotalCompletionTokens:N0})";
        RenderCostBreakdown(ChannelCostBreakdown, channelCost.AgentBreakdown, channelCost.TotalTokens);

        if (_selectedThreadId is { } threadId)
        {
            ThreadCostDto? threadCost = null;
            try
            {
                using var resp = await api.GetAsync($"/channels/{channelId}/chat/threads/{threadId}/cost");
                if (resp.IsSuccessStatusCode)
                {
                    using var s = await resp.Content.ReadAsStreamAsync();
                    threadCost = await JsonSerializer.DeserializeAsync<ThreadCostDto>(s, Json);
                }
            }
            catch { /* swallow */ }

            if (threadCost is not null && threadCost.TotalTokens > 0)
            {
                ThreadCostLabel.Visibility = Visibility.Visible;
                ThreadCostBreakdown.Visibility = Visibility.Visible;
                ThreadCostLabel.Text = $"thread tokens: {threadCost.TotalTokens:N0}  (prompt {threadCost.TotalPromptTokens:N0} + completion {threadCost.TotalCompletionTokens:N0})";
                RenderCostBreakdown(ThreadCostBreakdown, threadCost.AgentBreakdown, threadCost.TotalTokens);
            }
            else
            {
                ThreadCostLabel.Visibility = Visibility.Collapsed;
                ThreadCostBreakdown.Visibility = Visibility.Collapsed;
            }
        }
        else
        {
            ThreadCostLabel.Visibility = Visibility.Collapsed;
            ThreadCostBreakdown.Visibility = Visibility.Collapsed;
        }
    }

    private void RenderCostBreakdown(StackPanel panel, IReadOnlyList<AgentTokenBreakdownDto>? agents, int total)
    {
        panel.Children.Clear();
        if (agents is null || agents.Count == 0) return;

        foreach (var agent in agents)
        {
            var pct = total > 0 ? (double)agent.TotalTokens / total : 0;
            var barWidth = Math.Max(4, pct * 160);

            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };

            var bar = new Border
            {
                Width = barWidth,
                Height = 8,
                Background = Brush(0x00CC66),
                CornerRadius = new CornerRadius(2),
                VerticalAlignment = VerticalAlignment.Center
            };

            var label = new TextBlock
            {
                Text = $"{agent.AgentName}  {agent.TotalTokens:N0} ({pct:P0})",
                FontFamily = _monoFont,
                FontSize = 10,
                Foreground = Brush(0x888888),
                VerticalAlignment = VerticalAlignment.Center
            };

            row.Children.Add(bar);
            row.Children.Add(label);
            panel.Children.Add(row);
        }
    }

    private void RenderInlineCost(ChannelCostDto channelCost, ThreadCostDto? threadCost)
    {
        if (channelCost.TotalTokens == 0)
        {
            CostPanel.Visibility = Visibility.Collapsed;
            return;
        }

        CostPanel.Visibility = Visibility.Visible;
        ChannelCostLabel.Text = $"channel tokens: {channelCost.TotalTokens:N0}  (prompt {channelCost.TotalPromptTokens:N0} + completion {channelCost.TotalCompletionTokens:N0})";
        RenderCostBreakdown(ChannelCostBreakdown, channelCost.AgentBreakdown, channelCost.TotalTokens);

        if (threadCost is not null && threadCost.TotalTokens > 0)
        {
            ThreadCostLabel.Visibility = Visibility.Visible;
            ThreadCostBreakdown.Visibility = Visibility.Visible;
            ThreadCostLabel.Text = $"thread tokens: {threadCost.TotalTokens:N0}  (prompt {threadCost.TotalPromptTokens:N0} + completion {threadCost.TotalCompletionTokens:N0})";
            RenderCostBreakdown(ThreadCostBreakdown, threadCost.AgentBreakdown, threadCost.TotalTokens);
        }
        else
        {
            ThreadCostLabel.Visibility = Visibility.Collapsed;
            ThreadCostBreakdown.Visibility = Visibility.Collapsed;
        }
    }

    // ── Messages ─────────────────────────────────────────────────

    private async Task LoadHistoryAsync(Guid channelId)
    {
        _chatBubblePoolUsed = 0;
        MessagesPanel.Children.Clear();
        var api = App.Services!.GetRequiredService<SharpClawApiClient>();

        try
        {
            if (_selectedThreadId is not { } tid) return;

            var url = $"/channels/{channelId}/chat/threads/{tid}";
            using var resp = await api.GetAsync(url);
            if (!resp.IsSuccessStatusCode) return;

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

        row.Root.ContextFlyout = isSystem ? null : BuildRoleMenuFlyout(isUser, agentId);

        MessagesPanel.Children.Add(row.Root);
    }

    private void ScrollToBottom()
    {
        MessagesScroller.UpdateLayout();
        MessagesScroller.ChangeView(null, MessagesScroller.ScrollableHeight, null);
    }

    // ── Send ─────────────────────────────────────────────────────

    private void OnMessageKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter && !_isSending && !_isThreadBusy && !string.IsNullOrWhiteSpace(MessageInput.Text))
        {
            e.Handled = true;
            _ = SendMessageAsync();
        }
    }

    private void OnSendClick(object sender, RoutedEventArgs e)
    {
        if (!_isSending && !_isThreadBusy && !string.IsNullOrWhiteSpace(MessageInput.Text))
            _ = SendMessageAsync();
    }

    private async Task SendMessageAsync()
    {
        var text = MessageInput.Text.Trim();
        if (string.IsNullOrEmpty(text) || _isSending || _isThreadBusy) return;
        _isSending = true;
        SendButton.IsEnabled = false;
        MessageInput.IsEnabled = false;

        MessageInput.Text = string.Empty;
        var api = App.Services!.GetRequiredService<SharpClawApiClient>();

        // Auto-create channel + thread when none is selected
        if (_selectedChannelId is null)
        {
            try
            {
                var title = TerminalUI.Truncate(text, 10);
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
                var threadName = TerminalUI.Truncate(text, 10);
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

        _pooledStreamBuilder.Clear();
        if (_pooledStreamBuilder.Capacity > 32 * 1024)
            _pooledStreamBuilder.Capacity = 4096;
        var accumulated = _pooledStreamBuilder;
        var dispatcher = DispatcherQueue;

        ChannelCostDto? doneCostChannel = null;
        ThreadCostDto? doneCostThread = null;

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
            var lastFlushedLength = 0;

            await Task.Run(async () =>
            {
                while (true)
                {
                    var line = await reader.ReadLineAsync();
                    if (line is null) break;

                    if (line.StartsWith("event: "))
                    {
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
                            using var doc = JsonDocument.Parse(line.AsMemory(6));
                            if (doc.RootElement.TryGetProperty("finalResponse", out var fr))
                            {
                                if (fr.TryGetProperty("channelCost", out var cc) && cc.ValueKind == JsonValueKind.Object)
                                    doneCostChannel = JsonSerializer.Deserialize<ChannelCostDto>(cc.GetRawText(), Json);
                                if (fr.TryGetProperty("threadCost", out var tc) && tc.ValueKind == JsonValueKind.Object)
                                    doneCostThread = JsonSerializer.Deserialize<ThreadCostDto>(tc.GetRawText(), Json);
                            }

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
            _isSending = false;
            SendButton.IsEnabled = !_isThreadBusy;
            MessageInput.IsEnabled = !_isThreadBusy;
            if (doneCostChannel is not null)
                RenderInlineCost(doneCostChannel, doneCostThread);
            ScrollToBottom();
            UpdateCursor();
            DispatcherQueue.TryEnqueue(() => MessageInput.Focus(FocusState.Programmatic));
        }
    }
}
