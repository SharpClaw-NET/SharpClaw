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

    private async void OnMessageKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter)
            return;

        if (_isSending || string.IsNullOrWhiteSpace(MessageInput.Text))
            return;

        e.Handled = true;
        await SendMessageAsync();
    }

    private async void OnSendClick(object sender, RoutedEventArgs e)
    {
        if (!_isSending && !string.IsNullOrWhiteSpace(MessageInput.Text))
            await SendMessageAsync();
    }

    private async void OnCancelClick(object sender, RoutedEventArgs e)
    {
        if (App.Services?.GetService<ClientActionDispatcher>() is not { } actions)
            return;

        try
        {
            await actions.RunCommandAsync(
                "client.chat.cancel",
                _ =>
                {
                    _streamCts?.Cancel();
                    return ValueTask.CompletedTask;
                });
        }
        catch
        {
            // The stream cancellation path reports the final visible state.
        }
    }

    private async Task SendMessageAsync()
    {
        var message = MessageInput.Text.Trim();
        if (message.Length == 0)
            return;

        using var cts = new CancellationTokenSource();
        ChatBubbleRow assistant = default;
        var accepted = false;

        await CommitUiStateAsync(_ =>
        {
            if (_isSending)
                return ValueTask.CompletedTask;

            accepted = true;
            _isSending = true;
            _streamCts = cts;
            MessageInput.Text = string.Empty;
            MessageInput.IsEnabled = false;
            SendButton.IsEnabled = false;
            CancelButton.Visibility = Visibility.Visible;
            AppendMessage("user", message);
            assistant = AppendMessage("assistant", string.Empty);
            ScrollToBottom();
            return ValueTask.CompletedTask;
        });

        if (!accepted)
            return;

        var streamState = new UnoSseStreamState();
        var api = App.Services!.GetRequiredService<SharpClawApiClient>();
        var body = JsonSerializer.Serialize(new UnoDirectChatRequest(message), Json);
        using var content = new StringContent(body, Encoding.UTF8, "application/json");

        try
        {
            await api.ConsumeStreamAsync(
                "POST",
                "/chat/stream",
                content,
                async (response, streamToken) =>
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        await CommitStreamStateAsync(
                            _ =>
                            {
                                assistant.Content.Text =
                                    $"Request failed: {(int)response.StatusCode} {response.ReasonPhrase}";
                                assistant.Content.Foreground = Brush(0xFF4444);
                                return ValueTask.CompletedTask;
                            },
                            CancellationToken.None);
                        return;
                    }

                    var contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
                    if (!contentType.Contains("event-stream", StringComparison.OrdinalIgnoreCase))
                    {
                        var fallback = await response.Content.ReadAsStringAsync(streamToken);
                        await CommitStreamStateAsync(
                            _ =>
                            {
                                assistant.Content.Text = TerminalUI.Truncate(fallback, 200);
                                assistant.Content.Foreground = Brush(0xFF4444);
                                return ValueTask.CompletedTask;
                            },
                            CancellationToken.None);
                        return;
                    }

                    await using var stream =
                        await response.Content.ReadAsStreamAsync(streamToken);
                    await ReadSseStreamAsync(stream, streamState, assistant, streamToken);
                },
                cts.Token);
        }
        catch (OperationCanceledException)
        {
            await CommitStreamStateAsync(
                _ =>
                {
                    assistant.Content.Text = streamState.Text.Length == 0
                        ? "(cancelled)"
                        : streamState.Text;
                    return ValueTask.CompletedTask;
                },
                CancellationToken.None);
        }
        catch (Exception exception)
        {
            await CommitStreamStateAsync(
                _ =>
                {
                    assistant.Content.Text = streamState.Text.Length == 0
                        ? $"Request failed: {TerminalUI.Truncate(exception.Message, 200)}"
                        : streamState.Text + $"\nRequest failed: {TerminalUI.Truncate(exception.Message, 200)}";
                    assistant.Content.Foreground = Brush(0xFF4444);
                    return ValueTask.CompletedTask;
                },
                CancellationToken.None);
        }
        finally
        {
            await CommitUiStateAsync(_ =>
            {
                if (ReferenceEquals(_streamCts, cts))
                    _streamCts = null;
                _isSending = false;
                MessageInput.IsEnabled = true;
                SendButton.IsEnabled = true;
                CancelButton.Visibility = Visibility.Collapsed;
                MessageInput.Focus(FocusState.Programmatic);
                ScrollToBottom();
                return ValueTask.CompletedTask;
            });
        }
    }

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
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line.Length == 0)
            {
                if (eventType is not null && eventData is not null &&
                    await ApplySseEventAsync(state, eventType, eventData, assistant, cancellationToken))
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

        if (eventType is not null && eventData is not null)
            await ApplySseEventAsync(state, eventType, eventData, assistant, cancellationToken);

        await CommitStreamStateAsync(
            _ =>
            {
                if (!state.DoneReceived && !state.ErrorReceived)
                    assistant.Content.Text = state.Text.Length == 0 ? "(no response)" : state.Text;
                return ValueTask.CompletedTask;
            },
            CancellationToken.None);
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
            cancellationToken);
        return shouldEnd;
    }

    private async Task CommitStreamStateAsync(
        Func<CancellationToken, ValueTask> mutation,
        CancellationToken cancellationToken)
    {
        var actions = App.Services!.GetRequiredService<ClientActionDispatcher>();
        const string stateKey = "client.chat.stream";
        await actions.CommitStateAsync(
            stateKey,
            actions.GetStateVersion(stateKey),
            mutation,
            cancellationToken);
    }
}
