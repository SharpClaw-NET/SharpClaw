namespace SharpClaw.Runtime.Host;


internal sealed record RuntimeCliCommand(
    string Name,
    IReadOnlyList<string> Arguments);
