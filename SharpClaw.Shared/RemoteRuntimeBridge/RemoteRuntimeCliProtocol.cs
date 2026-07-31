using System.Net.WebSockets;
using System.Text.Json;

namespace SharpClaw.Shared.RemoteRuntimeBridge;

public static class RemoteRuntimeCliFrameTypes
{
    public const string Command = "command";
    public const string Input = "input";
    public const string Cancel = "cancel";
    public const string Close = "close";
    public const string Prompt = "prompt";
    public const string Output = "output";
    public const string Error = "error";
    public const string Exit = "exit";
}

public sealed record RemoteRuntimeCliFrame(
    string Type,
    IReadOnlyList<string>? Arguments = null,
    string? Text = null,
    string? PromptId = null,
    int? ExitCode = null,
    bool? Handled = null,
    bool? Secret = null)
{
    public static RemoteRuntimeCliFrame CommandFrame(IReadOnlyList<string> arguments)
        => new(RemoteRuntimeCliFrameTypes.Command, Arguments: arguments);

    public static RemoteRuntimeCliFrame InputFrame(string promptId, string? text)
        => new(RemoteRuntimeCliFrameTypes.Input, Text: text, PromptId: promptId);

    public static RemoteRuntimeCliFrame CancelFrame()
        => new(RemoteRuntimeCliFrameTypes.Cancel);

    public static RemoteRuntimeCliFrame PromptFrame(
        string promptId,
        string? text,
        bool secret = false)
        => new(
            RemoteRuntimeCliFrameTypes.Prompt,
            Text: text,
            PromptId: promptId,
            Secret: secret);

    public static RemoteRuntimeCliFrame ExitFrame(int exitCode, bool handled)
        => new(
            RemoteRuntimeCliFrameTypes.Exit,
            ExitCode: exitCode,
            Handled: handled);

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Type))
            throw new InvalidOperationException("The CLI frame type is required.");

        if (Type.Equals(RemoteRuntimeCliFrameTypes.Command, StringComparison.OrdinalIgnoreCase))
        {
            if (Arguments is null || Arguments.Count == 0)
                throw new InvalidOperationException("A CLI command requires an argument array.");

            if (Text is not null || PromptId is not null || ExitCode is not null)
                throw new InvalidOperationException("A CLI command frame contains unsupported fields.");
        }
        else if (Type.Equals(RemoteRuntimeCliFrameTypes.Input, StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(PromptId))
                throw new InvalidOperationException("A CLI input frame requires a prompt identifier.");
        }
        else if (Type.Equals(RemoteRuntimeCliFrameTypes.Prompt, StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(PromptId))
                throw new InvalidOperationException("A CLI prompt frame requires a prompt identifier.");
        }
        else if (Type.Equals(RemoteRuntimeCliFrameTypes.Exit, StringComparison.OrdinalIgnoreCase))
        {
            if (ExitCode is null)
                throw new InvalidOperationException("A CLI exit frame requires an exit status.");
        }
    }
}

public sealed record RemoteRuntimeCliPrompt(
    string PromptId,
    string Text,
    bool Secret);

public sealed record RemoteRuntimeCliExitStatus(
    int ExitCode,
    bool Handled);

public sealed class RemoteRuntimeCliClient : IAsyncDisposable
{
    private const int ReceiveBufferSize = 8 * 1024;
    private const int MaximumFrameBytes = 64 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly ClientWebSocket socket;
    private readonly SemaphoreSlim sendGate = new(1, 1);
    private int disposed;

    private RemoteRuntimeCliClient(ClientWebSocket socket)
    {
        this.socket = socket;
    }

    public static async Task<RemoteRuntimeCliClient> ConnectAsync(
        Uri endpoint,
        Action<ClientWebSocketOptions>? configure = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var socket = new ClientWebSocket();
        configure?.Invoke(socket.Options);
        await socket.ConnectAsync(endpoint, cancellationToken);
        return new RemoteRuntimeCliClient(socket);
    }

    public async Task<RemoteRuntimeCliExitStatus> RunAsync(
        IReadOnlyList<string> arguments,
        Func<RemoteRuntimeCliPrompt, ValueTask<string?>>? prompt = null,
        Action<string>? output = null,
        Action<string>? error = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Count == 0)
            throw new ArgumentException("The CLI argument array cannot be empty.", nameof(arguments));

        await SendAsync(
            RemoteRuntimeCliFrame.CommandFrame(arguments),
            cancellationToken);

        using var cancellationRegistration = cancellationToken.Register(
            static state => _ = ((RemoteRuntimeCliClient)state!).TrySendCancelAsync(),
            this);

        while (true)
        {
            var frame = await ReceiveAsync(cancellationToken);
            switch (frame.Type.ToLowerInvariant())
            {
                case RemoteRuntimeCliFrameTypes.Prompt:
                    frame.Validate();
                    if (prompt is null)
                    {
                        await SendAsync(
                            RemoteRuntimeCliFrame.InputFrame(frame.PromptId!, string.Empty),
                            cancellationToken);
                        break;
                    }

                    var response = await prompt(
                        new RemoteRuntimeCliPrompt(
                            frame.PromptId!,
                            frame.Text ?? string.Empty,
                            frame.Secret == true));
                    await SendAsync(
                        RemoteRuntimeCliFrame.InputFrame(frame.PromptId!, response),
                        cancellationToken);
                    break;

                case RemoteRuntimeCliFrameTypes.Output:
                    output?.Invoke(frame.Text ?? string.Empty);
                    break;

                case RemoteRuntimeCliFrameTypes.Error:
                    error?.Invoke(frame.Text ?? string.Empty);
                    break;

                case RemoteRuntimeCliFrameTypes.Exit:
                    frame.Validate();
                    return new RemoteRuntimeCliExitStatus(
                        frame.ExitCode!.Value,
                        frame.Handled == true);

                default:
                    throw new InvalidOperationException(
                        $"The CLI server returned unsupported frame type '{frame.Type}'.");
            }
        }
    }

    public async Task CancelAsync(CancellationToken cancellationToken = default)
        => await SendAsync(RemoteRuntimeCliFrame.CancelFrame(), cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;

        try
        {
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                await socket.CloseAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "CLI session closed",
                    CancellationToken.None);
            }
        }
        catch (WebSocketException)
        {
        }
        finally
        {
            socket.Dispose();
            sendGate.Dispose();
        }
    }

    private async Task TrySendCancelAsync()
    {
        try
        {
            await SendAsync(RemoteRuntimeCliFrame.CancelFrame(), CancellationToken.None);
        }
        catch (Exception) when (socket.State is WebSocketState.Aborted or WebSocketState.Closed)
        {
        }
    }

    private async Task SendAsync(
        RemoteRuntimeCliFrame frame,
        CancellationToken cancellationToken)
    {
        frame.Validate();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(frame, JsonOptions);
        await sendGate.WaitAsync(cancellationToken);
        try
        {
            await socket.SendAsync(
                bytes,
                WebSocketMessageType.Text,
                endOfMessage: true,
                cancellationToken);
        }
        finally
        {
            sendGate.Release();
        }
    }

    private async Task<RemoteRuntimeCliFrame> ReceiveAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[ReceiveBufferSize];
        using var message = new MemoryStream();
        ValueWebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer.AsMemory(), cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
                throw new WebSocketException("The CLI server closed the session before exit.");

            if (result.MessageType != WebSocketMessageType.Text
                || message.Length + result.Count > MaximumFrameBytes)
            {
                throw new InvalidOperationException("The CLI server returned an invalid frame.");
            }

            await message.WriteAsync(buffer.AsMemory(0, result.Count), cancellationToken);
        }
        while (!result.EndOfMessage);

        var frame = JsonSerializer.Deserialize<RemoteRuntimeCliFrame>(
            message.GetBuffer().AsSpan(0, checked((int)message.Length)),
            JsonOptions);
        if (frame is null)
            throw new InvalidOperationException("The CLI server returned an empty frame.");

        return frame;
    }
}
