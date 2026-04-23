namespace SharpClaw.Utils.Instances;

/// <summary>
/// Acquires exclusive ownership of an instance root for the current process.
/// </summary>
public sealed class SharpClawInstanceLock : IDisposable
{
    private readonly FileStream _lockStream;
    private int _disposeState;

    public SharpClawInstanceLock(SharpClawInstancePaths instancePaths)
    {
        ArgumentNullException.ThrowIfNull(instancePaths);

        instancePaths.EnsureDirectories();
        LockFilePath = Path.Combine(instancePaths.InstanceRoot, ".instance.lock");

        try
        {
            _lockStream = new FileStream(
                LockFilePath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None);
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException(
                $"The backend instance root '{instancePaths.InstanceRoot}' is already in use.",
                ex);
        }

        WriteOwnershipMetadata();
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
        catch
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
