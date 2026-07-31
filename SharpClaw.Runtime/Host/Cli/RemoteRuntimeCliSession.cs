using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using SharpClaw.Shared.RemoteRuntimeBridge;

namespace SharpClaw.Runtime.Host.Cli;

internal static class RemoteRuntimeCliSession
{
    private const int ReceiveBufferSize = 8 * 1024;
    private const int MaximumFrameBytes = 64 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static async Task RunAsync(
        WebSocket socket,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        using var sessionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var sessionToken = sessionCancellation.Token;
        var frames = Channel.CreateBounded<RemoteRuntimeCliFrame>(new BoundedChannelOptions(64)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true,
        });
        var promptInput = Channel.CreateBounded<string?>(new BoundedChannelOptions(64)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true,
        });
        var output = Channel.CreateBounded<RemoteRuntimeCliFrame>(new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
        var outputWriter = new RemoteRuntimeCliTextWriter(
            output.Writer,
            RemoteRuntimeCliFrameTypes.Output,
            sessionToken);
        var errorWriter = new RemoteRuntimeCliTextWriter(
            output.Writer,
            RemoteRuntimeCliFrameTypes.Error,
            sessionToken);

        var receiveTask = ReceiveFramesAsync(socket, frames.Writer, sessionToken);
        var sendTask = SendFramesAsync(socket, output.Reader, sessionToken);
        Task? commandTask = null;
        CancellationTokenSource? commandCancellation = null;
        string? currentPromptId = null;
        var normalClose = false;

        try
        {
            while (await frames.Reader.WaitToReadAsync(sessionToken))
            {
                while (frames.Reader.TryRead(out var frame))
                {
                    if (commandTask?.IsCompleted == true)
                    {
                        await commandTask;
                        commandTask = null;
                        commandCancellation?.Dispose();
                        commandCancellation = null;
                        Volatile.Write(ref currentPromptId, null);
                    }

                    if (frame.Type.Equals(RemoteRuntimeCliFrameTypes.Close, StringComparison.OrdinalIgnoreCase))
                    {
                        normalClose = true;
                        commandCancellation?.Cancel();
                        return;
                    }

                    if (frame.Type.Equals(RemoteRuntimeCliFrameTypes.Input, StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            frame.Validate();
                            if (!string.Equals(
                                    frame.PromptId,
                                    Volatile.Read(ref currentPromptId),
                                    StringComparison.Ordinal))
                            {
                                throw new InvalidOperationException(
                                    "The CLI input prompt is not active.");
                            }

                            Volatile.Write(ref currentPromptId, null);
                            await promptInput.Writer.WriteAsync(frame.Text, sessionToken);
                        }
                        catch (Exception exception) when (exception is InvalidOperationException)
                        {
                            await WriteErrorAsync(output.Writer, exception.Message, sessionToken);
                        }

                        continue;
                    }

                    if (frame.Type.Equals(RemoteRuntimeCliFrameTypes.Cancel, StringComparison.OrdinalIgnoreCase))
                    {
                        commandCancellation?.Cancel();
                        continue;
                    }

                    if (!frame.Type.Equals(RemoteRuntimeCliFrameTypes.Command, StringComparison.OrdinalIgnoreCase))
                    {
                        await WriteErrorAsync(
                            output.Writer,
                            "The CLI frame type is not supported.",
                            sessionToken);
                        continue;
                    }

                    try
                    {
                        frame.Validate();
                        if (commandTask is not null)
                            throw new InvalidOperationException("A CLI command is already running.");

                        commandCancellation = CancellationTokenSource.CreateLinkedTokenSource(sessionToken);
                        commandTask = ExecuteCommandAsync(
                            frame.Arguments!,
                            services,
                            outputWriter,
                            errorWriter,
                            promptInput.Reader,
                            output.Writer,
                            commandCancellation.Token,
                            sessionToken,
                            (promptId, _) => Volatile.Write(ref currentPromptId, promptId),
                            (promptId, text) => PublishPromptAsync(
                                output.Writer,
                                promptId,
                                text,
                                sessionToken));
                    }
                    catch (Exception exception) when (exception is InvalidOperationException)
                    {
                        await WriteErrorAsync(output.Writer, exception.Message, sessionToken);
                        await output.Writer.WriteAsync(
                            RemoteRuntimeCliFrame.ExitFrame(2, handled: false),
                            sessionToken);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (sessionToken.IsCancellationRequested)
        {
        }
        finally
        {
            commandCancellation?.Cancel();
            promptInput.Writer.TryComplete();
            frames.Writer.TryComplete();
            output.Writer.TryComplete();

            if (commandTask is not null)
            {
                try { await commandTask.WaitAsync(TimeSpan.FromSeconds(2)); }
                catch (Exception) { }
            }

            if (normalClose)
            {
                try { await sendTask.WaitAsync(TimeSpan.FromSeconds(2)); }
                catch (Exception) { }
            }

            sessionCancellation.Cancel();
            try { await receiveTask.WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (Exception) { }
            try { await sendTask.WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (Exception) { }

            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                try
                {
                    await socket.CloseAsync(
                        WebSocketCloseStatus.NormalClosure,
                        "CLI session closed",
                        CancellationToken.None);
                }
                catch (WebSocketException)
                {
                }
            }

            socket.Dispose();
            commandCancellation?.Dispose();
        }
    }

    private static async Task ExecuteCommandAsync(
        IReadOnlyList<string> arguments,
        IServiceProvider services,
        TextWriter outputWriter,
        TextWriter errorWriter,
        ChannelReader<string?> promptInput,
        ChannelWriter<RemoteRuntimeCliFrame> output,
        CancellationToken commandCancellation,
        CancellationToken sessionCancellation,
        Action<string, string> promptIdObserved,
        Func<string, string, ValueTask> promptRequested)
    {
        await Task.Yield();
        using var session = CliDispatcher.BeginSession(
            outputWriter,
            errorWriter,
            promptInput,
            cancellationToken: commandCancellation,
            promptRequested: (promptId, text) =>
            {
                promptIdObserved(promptId, text);
                promptRequested(promptId, text).AsTask().GetAwaiter().GetResult();
            });

        try
        {
            var handled = await CliDispatcher.TryHandleAsync(arguments.ToArray(), services);
            commandCancellation.ThrowIfCancellationRequested();
            await output.WriteAsync(
                RemoteRuntimeCliFrame.ExitFrame(handled ? 0 : 1, handled),
                sessionCancellation);
        }
        catch (OperationCanceledException) when (commandCancellation.IsCancellationRequested)
        {
            await WriteErrorAsync(output, "The CLI command was cancelled.", sessionCancellation);
            await output.WriteAsync(
                RemoteRuntimeCliFrame.ExitFrame(130, handled: false),
                sessionCancellation);
        }
        catch (Exception exception)
        {
            await WriteErrorAsync(output, exception.Message, sessionCancellation);
            await output.WriteAsync(
                RemoteRuntimeCliFrame.ExitFrame(1, handled: false),
                sessionCancellation);
        }
    }

    private static ValueTask PublishPromptAsync(
        ChannelWriter<RemoteRuntimeCliFrame> output,
        string promptId,
        string text,
        CancellationToken cancellationToken)
        => output.WriteAsync(
            RemoteRuntimeCliFrame.PromptFrame(promptId, text),
            cancellationToken);

    private static ValueTask WriteErrorAsync(
        ChannelWriter<RemoteRuntimeCliFrame> output,
        string text,
        CancellationToken cancellationToken)
        => output.WriteAsync(
            new RemoteRuntimeCliFrame(RemoteRuntimeCliFrameTypes.Error, Text: text),
            cancellationToken);

    private static async Task ReceiveFramesAsync(
        WebSocket socket,
        ChannelWriter<RemoteRuntimeCliFrame> writer,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[ReceiveBufferSize];
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                using var message = new MemoryStream();
                ValueWebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(buffer.AsMemory(), cancellationToken);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await writer.WriteAsync(
                            new RemoteRuntimeCliFrame(RemoteRuntimeCliFrameTypes.Close),
                            cancellationToken);
                        return;
                    }

                    if (result.MessageType != WebSocketMessageType.Text)
                    {
                        await writer.WriteAsync(
                            new RemoteRuntimeCliFrame(
                                RemoteRuntimeCliFrameTypes.Error,
                                Text: "The CLI bridge accepts text frames only."),
                            cancellationToken);
                        break;
                    }

                    if (message.Length + result.Count > MaximumFrameBytes)
                    {
                        await writer.WriteAsync(
                            new RemoteRuntimeCliFrame(
                                RemoteRuntimeCliFrameTypes.Error,
                                Text: "The CLI frame exceeds the maximum size."),
                            cancellationToken);
                        return;
                    }

                    await message.WriteAsync(buffer.AsMemory(0, result.Count), cancellationToken);
                }
                while (!result.EndOfMessage);

                if (result.MessageType != WebSocketMessageType.Text)
                    continue;

                RemoteRuntimeCliFrame? frame;
                try
                {
                    frame = JsonSerializer.Deserialize<RemoteRuntimeCliFrame>(
                        message.GetBuffer().AsSpan(0, checked((int)message.Length)),
                        JsonOptions);
                }
                catch (JsonException)
                {
                    frame = null;
                }

                await writer.WriteAsync(
                    frame ?? new RemoteRuntimeCliFrame(
                        RemoteRuntimeCliFrameTypes.Error,
                        Text: "The CLI frame is not valid JSON."),
                    cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (WebSocketException)
        {
        }
        finally
        {
            writer.TryComplete();
        }
    }

    private static async Task SendFramesAsync(
        WebSocket socket,
        ChannelReader<RemoteRuntimeCliFrame> reader,
        CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var frame in reader.ReadAllAsync(cancellationToken))
            {
                var bytes = JsonSerializer.SerializeToUtf8Bytes(frame, JsonOptions);
                await socket.SendAsync(
                    bytes,
                    WebSocketMessageType.Text,
                    endOfMessage: true,
                    cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (WebSocketException)
        {
        }
    }

    private sealed class RemoteRuntimeCliTextWriter(
        ChannelWriter<RemoteRuntimeCliFrame> writer,
        string frameType,
        CancellationToken cancellationToken) : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value)
            => Publish(value.ToString(), line: false);

        public override void Write(string? value)
            => Publish(value ?? string.Empty, line: false);

        public override void Write(ReadOnlySpan<char> buffer)
            => Publish(buffer.ToString(), line: false);

        public override void Write(char[] buffer, int index, int count)
            => Publish(new string(buffer, index, count), line: false);

        public override void WriteLine()
            => Publish(string.Empty, line: true);

        public override void WriteLine(string? value)
            => Publish(value ?? string.Empty, line: true);

        private void Publish(string text, bool line)
        {
            try
            {
                CliDispatcher.ObserveSessionOutput(text, line);
                writer.WriteAsync(
                        new RemoteRuntimeCliFrame(frameType, Text: text),
                        cancellationToken)
                    .AsTask()
                    .GetAwaiter()
                    .GetResult();
            }
            catch (ChannelClosedException)
            {
            }
            catch (OperationCanceledException)
            {
            }
        }
    }
}
