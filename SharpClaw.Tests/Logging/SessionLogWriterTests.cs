using SharpClaw.Utils.Logging;

namespace SharpClaw.Tests.Logging;

[TestFixture]
public sealed class SessionLogWriterTests
{
    [Test]
    public async Task FreshStartResetsSerilogFileAlongsideSessionLogs()
    {
        var logsRoot = Path.Combine(
            Path.GetTempPath(),
            "SharpClawSessionLogWriterTests_" + Guid.NewGuid().ToString("N"));

        try
        {
            var appLogDirectory = Path.Combine(logsRoot, "core");
            Directory.CreateDirectory(appLogDirectory);

            File.WriteAllText(Path.Combine(appLogDirectory, "log.txt"), "old log");
            File.WriteAllText(Path.Combine(appLogDirectory, "debug.txt"), "old debug");
            File.WriteAllText(Path.Combine(appLogDirectory, "exceptions.txt"), "old exception");
            File.WriteAllText(Path.Combine(appLogDirectory, "serilog.txt"), "old serilog");

            string logPath;
            string debugPath;
            string exceptionPath;
            await using (var writer = new SessionLogWriter(
                "core",
                logsRoot,
                TimeSpan.FromMinutes(10)))
            {
                logPath = writer.LogFilePath;
                debugPath = writer.DebugFilePath;
                exceptionPath = writer.ExceptionFilePath;

                File.Exists(writer.SerilogFilePath).Should().BeFalse(
                    "Serilog creates this file after startup, so the stale file must be deleted first");
            }

            new FileInfo(logPath).Length.Should().Be(0);
            new FileInfo(debugPath).Length.Should().Be(0);
            new FileInfo(exceptionPath).Length.Should().Be(0);
        }
        finally
        {
            TryDeleteDirectory(logsRoot);
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Best effort cleanup for Windows file handles in failed test runs.
        }
    }
}
