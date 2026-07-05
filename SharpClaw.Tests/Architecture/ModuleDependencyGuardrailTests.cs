using System.Reflection;
using System.Xml.Linq;
using FluentAssertions;
using NUnit.Framework;

namespace SharpClaw.Tests.Architecture;

/// <summary>
/// Guardrails for packaged modules: the runtime host may copy module payloads
/// from packages, but module assemblies must not become compiler references in
/// the host pipeline.
/// </summary>
[TestFixture]
public class ModuleDependencyGuardrailTests
{
    [Test]
    public void Api_assembly_must_not_reference_module_assemblies()
    {
        var apiAssembly = typeof(SharpClaw.Runtime.Host.DatabaseInitializationGate).Assembly;

        var moduleReferences = apiAssembly.GetReferencedAssemblies()
            .Select(a => a.Name)
            .Where(name => name is not null && name.StartsWith("SharpClaw.Modules.", StringComparison.Ordinal))
            .ToList();

        moduleReferences.Should().BeEmpty(
            "modules contribute through runtime discovery and must not enter the API compiler reference graph");
    }

    [Test]
    public void Api_project_must_not_reference_extracted_module_source_projects()
    {
        var apiProjectPath = FindFileFromTestAssembly("SharpClaw.Runtime.Host", "SharpClaw.Runtime.Host.csproj");
        var project = XDocument.Load(apiProjectPath);

        var extractedModuleProjectNames = new[]
        {
            "SharpClaw.Modules.AgentOrchestration.csproj",
            "SharpClaw.Modules.Metrics.csproj",
            "SharpClaw.Modules.ModuleDev.csproj",
        };
        var extractedModuleReferences = project.Descendants("ProjectReference")
            .Where(reference =>
            {
                var include = (string?)reference.Attribute("Include") ?? "";
                return extractedModuleProjectNames.Any(name =>
                    include.Contains(name, StringComparison.OrdinalIgnoreCase));
            })
            .ToList();

        extractedModuleReferences.Should().BeEmpty(
            "extracted modules are consumed from NuGet package payloads, not source project references");

        project.Descendants("ProjectReference")
            .Select(reference => (string?)reference.Attribute("Include") ?? "")
            .Should()
            .Contain(path => path.Contains("SharpClaw.Modules.TestHarness", StringComparison.OrdinalIgnoreCase),
                "TestHarness is the only in-repo module source and is explicit test infrastructure");
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
