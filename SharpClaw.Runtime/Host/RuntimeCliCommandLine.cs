namespace SharpClaw.Runtime.Host;

internal static class RuntimeCliCommandLine
{
    private const string Switch = "--cli";

    public static bool IsRequested(IReadOnlyList<string> args) =>
        args.Any(static arg => string.Equals(arg, Switch, StringComparison.OrdinalIgnoreCase));

    public static RuntimeCliCommand Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var switchIndex = -1;
        for (var index = 0; index < args.Count; index++)
        {
            if (string.Equals(args[index], Switch, StringComparison.OrdinalIgnoreCase))
            {
                switchIndex = index;
                break;
            }
        }

        if (switchIndex < 0)
            throw new InvalidOperationException("The Runtime CLI switch was not supplied.");
        if (switchIndex + 1 >= args.Count)
            throw new InvalidOperationException("The Runtime CLI command was not supplied.");

#pragma warning disable CA1308 // Registration CLI names use lowercase wire identifiers; changing canonical casing would alter the parsed command contract.
        var command = args[switchIndex + 1].Trim().ToLowerInvariant();
#pragma warning restore CA1308
        if (command.Length == 0)
            throw new InvalidOperationException("The Runtime CLI command was empty.");

        return new RuntimeCliCommand(
            command,
            args.Skip(switchIndex + 2).ToArray());
    }
}
