namespace SharpClaw.Application.API;

/// <summary>
/// Coordinates command and request execution with database cold-storage initialization.
/// </summary>
public sealed class DatabaseInitializationGate
{
    private readonly TaskCompletionSource _initialized =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Gets whether cold-storage initialization has completed successfully.
    /// </summary>
    public bool IsInitialized => _initialized.Task.IsCompletedSuccessfully;

    /// <summary>
    /// Waits until cold-storage initialization has completed successfully.
    /// </summary>
    public Task WaitAsync(CancellationToken cancellationToken = default)
        => _initialized.Task.WaitAsync(cancellationToken);

    /// <summary>
    /// Marks cold-storage initialization as complete.
    /// </summary>
    public void MarkInitialized()
        => _initialized.TrySetResult();

    /// <summary>
    /// Marks cold-storage initialization as failed.
    /// </summary>
    public void MarkFailed(Exception exception)
        => _initialized.TrySetException(exception);
}
