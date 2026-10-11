using System.Text;
using System.Text.Json;
using SharpClaw.Shared.Logging;

namespace SharpClaw.Services;

/// <summary>
/// A synchronous, profile-writable startup journal independent of DI, Serilog and
/// the native window. Never records exception messages, configuration or secrets.
/// </summary>
internal sealed class ClientStartupDiagnostics
{
    private readonly Lock _gate = new();
    private readonly Action<string> _fallback;

    internal ClientStartupDiagnostics(string directory, Action<string>? fallback = null)
    {
        _fallback = fallback ?? (message => Console.Error.WriteLine(message));
        JournalPath = Path.Combine(Path.GetFullPath(directory),
            $"startup-{DateTime.UtcNow:yyyyMMddTHHmmssfff}-{Environment.ProcessId}.jsonl");
    }

    internal static ClientStartupDiagnostics Current { get; } = new(
        Path.Combine(SharpClawAppDataPaths.GetSharpClawRootDirectory(), "diagnostics", "startup"));

    internal string JournalPath { get; }

    internal void Record(ClientStartupStage stage, Exception? exception = null)
    {
        try
        {
            lock (_gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(JournalPath)!);
                var line = JsonSerializer.Serialize(new
                {
                    TimestampUtc = DateTimeOffset.UtcNow,
                    ProcessId = Environment.ProcessId,
                    Stage = stage.ToString(),
                    FailureType = exception?.GetType().FullName,
                    FailureHResult = exception?.HResult,
                });
                using var stream = new FileStream(JournalPath, FileMode.Append,
                    FileAccess.Write, FileShare.ReadWrite);
                stream.Write(Encoding.UTF8.GetBytes(line + "\n"));
                stream.Flush(flushToDisk: true);
            }
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            // A journal failure must not replace the original startup failure.
            RecordFallback(failure);
        }
    }

    private void RecordFallback(Exception journalFailure)
    {
        try { _fallback($"SharpClaw startup journal unavailable ({journalFailure.GetType().Name})."); }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            // Both sinks are best-effort. An ordinary unavailable/disposed stderr
            // must not replace an action fault or interrupt remaining cleanup.
        }
    }
}
