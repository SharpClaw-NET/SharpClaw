using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace SharpClaw.Runtime.Host.Cli;

internal sealed class CliSessionContext(
    TextWriter output,
    TextWriter error,
    ChannelReader<string?>? input,
    Action<string, bool>? outputObserved,
    Action<string, string>? promptRequested,
    CancellationToken cancellationToken)
{
    public TextWriter Output { get; } = output;

    public TextWriter Error { get; } = error;

    public ChannelReader<string?>? Input { get; } = input;

    private readonly Action<string, bool>? outputObserved = outputObserved;
    private readonly Action<string, string>? promptRequested = promptRequested;
    private readonly CancellationToken cancellationToken = cancellationToken;
    private string pendingPromptText = string.Empty;
    private bool lastOutputWasLine;

    public ILogger? Logger { get; set; }

    public string? CurrentUser { get; set; }

    public Guid? CurrentUserId { get; set; }

    public Guid? CurrentChannelId { get; set; }

    public Guid? CurrentThreadId { get; set; }

    public bool ChatMode { get; set; }

    public void ObserveOutput(string text, bool line)
    {
        if (line)
        {
            pendingPromptText = text;
            lastOutputWasLine = true;
        }
        else if (text.Length > 0)
        {
            pendingPromptText = lastOutputWasLine
                ? text
                : pendingPromptText + text;
            lastOutputWasLine = false;
        }

        outputObserved?.Invoke(text, line);
    }

    public string? ReadLine()
    {
        if (Input is null)
            return System.Console.ReadLine();

        try
        {
            var promptId = Guid.NewGuid().ToString("N");
            var promptText = pendingPromptText;
            pendingPromptText = string.Empty;
            promptRequested?.Invoke(promptId, promptText);
            var value = Input.ReadAsync(cancellationToken).AsTask().GetAwaiter().GetResult();
            return value;
        }
        catch (ChannelClosedException)
        {
            return null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }
}

internal static class CliConsole
{
    private static TextWriter Output
        => CliDispatcher.CurrentSession?.Output ?? System.Console.Out;

    public static TextWriter Error
        => CliDispatcher.CurrentSession?.Error ?? System.Console.Error;

    public static bool IsInputRedirected
        => CliDispatcher.CurrentSession?.Input is null
            ? System.Console.IsInputRedirected
            : false;

    public static string? ReadLine()
        => CliDispatcher.CurrentSession?.ReadLine() ?? System.Console.ReadLine();

    public static void Write(string? value) => Output.Write(value);

    public static void Write(ReadOnlySpan<char> value) => Output.Write(value);

    public static void WriteLine() => Output.WriteLine();

    public static void WriteLine(string? value) => Output.WriteLine(value);
}
