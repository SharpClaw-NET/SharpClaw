namespace SharpClaw.Shared.Instances;

/// <summary>
/// Acquires exclusive ownership of an instance root for the current process.
/// </summary>
public sealed class SharpClawInstanceLock : IDisposable
{
    private readonly FileStream _lockStream;
    private int _disposeState;

    public SharpClawInstanceLock(SharpClawInstancePaths instancePaths)
        : this(instancePaths, static path => new FileStream(
            path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
    {
    }

    internal SharpClawInstanceLock(SharpClawInstancePaths instancePaths, Func<string, FileStream> acquireStream)
    {
        ArgumentNullException.ThrowIfNull(instancePaths);
        ArgumentNullException.ThrowIfNull(acquireStream);

        instancePaths.EnsureDirectories();
        LockFilePath = Path.Combine(instancePaths.InstanceRoot, ".instance.lock");

        try
        {
            _lockStream = acquireStream(LockFilePath)
                ?? throw new InvalidOperationException("The instance lock stream was not acquired.");
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException(
                $"The backend instance root '{instancePaths.InstanceRoot}' is already in use.",
                ex);
        }

        try { WriteOwnershipMetadata(); }
        catch
        {
            // Acquisition does not transfer to the caller until metadata is
            // durable. Closing this handle releases the exclusive path lease.
            try { _lockStream.Dispose(); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Preserve the original metadata failure if release also fails.
            }
            throw;
        }
    }

    public string LockFilePath { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
            return;

        _lockStream.Dispose();

        try
        {
            if (File.Exists(LockFilePath))
                File.Delete(LockFilePath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private void WriteOwnershipMetadata()
    {
        _lockStream.SetLength(0);
        using var writer = new StreamWriter(_lockStream, leaveOpen: true);
        writer.WriteLine($"pid={Environment.ProcessId}");
        writer.WriteLine($"startedAtUtc={DateTimeOffset.UtcNow:O}");
        writer.Flush();
        _lockStream.Flush(flushToDisk: true);
        _lockStream.Position = 0;
    }
}
