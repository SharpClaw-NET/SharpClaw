namespace SharpClaw.Shared.RemoteRuntimeBridge;

public static class RemoteRuntimeCliFrameTypes
{
    public const string Command = "command";
    public const string Input = "input";
    public const string Close = "close";
    public const string Output = "output";
    public const string Error = "error";
    public const string Result = "result";
}

public sealed record RemoteRuntimeCliFrame(
    string Type,
    string? Text = null,
    bool? Handled = null);
