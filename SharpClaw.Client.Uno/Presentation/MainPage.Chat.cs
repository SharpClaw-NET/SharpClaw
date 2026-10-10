using System.Text;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using SharpClaw.Helpers;
using SharpClaw.Services;

namespace SharpClaw.Presentation;

public sealed partial class MainPage
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private void OnMessageKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter || !_canChat || _isSending || string.IsNullOrWhiteSpace(MessageInput.Text)) return;
        e.Handled = true;
        ClientUiEvent.Observe(SendMessageAsync);
    }

    private void OnSendClick(object sender, RoutedEventArgs e)
    {
        if (_canChat && !_isSending && !string.IsNullOrWhiteSpace(MessageInput.Text))
            ClientUiEvent.Observe(SendMessageAsync);
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => ClientUiEvent.Observe(async () =>
    {
        if (App.Services?.GetService<ClientActionDispatcher>() is not { } actions) return;
        await actions.RunCommandAsync("client.chat.cancel", async _ =>
        {
            if (_streamCts is { IsCancellationRequested: false } stream) await stream.CancelAsync().ConfigureAwait(true);
        }, CancellationToken.None).ConfigureAwait(true);
    });

    private async Task SendMessageAsync()
    {
        // One owner covers readiness as well as the stream; repeated clicks do not queue sends.
        if (!await _sendGate.WaitAsync(0, CancellationToken.None).ConfigureAwait(true)) return;
        try { await SendMessageCoreAsync().ConfigureAwait(true); }
        finally { _sendGate.Release(); }
    }

    private async Task SendMessageCoreAsync()
    {
        var message = MessageInput.Text.Trim();
        if (!_canChat || _isSending || message.Length == 0 ||
            _pageLifetime is not { IsCancellationRequested: false } lifetime) return;
        var pageToken = lifetime.Token;
        if (!await RefreshChatAvailabilityAsync(pageToken).ConfigureAwait(true)) return;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(pageToken);
        try
        {
            var assistant = await BeginSendAsync(message, cts, pageToken).ConfigureAwait(true);
            if (assistant is null) return;
            await RunChatStreamAsync(message, assistant.Value, cts.Token).ConfigureAwait(true);
        }
        finally
        {
            // Admission may publish its CTS before a later action-receipt fault.
            // The exact CTS identity proves this operation owns the cleanup.
            if (ReferenceEquals(_streamCts, cts))
            {
                try
                {
                    await CommitUiStateAsync(_ =>
                    {
                        if (!ReferenceEquals(_pageLifetime, lifetime) || pageToken.IsCancellationRequested) return ValueTask.CompletedTask;
                        _isSending = false;
                        SetChatAvailability(_canChat);
                        CancelButton.Visibility = Visibility.Collapsed;
                        MessageInput.Focus(FocusState.Programmatic);
                        ScrollToBottom();
                        return ValueTask.CompletedTask;
                    }, CancellationToken.None).ConfigureAwait(true);
                }
                finally { if (ReferenceEquals(_streamCts, cts)) _streamCts = null; }
            }
        }
    }

    private async Task<ChatBubbleRow?> BeginSendAsync(string message, CancellationTokenSource cts, CancellationToken pageToken)
    {
        ChatBubbleRow assistant = default;
        var accepted = false;
        await CommitUiStateAsync(_ =>
        {
            if (_isSending || !_canChat || pageToken.IsCancellationRequested) return ValueTask.CompletedTask;
            accepted = true;
            _isSending = true;
            _streamCts = cts;
            _streamPageToken = pageToken;
            MessageInput.Text = string.Empty;
            MessageInput.IsEnabled = false;
            SendButton.IsEnabled = false;
            CancelButton.Visibility = Visibility.Visible;
            AppendMessage("user", message);
            assistant = AppendMessage("assistant", string.Empty);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(assistant.Content, "ChatAssistantResponse");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetItemStatus(assistant.Content, "streaming");
            ScrollToBottom();
            return ValueTask.CompletedTask;
        }, pageToken).ConfigureAwait(true);
        return accepted ? assistant : null;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031",
        Justification = "Every failed HTTP, SSE or client action is rendered as a failed stream receipt; cancellation has its separate visible outcome and the exception type is journalled.")]
    private async Task RunChatStreamAsync(string message, ChatBubbleRow assistant, CancellationToken token)
    {
        var state = new UnoSseStreamState();
        var api = App.Services!.GetRequiredService<SharpClawApiClient>();
        using var content = new StringContent(JsonSerializer.Serialize(new UnoDirectChatRequest(message), Json), Encoding.UTF8, "application/json");
        try
        {
            await api.ConsumeStreamAsync("POST", "/chat/stream", content,
                (response, streamToken) => ConsumeChatResponseAsync(response, state, assistant, streamToken), token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            await CommitStreamStateAsync(_ =>
            {
                assistant.Content.Text = state.Text.Length == 0 ? "(cancelled)" : state.Text;
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetItemStatus(assistant.Content, "cancelled");
                return ValueTask.CompletedTask;
            }, CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            ClientStartupDiagnostics.Current.Record(ClientStartupStage.UnhandledException, exception);
            await CommitStreamStateAsync(_ =>
            {
                assistant.Content.Text = state.Text.Length == 0
                    ? $"Request failed: {TerminalUI.Truncate(exception.Message, 200)}"
                    : state.Text + $"\nRequest failed: {TerminalUI.Truncate(exception.Message, 200)}";
                assistant.Content.Foreground = Brush(0xFF4444);
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetItemStatus(assistant.Content, "failed");
                return ValueTask.CompletedTask;
            }, CancellationToken.None).ConfigureAwait(true);
        }
    }

    private async Task ConsumeChatResponseAsync(
        HttpResponseMessage response, UnoSseStreamState state, ChatBubbleRow assistant, CancellationToken token)
    {
        if (!response.IsSuccessStatusCode)
        {
            await SetStreamFailureAsync(assistant, $"Request failed: {(int)response.StatusCode} {response.ReasonPhrase}").ConfigureAwait(true);
            return;
        }
        if (!(response.Content.Headers.ContentType?.MediaType ?? string.Empty).Contains("event-stream", StringComparison.OrdinalIgnoreCase))
        {
            var fallback = await response.Content.ReadAsStringAsync(token).ConfigureAwait(true);
            await SetStreamFailureAsync(assistant, TerminalUI.Truncate(fallback, 200)).ConfigureAwait(true);
            return;
        }
        var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(true);
        await using var streamAsyncDisposal = stream.ConfigureAwait(true);
        await ReadSseStreamAsync(stream, state, assistant, token).ConfigureAwait(true);
        if (!state.DoneReceived && !state.ErrorReceived)
            throw new InvalidDataException("The response stream ended before completion.");
        await CommitStreamStateAsync(_ =>
        {
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetItemStatus(assistant.Content, state.ErrorReceived ? "failed" : "complete");
            return ValueTask.CompletedTask;
        }, CancellationToken.None).ConfigureAwait(true);
    }

    private Task SetStreamFailureAsync(ChatBubbleRow assistant, string message) => CommitStreamStateAsync(_ =>
    {
        assistant.Content.Text = message;
        assistant.Content.Foreground = Brush(0xFF4444);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetItemStatus(assistant.Content, "failed");
        return ValueTask.CompletedTask;
    }, CancellationToken.None);

    private async Task ReadSseStreamAsync(
        Stream stream,
        UnoSseStreamState state,
        ChatBubbleRow assistant,
        CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(
            stream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 4096,
            leaveOpen: true);

        string? eventType = null;
        string? eventData = null;
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(true) is { } line)
        {
            if (line.Length == 0)
            {
                if (eventData is not null &&
                    await ApplySseEventAsync(state, eventType ?? string.Empty, eventData, assistant, cancellationToken).ConfigureAwait(true))
                    return;

                eventType = null;
                eventData = null;
                continue;
            }

            if (line.StartsWith("event: ", StringComparison.Ordinal))
                eventType = line[7..];
            else if (line.StartsWith("data: ", StringComparison.Ordinal))
                eventData = line[6..];
        }

        if (eventData is not null)
            await ApplySseEventAsync(state, eventType ?? string.Empty, eventData, assistant, cancellationToken).ConfigureAwait(true);

        await CommitStreamStateAsync(
            _ =>
            {
                if (!state.DoneReceived && !state.ErrorReceived)
                    assistant.Content.Text = state.Text.Length == 0 ? "(no response)" : state.Text;
                return ValueTask.CompletedTask;
            },
            CancellationToken.None).ConfigureAwait(true);
    }

    private async Task<bool> ApplySseEventAsync(
        UnoSseStreamState state,
        string eventType,
        string eventData,
        ChatBubbleRow assistant,
        CancellationToken cancellationToken)
    {
        var shouldEnd = false;
        await CommitStreamStateAsync(
            _ =>
            {
                var result = state.Apply(eventType, eventData);
                shouldEnd = result.ShouldEnd;
                assistant.Content.Text = state.ErrorReceived
                    ? $"{state.Text}\nError: {state.ErrorText}".Trim()
                    : state.Text + (shouldEnd ? string.Empty : "|");
                assistant.Content.Foreground = Brush(state.ErrorReceived ? 0xFF4444 : 0xCCCCCC);
                ScrollToBottom();
                return ValueTask.CompletedTask;
            },
            cancellationToken).ConfigureAwait(true);
        return shouldEnd;
    }

    private async Task CommitStreamStateAsync(
        Func<CancellationToken, ValueTask> mutation,
        CancellationToken cancellationToken)
    {
        var actions = App.Services!.GetRequiredService<ClientActionDispatcher>();
        const string stateKey = "client.chat.stream";
        var visitToken = _streamPageToken;
        await actions.CommitStateAsync(
            stateKey,
            actions.GetStateVersion(stateKey),
            ct => visitToken.IsCancellationRequested ? ValueTask.CompletedTask : mutation(ct),
            cancellationToken).ConfigureAwait(true);
    }
}
