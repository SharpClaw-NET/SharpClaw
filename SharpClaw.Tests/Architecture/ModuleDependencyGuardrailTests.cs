using System.Reflection;
using System.Xml.Linq;
using FluentAssertions;
using NUnit.Framework;

namespace SharpClaw.Tests.Architecture;

/// <summary>
/// Guardrails for bundled modules: the API may build them for local F5 convenience,
/// but module assemblies must not become compiler references in the host pipeline.
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
    public void Api_project_module_references_must_not_output_compiler_references()
    {
        var apiProjectPath = FindFileFromTestAssembly("SharpClaw.Runtime.Host", "SharpClaw.Runtime.Host.csproj");
        var project = XDocument.Load(apiProjectPath);

        var moduleReferences = project.Descendants("ProjectReference")
            .Where(reference => ((string?)reference.Attribute("Include"))?.Contains("DefaultModules", StringComparison.OrdinalIgnoreCase) == true)
            .ToList();

        moduleReferences.Should().NotBeEmpty("the API project currently builds bundled default modules for local startup");

        var unsafeReferences = moduleReferences
            .Where(reference => !string.Equals(reference.Element("ReferenceOutputAssembly")?.Value.Trim(), "false", StringComparison.OrdinalIgnoreCase))
            .Select(reference => (string?)reference.Attribute("Include") ?? "<missing Include>")
            .ToList();

        unsafeReferences.Should().BeEmpty(
            "bundled modules may be in the build graph, but must not be added to the API compiler reference list");
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
