using System.Reflection;
using System.Xml.Linq;
using FluentAssertions;
using NUnit.Framework;

namespace SharpClaw.Tests.Architecture;

[TestFixture]
public sealed class CanonicalJobsRouteBoundaryTests
{
    [Test]
    public void Obsolete_channel_scoped_job_owners_are_absent()
    {
        var root = FindSolutionRoot();

        File.Exists(Path.Combine(
            root,
            "SharpClaw.Gateway",
            "Controllers",
            "AgentJobsController.cs")).Should().BeFalse();
        File.Exists(Path.Combine(
            root,
            "SharpClaw.Runtime",
            "Host",
            "Handlers",
            "AgentJobHandlers.cs")).Should().BeFalse();
    }

    [Test]
    public void Runtime_host_compiles_the_canonical_jobs_handler_only()
    {
        var root = FindSolutionRoot();
        var project = XDocument.Load(Path.Combine(
            root,
            "SharpClaw.Runtime",
            "Host",
            "SharpClaw.Runtime.Host.csproj"));
        var includes = project.Descendants("Compile")
            .Select(element => (string?)element.Attribute("Include"))
            .Where(include => include is not null)
            .Select(include => include!)
            .ToArray();

        includes.Should().Contain("Handlers\\KernelJobsHandlers.cs");
        includes.Should().NotContain("Handlers\\AgentJobHandlers.cs");
    }

    [Test]
    public void Canonical_jobs_path_does_not_use_host_agent_job_storage()
    {
        var root = FindSolutionRoot();
        var sources = new[]
        {
            Path.Combine(root, "SharpClaw.Runtime", "Host", "Handlers", "KernelJobsHandlers.cs"),
            Path.Combine(root, "SharpClaw.Runtime", "Host", "RuntimeHostComposition.cs"),
            Path.Combine(root, "SharpClaw.Runtime", "BLL", "Kernel"),
        }
            .SelectMany(path => File.Exists(path)
                ? [path]
                : Directory.Exists(path)
                    ? Directory.GetFiles(path, "*.cs", SearchOption.AllDirectories)
                    : [])
            .Select(File.ReadAllText)
            .ToArray();

        string.Join(Environment.NewLine, sources)
            .Should()
            .NotContain("AgentJobDB")
            .And
            .NotContain("ExecutionOwnerKind.AgentJob");
    }

    [Test]
    public void Excluded_legacy_orchestration_sources_are_absent()
    {
        var root = FindSolutionRoot();
        var bll = Path.Combine(root, "SharpClaw.Runtime", "BLL");
        var host = Path.Combine(root, "SharpClaw.Runtime", "Host");

        GetSourceFiles(bll, "Services").Should().BeEmpty();
        GetSourceFiles(host, "Cli").Should().BeEmpty();

        var moduleSources = Directory.Exists(Path.Combine(bll, "Modules"))
            ? Directory.GetFiles(
                    Path.Combine(bll, "Modules"),
                    "*.cs",
                    SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(bll, path))
                .ToArray()
            : [];

        moduleSources.Should().Equal("Modules\\SecureJsonOptions.cs");
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
