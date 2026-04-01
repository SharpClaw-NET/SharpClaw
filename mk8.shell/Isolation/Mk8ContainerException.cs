namespace Mk8.Shell.Isolation;

/// <summary>
/// Thrown when container isolation setup, teardown, or enforcement fails.
/// </summary>
public sealed class Mk8ContainerException : InvalidOperationException
{
    public Mk8ContainerException(string message)
        : base($"mk8.shell container error: {message}") { }

    public Mk8ContainerException(string message, Exception innerException)
        : base($"mk8.shell container error: {message}", innerException) { }
}
