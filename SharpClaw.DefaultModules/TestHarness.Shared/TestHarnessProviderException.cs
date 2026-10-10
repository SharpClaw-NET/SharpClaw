namespace SharpClaw.DefaultPackages.TestHarness;

public sealed class TestHarnessProviderException : Exception
{
    public TestHarnessProviderException() : base("Test harness provider failure.") { }
    public TestHarnessProviderException(string message) : base(message) { }
    public TestHarnessProviderException(string message, Exception innerException) : base(message, innerException) { }
}
