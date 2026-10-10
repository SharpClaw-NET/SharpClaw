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

    internal ClientStartupDiagnostics(string directory)
    {
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
            Console.Error.WriteLine($"SharpClaw startup journal unavailable ({failure.GetType().Name}).");
        }
    }
}
