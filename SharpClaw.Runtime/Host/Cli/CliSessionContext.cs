using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace SharpClaw.Runtime.Host.Cli;

internal sealed class CliSessionContext(
    TextWriter output,
    TextWriter error,
    ChannelReader<string?>? input)
{
    public TextWriter Output { get; } = output;

    public TextWriter Error { get; } = error;

    public ChannelReader<string?>? Input { get; } = input;

    public ILogger? Logger { get; set; }

    public string? CurrentUser { get; set; }

    public Guid? CurrentUserId { get; set; }

    public Guid? CurrentChannelId { get; set; }

    public Guid? CurrentThreadId { get; set; }

    public bool ChatMode { get; set; }

    public string? ReadLine()
    {
        if (Input is null)
            return System.Console.ReadLine();

        try
        {
            return Input.ReadAsync().AsTask().GetAwaiter().GetResult();
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
