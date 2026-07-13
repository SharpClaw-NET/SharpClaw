using System.Reflection;
using System.Xml.Linq;
using FluentAssertions;
using NUnit.Framework;

namespace SharpClaw.Tests.Architecture;

/// <summary>
/// Compile-time dependency guardrails for distributable processes. Client.Uno may
/// ship Runtime.Host and Gateway payloads, but those payloads must not become
/// project or assembly references in the client or gateway compile graph.
/// </summary>
[TestFixture]
public sealed class RuntimeClientBoundaryGuardrailTests
{
    [Test]
    public void Gateway_project_must_not_reference_runtime_or_client_projects()
    {
        var projectPath = FindFileFromTestAssembly("SharpClaw.Gateway", "SharpClaw.Gateway.csproj");

        var offenders = LoadProjectReferenceIncludes(projectPath)
            .Where(include =>
                include.Contains("SharpClaw.Runtime.", StringComparison.OrdinalIgnoreCase) ||
                include.Contains("SharpClaw.Client.Uno", StringComparison.OrdinalIgnoreCase))
            .ToList();

        offenders.Should().BeEmpty(
            "Gateway must communicate with Runtime.Host over HTTP and must not compile against runtime or client projects");
    }

    [Test]
    public void Gateway_assembly_must_not_reference_runtime_or_client_assemblies()
    {
        var gatewayAssembly = typeof(SharpClaw.Gateway.Infrastructure.InternalApiOptions).Assembly;

        var offenders = gatewayAssembly.GetReferencedAssemblies()
            .Select(reference => reference.Name)
            .Where(name =>
                name is not null &&
                (name.StartsWith("SharpClaw.Runtime.", StringComparison.Ordinal) ||
                 string.Equals(name, "SharpClaw.Client.Uno", StringComparison.Ordinal)))
            .ToList();

        offenders.Should().BeEmpty(
            "Gateway must stay process-independent from Runtime.Host and Client.Uno");
    }

    [Test]
    public void ClientUno_project_must_not_reference_runtime_or_gateway_projects()
    {
        var projectPath = FindFileFromTestAssembly("SharpClaw.Client.Uno", "SharpClaw.Client.Uno.csproj");

        var offenders = LoadProjectReferenceIncludes(projectPath)
            .Where(include =>
                include.Contains("SharpClaw.Runtime.", StringComparison.OrdinalIgnoreCase) ||
                include.Contains("SharpClaw.Gateway", StringComparison.OrdinalIgnoreCase))
            .ToList();

        offenders.Should().BeEmpty(
            "Client.Uno may package Runtime.Host and Gateway through publish targets, but must not compile against them");
    }

    [Test]
    public void ClientUno_assembly_must_not_reference_runtime_or_gateway_assemblies()
    {
        var clientAssembly = typeof(SharpClaw.Services.BackendProcessManager).Assembly;

        var offenders = clientAssembly.GetReferencedAssemblies()
            .Select(reference => reference.Name)
            .Where(name =>
                name is not null &&
                (name.StartsWith("SharpClaw.Runtime.", StringComparison.Ordinal) ||
                 string.Equals(name, "SharpClaw.Gateway", StringComparison.Ordinal) ||
                 string.Equals(name, "SharpClaw.Gateway.Contracts", StringComparison.Ordinal)))
            .ToList();

        offenders.Should().BeEmpty(
            "Client.Uno starts packaged processes and talks over HTTP; it must not compile against Runtime or Gateway assemblies");
    }

    private static IReadOnlyList<string> LoadProjectReferenceIncludes(string projectPath)
    {
        var project = XDocument.Load(projectPath);

        return project.Descendants("ProjectReference")
            .Select(reference => (string?)reference.Attribute("Include"))
            .Where(include => !string.IsNullOrWhiteSpace(include))
            .Select(include => include!)
            .ToList();
    }

    private static string FindFileFromTestAssembly(string projectDirectory, string fileName)
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!);

        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, projectDirectory, fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not find {projectDirectory}\\{fileName} from test assembly location.");
    }
}
