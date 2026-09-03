using FluentAssertions;
using SharpClaw.Contracts.Modules;
using SharpClaw.Runtime.BLL.Kernel;

namespace SharpClaw.Tests.Kernel;

[TestFixture]
public sealed class RuntimeTransactionBoundaryTests
{
    [Test]
    public void Transaction_manifest_matches_the_published_catalog()
    {
        var expected = SharpClawActionCatalog.Kernel
            .Where(static key => key.Value.StartsWith(
                "storage.transaction.",
                StringComparison.Ordinal))
            .Select(static key => key.Value)
            .ToArray();

        RuntimeTransactionActionManifest.Required
            .Select(static key => key.Value)
            .Should()
            .Equal(expected);
    }

    [Test]
    public void Provider_transaction_calls_are_owned_by_the_transaction_terminal_adapter()
    {
        var sourceRoot = FindSourceRoot();
        var runtimeSources = Directory.EnumerateFiles(
                Path.Combine(sourceRoot, "SharpClaw.Runtime"),
                "*.cs",
                SearchOption.AllDirectories)
            .Where(path => !path.Contains(
                $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Contains(
                $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                StringComparison.OrdinalIgnoreCase));

        var offenders = runtimeSources
            .SelectMany(path => File.ReadLines(path)
                .Select((line, index) => (Path: path, Line: index + 1, Text: line)))
            .Where(entry =>
                entry.Text.Contains("db.Database.BeginTransaction", StringComparison.Ordinal)
                || entry.Text.Contains("transaction.CommitAsync(", StringComparison.Ordinal)
                || entry.Text.Contains("transaction.RollbackAsync(", StringComparison.Ordinal)
                || entry.Text.Contains("current.CommitAsync(", StringComparison.Ordinal)
                || entry.Text.Contains("current.RollbackAsync(", StringComparison.Ordinal))
            .Where(entry => !string.Equals(
                Path.GetFileName(entry.Path),
                "RuntimeTransactionActionBoundary.cs",
                StringComparison.Ordinal))
            .Select(entry => $"{entry.Path}:{entry.Line}")
            .ToArray();

        offenders.Should().BeEmpty(
            "provider transaction methods must run only in the transaction terminal adapter");
    }

    private static string FindSourceRoot()
    {
        var configuredRoot = Environment.GetEnvironmentVariable("SHARPCLAW_SOURCE_ROOT");
        if (!string.IsNullOrWhiteSpace(configuredRoot) &&
            Directory.Exists(Path.Combine(configuredRoot, "SharpClaw.Runtime")))
            return configuredRoot;

        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "SharpClaw.Runtime")))
                return directory.FullName;
        }

        throw new AssertionException("The SharpClaw source root could not be located.");
    }
}
