using System.Text.Json;
using FluentAssertions;
using NUnit.Framework;
using SharpClaw.Services;

namespace SharpClaw.Tests.Publishing;

[TestFixture]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812",
    Justification = "NUnit discovers and instantiates this fixture through its TestFixture attribute.")]
internal sealed class ClientStartupDiagnosticsTests
{
    [Test]
    public void StartupJournalWorksBeforeServicesAndDoesNotPersistExceptionSecrets()
    {
        var root = Path.Combine(Path.GetTempPath(), "sharpclaw-startup-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var diagnostics = new ClientStartupDiagnostics(root);
            diagnostics.Record(ClientStartupStage.DesktopHostStarting);
            diagnostics.Record(ClientStartupStage.StartupFailed,
                new InvalidOperationException("ApiKey=do-not-persist-this-secret"));
            var lines = File.ReadAllLines(diagnostics.JournalPath);
            lines.Should().HaveCount(2);
            using var first = JsonDocument.Parse(lines[0]);
            first.RootElement.GetProperty("Stage").GetString().Should().Be("DesktopHostStarting");
            first.RootElement.GetProperty("ProcessId").GetInt32().Should().Be(Environment.ProcessId);
            using var failure = JsonDocument.Parse(lines[1]);
            failure.RootElement.GetProperty("FailureType").GetString().Should().Be(typeof(InvalidOperationException).FullName);
            File.ReadAllText(diagnostics.JournalPath).Should().NotContain("do-not-persist").And.NotContain("ApiKey");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void AJournalWriteFailureDoesNotReplaceTheStartupException()
    {
        var blocker = Path.GetTempFileName();
        try
        {
            var diagnostics = new ClientStartupDiagnostics(Path.Combine(blocker, "unavailable"));
            var record = () => diagnostics.Record(ClientStartupStage.StartupFailed,
                new InvalidOperationException("original failure"));
            record.Should().NotThrow();
        }
        finally
        {
            File.Delete(blocker);
        }
    }
}
