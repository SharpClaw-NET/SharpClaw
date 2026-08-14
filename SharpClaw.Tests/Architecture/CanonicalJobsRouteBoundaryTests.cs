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
