namespace SharpClaw.Runtime.BLL.Modules.Foreign;

internal sealed class ForeignModuleStartupException : Exception
{
    public ForeignModuleStartupException(
        string message,
        ForeignModuleProcessOutput output,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Output = output;
    }

    public ForeignModuleProcessOutput Output { get; }
}
