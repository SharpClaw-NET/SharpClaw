using System.Reflection;
using System.Xml.Linq;
using FluentAssertions;
using NUnit.Framework;

namespace SharpClaw.Tests.Architecture;

[TestFixture]
public sealed class CanonicalJobsRouteBoundaryTests
{
    [Test]
    public void Runtime_host_compiles_the_canonical_jobs_handler_with_default_source_items()
    {
        var root = FindSolutionRoot();
        var project = XDocument.Load(Path.Combine(
            root,
            "SharpClaw.Runtime",
            "Host",
            "SharpClaw.Runtime.Host.csproj"));
        project.Descendants("Compile").Should().BeEmpty();
        File.Exists(Path.Combine(
            root,
            "SharpClaw.Runtime",
            "Host",
            "Handlers",
            "KernelJobsHandlers.cs")).Should().BeTrue();
    }

    [Test]
    public void Excluded_legacy_orchestration_sources_are_absent()
    {
        var root = FindSolutionRoot();
        var bll = Path.Combine(root, "SharpClaw.Runtime", "BLL");
        var host = Path.Combine(root, "SharpClaw.Runtime", "Host");

        GetSourceFiles(bll, "Services").Should().BeEmpty();
        GetSourceFiles(host, "Cli").Should().BeEmpty();

        Directory.Exists(Path.Combine(bll, "Modules")).Should().BeFalse();
        File.Exists(Path.Combine(bll, "Configuration", "SecureJsonOptions.cs"))
            .Should().BeTrue();
    }

    private static IReadOnlyList<string> GetSourceFiles(string root, string relativeDirectory)
    {
        var directory = Path.Combine(root, relativeDirectory);
        return Directory.Exists(directory)
            ? Directory.GetFiles(directory, "*.cs", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(root, path))
                .ToArray()
            : [];
    }

    private static string FindSolutionRoot()
    {
        var starts = new[]
        {
            Environment.GetEnvironmentVariable("SHARPCLAW_SOURCE_ROOT"),
            Directory.GetCurrentDirectory(),
            Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location),
        };

        foreach (var start in starts.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            var directory = new DirectoryInfo(start!);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "SharpClaw.slnx")))
                    return directory.FullName;
                directory = directory.Parent;
            }
        }

        throw new DirectoryNotFoundException(
            "Could not locate SharpClaw.slnx from test assembly.");
    }
}
