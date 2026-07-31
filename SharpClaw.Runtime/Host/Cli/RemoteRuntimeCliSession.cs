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
        var ct = sessionCancellation.Token;
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
            ct);
        var errorWriter = new RemoteRuntimeCliTextWriter(
            output.Writer,
            RemoteRuntimeCliFrameTypes.Error,
            ct);

        var receiveTask = ReceiveFramesAsync(socket, frames.Writer, ct);
        var sendTask = SendFramesAsync(socket, output.Reader, ct);
        var normalClose = false;

        try
        {
            using var session = CliDispatcher.BeginSession(outputWriter, errorWriter, promptInput.Reader);

            while (await frames.Reader.WaitToReadAsync(ct))
            {
                while (frames.Reader.TryRead(out var frame))
                {
                    if (frame.Type.Equals(RemoteRuntimeCliFrameTypes.Close, StringComparison.OrdinalIgnoreCase))
                    {
                        normalClose = true;
                        return;
                    }

                    if (frame.Type.Equals(RemoteRuntimeCliFrameTypes.Input, StringComparison.OrdinalIgnoreCase))
                    {
                        await promptInput.Writer.WriteAsync(frame.Text, ct);
                        continue;
                    }

                    if (!frame.Type.Equals(RemoteRuntimeCliFrameTypes.Command, StringComparison.OrdinalIgnoreCase))
                    {
                        await output.Writer.WriteAsync(
                            new RemoteRuntimeCliFrame(
                                RemoteRuntimeCliFrameTypes.Error,
                                "The CLI frame type is not supported."),
                            ct);
                        continue;
                    }

                    var args = CliDispatcher.ParseCommandLine(frame.Text ?? string.Empty);
                    if (args.Length == 0)
                        continue;

                    bool handled;
                    try
                    {
                        handled = await CliDispatcher.TryHandleAsync(args, services);
                    }
                    catch (Exception exception)
                    {
                        await output.Writer.WriteAsync(
                            new RemoteRuntimeCliFrame(
                                RemoteRuntimeCliFrameTypes.Error,
                                exception.Message),
                            ct);
                        handled = false;
                    }

                    await output.Writer.WriteAsync(
                        new RemoteRuntimeCliFrame(
                            RemoteRuntimeCliFrameTypes.Result,
                            Handled: handled),
                        ct);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        finally
        {
            promptInput.Writer.TryComplete();
            frames.Writer.TryComplete();
            output.Writer.TryComplete();

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
        }
    }

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
                                "The CLI bridge accepts text frames only."),
                            cancellationToken);
                        break;
                    }

                    if (message.Length + result.Count > MaximumFrameBytes)
                    {
                        await writer.WriteAsync(
                            new RemoteRuntimeCliFrame(
                                RemoteRuntimeCliFrameTypes.Error,
                                "The CLI frame exceeds the maximum size."),
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
                        "The CLI frame is not valid JSON."),
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
            => Publish(value.ToString());

        public override void Write(string? value)
            => Publish(value ?? string.Empty);

        public override void Write(ReadOnlySpan<char> buffer)
            => Publish(buffer.ToString());

        public override void Write(char[] buffer, int index, int count)
            => Publish(new string(buffer, index, count));

        public override void WriteLine()
            => Publish(string.Empty);

        public override void WriteLine(string? value)
            => Publish(value ?? string.Empty);

        private void Publish(string text)
        {
            try
            {
                writer.WriteAsync(
                        new RemoteRuntimeCliFrame(frameType, text),
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
