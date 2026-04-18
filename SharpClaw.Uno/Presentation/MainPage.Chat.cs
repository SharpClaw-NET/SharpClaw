using System.Diagnostics;
using System.Text;
using System.Text.Json;
using SharpClaw.Helpers;
using SharpClaw.Services;

namespace SharpClaw.Presentation;

// Chat messages, history loading, sending, SSE streaming, cost bars,
// and thread activity watch (real-time updates from other clients).
public sealed partial class MainPage
{
    private const string ChatLogCategory = "SharpClaw.Chat";

    [Conditional("DEBUG")]
    private static void ChatLog(string message) => Debug.WriteLine(message, ChatLogCategory);

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
        if (_streamCts is not null)
        {
            _streamCts.Cancel();
            _streamCts.Dispose();
            _streamCts = null;
        }
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
        ChatLog($"[ThreadWatch] Connecting watch for channel={channelId} thread={threadId}");
        try
        {
            using var resp = await api.GetStreamAsync(
                $"/channels/{channelId}/chat/threads/{threadId}/watch", ct);
            if (!resp.IsSuccessStatusCode)
            {
                ChatLog($"[ThreadWatch] Watch returned {(int)resp.StatusCode}");
                return;
            }

            ChatLog("[ThreadWatch] Connected, reading SSE events...");
            using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream);

            ReadOnlyMemory<char> eventTypeMem = default;

            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line is null) { ChatLog("[ThreadWatch] Stream ended (null)"); break; }
                if (line.Length == 0) continue;

                ChatLog($"[ThreadWatch] SSE: {line}");

                if (line.StartsWith("event: "))
                {
                    eventTypeMem = line.AsMemory(7);
                }
                else if (line.StartsWith("data: ") && eventTypeMem.Length > 0)
                {
                    var evtSpan = eventTypeMem.Span;

                    if (evtSpan.SequenceEqual("Processing"))
                    {
                        ChatLog("[ThreadWatch] Event: Processing");
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
                        ChatLog("[ThreadWatch] Event: NewMessages");
                        DispatcherQueue.TryEnqueue(async () =>
                        {
                            _isThreadBusy = false;
                            if (!_isSending)
                            {
                                SendButton.IsEnabled = true;
                                MessageInput.IsEnabled = true;
                            }

                            if (_isSending)
                            {
                                ChatLog("[ThreadWatch] NewMessages during send — flagging stale");
                                _historyStaleAfterSend = true;
                                return;
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
        catch (OperationCanceledException) { ChatLog("[ThreadWatch] Cancelled (normal)"); }
        catch (Exception ex) { ChatLog($"[ThreadWatch] Error: {ex.GetType().Name}: {ex.Message}"); }
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
        ChatLog($"[History] LoadHistoryAsync channel={channelId} thread={_selectedThreadId}");
        _chatBubblePoolUsed = 0;
        MessagesPanel.Children.Clear();
        var api = App.Services!.GetRequiredService<SharpClawApiClient>();

        try
        {
            if (_selectedThreadId is not { } tid) return;

            var url = $"/channels/{channelId}/chat/threads/{tid}";
            ChatLog($"[History] GET {url}");
            using var resp = await api.GetAsync(url);
            if (!resp.IsSuccessStatusCode)
            {
                ChatLog($"[History] Failed: {(int)resp.StatusCode}");
                return;
            }

            using var contentStream = await resp.Content.ReadAsStreamAsync();
            var messages = await JsonSerializer.DeserializeAsync<List<ChatMessageDto>>(
                contentStream, Json);

            if (messages is null) { ChatLog("[History] Null response"); return; }
            ChatLog($"[History] Loaded {messages.Count} messages");

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
        catch (Exception ex) { ChatLog($"[History] Error: {ex.GetType().Name}: {ex.Message}"); }

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

    private void ScrollToBottom(bool forceLayout = true)
    {
        if (forceLayout)
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

        // ── Streaming bubble ─────────────────────────────────────
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
        ScrollToBottom();

        try
        {
            await StreamChatResponseAsync(channelId, text, streamBubble);
        }
        finally
        {
            _isSending = false;
            _historyStaleAfterSend = false;
            SendButton.IsEnabled = !_isThreadBusy;
            MessageInput.IsEnabled = !_isThreadBusy;
            UpdateCursor();
            DispatcherQueue.TryEnqueue(() => MessageInput.Focus(FocusState.Programmatic));
        }
    }

    // ── SSE Streaming ────────────────────────────────────────────

    private static readonly JsonSerializerOptions SseJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private async Task StreamChatResponseAsync(Guid channelId, string message, ChatBubbleRow bubble)
    {
        var api = App.Services!.GetRequiredService<SharpClawApiClient>();
        _pooledStreamBuilder.Clear();
        _needsNewlineBeforeNextDelta = false;

        var cts = new CancellationTokenSource();
        _streamCts = cts;
        var ct = cts.Token;

        var threadPart = _selectedThreadId is { } tid
            ? $"/threads/{tid}"
            : "";
        var url = $"/channels/{channelId}/chat{threadPart}/stream";
        ChatLog($"[Stream] POST {url}");

        var body = JsonSerializer.Serialize(new
        {
            message,
            agentId = _selectedAgentId,
            clientType = _clientType
        }, Json);
        var content = new StringContent(body, Encoding.UTF8, "application/json");

        var sw = Stopwatch.StartNew();
        HttpResponseMessage? resp = null;
        try
        {
            resp = await api.PostStreamAsync(url, content, ct);
            ChatLog($"[Stream] Headers: {(int)resp.StatusCode} in {sw.ElapsedMilliseconds}ms");

            if (!resp.IsSuccessStatusCode)
            {
                bubble.Content.Text = $"✗ {(int)resp.StatusCode} {resp.ReasonPhrase}";
                bubble.Content.Foreground = Brush(0xFF4444);
                return;
            }

            var contentType = resp.Content.Headers.ContentType?.MediaType ?? "";
            if (!contentType.Contains("event-stream", StringComparison.OrdinalIgnoreCase))
            {
                var fallback = await resp.Content.ReadAsStringAsync(ct);
                ChatLog($"[Stream] Unexpected content-type: {contentType}");
                bubble.Content.Text = $"✗ Unexpected response: {TerminalUI.Truncate(fallback, 200)}";
                bubble.Content.Foreground = Brush(0xFF4444);
                return;
            }

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            await ReadSseStreamAsync(stream, bubble, channelId, ct);
        }
        catch (OperationCanceledException)
        {
            ChatLog("[Stream] Cancelled");
            if (_pooledStreamBuilder.Length == 0)
                bubble.Content.Text = "(cancelled)";
            else
                bubble.Content.Text = _pooledStreamBuilder.ToString();
        }
        catch (Exception ex)
        {
            ChatLog($"[Stream] Error: {ex.GetType().Name}: {ex.Message}");
            bubble.Content.Text = _pooledStreamBuilder.Length > 0
                ? _pooledStreamBuilder.ToString() + $"\n✗ {ex.Message}"
                : $"✗ {ex.Message}";
            bubble.Content.Foreground = Brush(0xFF4444);
        }
        finally
        {
            sw.Stop();
            ChatLog($"[Stream] End: {sw.ElapsedMilliseconds}ms total");
            resp?.Dispose();
            if (_streamCts == cts)
                _streamCts = null;
            cts.Dispose();
        }
    }

    private async Task ReadSseStreamAsync(Stream stream, ChatBubbleRow bubble, Guid channelId, CancellationToken ct)
    {
        // Parsed SSE event produced by the background reader.
        var events = System.Threading.Channels.Channel.CreateUnbounded<(string EventType, string DataJson)>(
            new System.Threading.Channels.UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

        // Background task: read bytes from the network stream, decode UTF-8, extract SSE
        // lines, and push parsed (eventType, dataJson) pairs into the channel.
        // This keeps all blocking I/O off the UI thread.
        var readerTask = Task.Run(async () =>
        {
            var buffer = new byte[4096];
            var decoder = Encoding.UTF8.GetDecoder();
            var charBuf = new char[4096];
            var lineBuilder = new StringBuilder(512);
            string? currentEventType = null;
            var readCount = 0;

            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var bytesRead = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
                    if (bytesRead == 0)
                    {
                        ChatLog($"[Stream] Read #{readCount}: 0B (end of stream)");
                        break;
                    }

                    readCount++;
                    ChatLog($"[Stream] Read #{readCount}: {bytesRead}B");

                    var charsDecoded = decoder.GetChars(buffer.AsSpan(0, bytesRead), charBuf, flush: false);
                    lineBuilder.Append(charBuf, 0, charsDecoded);

                    while (TryExtractLine(lineBuilder, out var line))
                    {
                        if (line.Length == 0) { currentEventType = null; continue; }
                        if (line.StartsWith(':')) continue;

                        if (line.StartsWith("event: ", StringComparison.Ordinal))
                        {
                            currentEventType = line[7..];
                            continue;
                        }

                        if (line.StartsWith("data: ", StringComparison.Ordinal) && currentEventType is not null)
                        {
                            var dataJson = line[6..];
                            events.Writer.TryWrite((currentEventType, dataJson));
                            currentEventType = null;
                        }
                    }
                }
            }
            finally
            {
                events.Writer.Complete();
            }
        }, ct);

        // UI consumer: read parsed events from the channel and update the UI.
        var eventCount = 0;
        var doneReceived = false;

        await foreach (var (eventType, dataJson) in events.Reader.ReadAllAsync(ct))
        {
            eventCount++;
            ChatLog($"[Stream] SSE #{eventCount}: {eventType} {TerminalUI.Truncate(dataJson, 120)}");

            if (ProcessSseEvent(eventType, dataJson, bubble, channelId))
            {
                doneReceived = true;
                break;
            }

            if (!doneReceived)
                bubble.Content.Text = _pooledStreamBuilder.ToString() + "▍";
            ScrollToBottom(forceLayout: false);
        }

        // Wait for the background reader to finish cleanly.
        try { await readerTask.ConfigureAwait(false); }
        catch (OperationCanceledException) { /* expected on cancel */ }

        // Final text (no cursor)
        if (_pooledStreamBuilder.Length > 0)
            bubble.Content.Text = _pooledStreamBuilder.ToString();
        else if (!doneReceived)
            bubble.Content.Text = "(no response)";

        ScrollToBottom(forceLayout: true);

        if (!doneReceived)
        {
            ChatLog("[Stream] Ended without Done event, falling back to LoadCostAsync");
            await LoadCostAsync(channelId);
        }
    }

    /// <summary>
    /// Processes a single SSE event. Returns <c>true</c> if the stream should end (Done or Error received).
    /// </summary>
    private bool ProcessSseEvent(string eventType, string dataJson, ChatBubbleRow bubble, Guid channelId)
    {
        switch (eventType)
        {
            case "TextDelta":
            {
                try
                {
                    using var doc = JsonDocument.Parse(dataJson);
                    var delta = doc.RootElement.GetProperty("delta").GetString();
                    if (delta is not null)
                    {
                        if (_needsNewlineBeforeNextDelta)
                        {
                            _pooledStreamBuilder.Append('\n');
                            _needsNewlineBeforeNextDelta = false;
                        }
                        _pooledStreamBuilder.Append(delta);
                        ChatLog($"[Stream] Delta: +{delta.Length} total={_pooledStreamBuilder.Length}");
                    }
                }
                catch (Exception ex) { ChatLog($"[Stream] TextDelta parse error: {ex.Message}"); }
                return false;
            }

            case "ToolCallStart":
            {
                try
                {
                    using var doc = JsonDocument.Parse(dataJson);
                    var actionKey = doc.RootElement.GetProperty("job").GetProperty("actionKey").GetString() ?? "?";
                    var status = doc.RootElement.GetProperty("job").GetProperty("status").GetString() ?? "started";
                    _pooledStreamBuilder.Append($"\n⚙ [{actionKey}] → {status}");
                    _needsNewlineBeforeNextDelta = true;
                    ChatLog($"[Stream] Tool: ToolCallStart {actionKey} → {status}");
                }
                catch (Exception ex) { ChatLog($"[Stream] ToolCallStart parse error: {ex.Message}"); }
                return false;
            }

            case "ToolCallResult":
            {
                try
                {
                    using var doc = JsonDocument.Parse(dataJson);
                    var actionKey = doc.RootElement.GetProperty("result").GetProperty("actionKey").GetString() ?? "?";
                    var status = doc.RootElement.GetProperty("result").GetProperty("status").GetString() ?? "done";
                    _pooledStreamBuilder.Append($"\n⚙ [{actionKey}] → {status}");
                    _needsNewlineBeforeNextDelta = true;
                    ChatLog($"[Stream] Tool: ToolCallResult {actionKey} → {status}");
                }
                catch (Exception ex) { ChatLog($"[Stream] ToolCallResult parse error: {ex.Message}"); }
                return false;
            }

            case "ApprovalRequired":
            {
                try
                {
                    using var doc = JsonDocument.Parse(dataJson);
                    var actionKey = doc.RootElement.GetProperty("pendingJob").GetProperty("actionKey").GetString() ?? "?";
                    _pooledStreamBuilder.Append($"\n⏳ [{actionKey}] awaiting approval");
                    _needsNewlineBeforeNextDelta = true;
                    ChatLog($"[Stream] Tool: ApprovalRequired {actionKey}");
                }
                catch (Exception ex) { ChatLog($"[Stream] ApprovalRequired parse error: {ex.Message}"); }
                return false;
            }

            case "ApprovalResult":
            {
                try
                {
                    using var doc = JsonDocument.Parse(dataJson);
                    var actionKey = doc.RootElement.GetProperty("approvalOutcome").GetProperty("actionKey").GetString() ?? "?";
                    var status = doc.RootElement.GetProperty("approvalOutcome").GetProperty("status").GetString() ?? "resolved";
                    _pooledStreamBuilder.Append($"\n⚙ [{actionKey}] → {status}");
                    _needsNewlineBeforeNextDelta = true;
                    ChatLog($"[Stream] Tool: ApprovalResult {actionKey} → {status}");
                }
                catch (Exception ex) { ChatLog($"[Stream] ApprovalResult parse error: {ex.Message}"); }
                return false;
            }

            case "Error":
            {
                try
                {
                    using var doc = JsonDocument.Parse(dataJson);
                    var error = doc.RootElement.GetProperty("error").GetString() ?? "Unknown error";
                    ChatLog($"[Stream] Error event: {error}");
                    bubble.Content.Text = _pooledStreamBuilder.Length > 0
                        ? _pooledStreamBuilder.ToString() + $"\n✗ {error}"
                        : $"✗ {error}";
                    bubble.Content.Foreground = Brush(0xFF4444);
                }
                catch (Exception ex)
                {
                    ChatLog($"[Stream] Error parse error: {ex.Message}");
                    bubble.Content.Text = "✗ Stream error";
                    bubble.Content.Foreground = Brush(0xFF4444);
                }
                return true;
            }

            case "Done":
            {
                try
                {
                    using var doc = JsonDocument.Parse(dataJson);
                    var root = doc.RootElement;

                    if (root.TryGetProperty("finalResponse", out var fr))
                    {
                        // Extract final assistant text if available
                        if (fr.TryGetProperty("assistantMessage", out var am) &&
                            am.TryGetProperty("content", out var contentEl))
                        {
                            var finalText = contentEl.GetString();
                            if (finalText is not null)
                            {
                                _pooledStreamBuilder.Clear();
                                _pooledStreamBuilder.Append(finalText);
                            }
                        }

                        // Extract and render costs inline
                        if (fr.TryGetProperty("channelCost", out var ccEl))
                        {
                            var channelCost = ccEl.Deserialize<ChannelCostDto>(SseJson);
                            ThreadCostDto? threadCost = null;
                            if (fr.TryGetProperty("threadCost", out var tcEl))
                                threadCost = tcEl.Deserialize<ThreadCostDto>(SseJson);

                            if (channelCost is not null)
                            {
                                ChatLog($"[Stream] Done: ch={channelCost.TotalTokens} th={threadCost?.TotalTokens}");
                                RenderInlineCost(channelCost, threadCost);
                            }
                        }
                    }
                }
                catch (Exception ex) { ChatLog($"[Stream] Done parse error: {ex.Message}"); }

                // Set final text
                bubble.Content.Text = _pooledStreamBuilder.Length > 0
                    ? _pooledStreamBuilder.ToString()
                    : "(empty response)";
                return true;
            }

            default:
                ChatLog($"[Stream] Unknown event type: {eventType}");
                return false;
        }
    }

    private static bool TryExtractLine(StringBuilder sb, out string line)
    {
        for (var i = 0; i < sb.Length; i++)
        {
            if (sb[i] == '\n')
            {
                var len = i > 0 && sb[i - 1] == '\r' ? i - 1 : i;
                line = sb.ToString(0, len);
                sb.Remove(0, i + 1);
                return true;
            }
        }

        line = "";
        return false;
    }
}
