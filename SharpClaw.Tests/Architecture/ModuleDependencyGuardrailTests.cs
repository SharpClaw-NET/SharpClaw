using System.Reflection;
using System.Text.Json;
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
    public sealed record ProjectLocation(string Directory, string ProjectFile);

    private static readonly ProjectLocation[] ProjectLocations =
    [
        new("SharpClaw.Runtime/Host", "SharpClaw.Runtime.Host.csproj"),
        new("SharpClaw.Gateway", "SharpClaw.Gateway.csproj"),
        new("SharpClaw.Tests", "SharpClaw.Tests.csproj"),
    ];

    [Test]
    public void SharpClaw_assemblies_must_not_reference_module_payload_assemblies()
    {
        var assemblies = new[]
        {
            typeof(SharpClaw.Runtime.Host.DatabaseInitializationGate).Assembly,
            typeof(SharpClaw.Gateway.Configuration.GatewayEnvironment).Assembly,
            typeof(ModuleDependencyGuardrailTests).Assembly,
        };

        var moduleReferences = assemblies
            .SelectMany(assembly => assembly.GetReferencedAssemblies()
                .Select(reference => new { Assembly = assembly.GetName().Name, Reference = reference.Name }))
            .Where(item => item.Reference is not null
                && item.Reference.StartsWith("SharpClaw.Modules.", StringComparison.Ordinal))
            .ToList();

        moduleReferences.Should().BeEmpty(
            "module NuGet packages are copied as runtime payloads and must not enter SharpClaw compiler reference graphs");
    }

    [Test]
    public void Runtime_host_project_must_not_reference_extracted_module_source_projects()
    {
        var apiProjectPath = FindFileFromTestAssembly("SharpClaw.Runtime/Host", "SharpClaw.Runtime.Host.csproj");
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

        var testHarnessReferences = project.Descendants("ProjectReference")
            .Where(reference => (((string?)reference.Attribute("Include")) ?? "")
                .Contains("SharpClaw.DefaultModules.TestHarness", StringComparison.OrdinalIgnoreCase))
            .ToList();

        testHarnessReferences.Should().NotBeEmpty(
            "TestHarness modules remain in-repo as explicit test infrastructure");
        foreach (var reference in testHarnessReferences)
        {
            reference.Element("ReferenceOutputAssembly")?.Value.Should().Be(
                "false",
                "in-repo TestHarness modules are payload fixtures and must not become Runtime.Host compiler references");
        }
    }

    [TestCaseSource(nameof(ProjectLocations))]
    public void Module_payload_package_references_must_be_path_only(ProjectLocation projectLocation)
    {
        var projectPath = FindFileFromTestAssembly(projectLocation.Directory, projectLocation.ProjectFile);
        var project = XDocument.Load(projectPath);
        var packageReferences = GetModulePayloadPackageIds(project)
            .Select(id => new
            {
                Id = id,
                Element = project.Descendants("PackageReference")
                    .Single(reference => string.Equals(
                        (string?)reference.Attribute("Include"),
                        id,
                        StringComparison.Ordinal))
            })
            .ToList();

        packageReferences.Should().NotBeEmpty();

        foreach (var reference in packageReferences)
        {
            ((string?)reference.Element.Attribute("GeneratePathProperty")).Should().Be(
                "true",
                $"{reference.Id} is consumed only for package payload paths");
            ((string?)reference.Element.Attribute("PrivateAssets")).Should().Be(
                "all",
                $"{reference.Id} must not flow transitively from SharpClaw projects");

            var excludedAssets = (((string?)reference.Element.Attribute("ExcludeAssets")) ?? "")
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            excludedAssets.Should().Contain("compile", $"{reference.Id} must not expose module types to SharpClaw code");
        }
    }

    [TestCaseSource(nameof(ProjectLocations))]
    public void In_repo_test_harness_project_references_must_be_payload_only(ProjectLocation projectLocation)
    {
        var projectPath = FindFileFromTestAssembly(projectLocation.Directory, projectLocation.ProjectFile);
        var project = XDocument.Load(projectPath);
        var testHarnessReferences = project.Descendants("ProjectReference")
            .Where(reference => (((string?)reference.Attribute("Include")) ?? "")
                .Contains("SharpClaw.DefaultModules.TestHarness", StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var reference in testHarnessReferences)
        {
            var referenceOutputAssembly =
                (string?)reference.Attribute("ReferenceOutputAssembly")
                ?? reference.Element("ReferenceOutputAssembly")?.Value;

            referenceOutputAssembly.Should().Be(
                "false",
                "TestHarness modules are built/copied as payloads and must not expose implementation types to SharpClaw code");
        }
    }

    [TestCaseSource(nameof(ProjectLocations))]
    public void Module_payload_packages_must_not_contribute_compile_assets(ProjectLocation projectLocation)
    {
        var projectPath = FindFileFromTestAssembly(projectLocation.Directory, projectLocation.ProjectFile);
        var project = XDocument.Load(projectPath);
        var packageIds = GetModulePayloadPackageIds(project).ToList();
        var assetsPath = Path.Combine(Path.GetDirectoryName(projectPath)!, "obj", "project.assets.json");

        File.Exists(assetsPath).Should().BeTrue("restore must produce project.assets.json before architecture tests run");

        using var document = JsonDocument.Parse(File.ReadAllText(assetsPath));
        var target = document.RootElement
            .GetProperty("targets")
            .EnumerateObject()
            .First()
            .Value;
        var targetLibraries = target.EnumerateObject().ToDictionary(
            property => property.Name,
            property => property.Value,
            StringComparer.OrdinalIgnoreCase);

        foreach (var packageId in packageIds)
        {
            var library = targetLibraries
                .Single(pair => pair.Key.StartsWith(packageId + "/", StringComparison.OrdinalIgnoreCase))
                .Value;

            AssertPlaceholderOnly(library.GetProperty("compile"), packageId, "compile");
        }
    }

    [TestCaseSource(nameof(ProjectLocations))]
    public void Module_facing_package_graph_must_not_depend_on_sharpclaw_core(ProjectLocation projectLocation)
    {
        var projectPath = FindFileFromTestAssembly(projectLocation.Directory, projectLocation.ProjectFile);
        var project = XDocument.Load(projectPath);
        var packageIds = GetModuleFacingPackageIds(project).ToList();
        var assetsPath = Path.Combine(Path.GetDirectoryName(projectPath)!, "obj", "project.assets.json");

        File.Exists(assetsPath).Should().BeTrue("restore must produce project.assets.json before architecture tests run");

        using var document = JsonDocument.Parse(File.ReadAllText(assetsPath));
        var libraries = document.RootElement
            .GetProperty("libraries")
            .EnumerateObject()
            .ToDictionary(
                property => GetPackageId(property.Name),
                property => property.Value,
                StringComparer.OrdinalIgnoreCase);

        foreach (var packageId in packageIds)
        {
            var dependencyPath = FindSharpClawCoreDependencyPath(packageId, libraries);

            dependencyPath.Should().BeNull(
                $"{packageId} is module-facing and must rely on SharpClaw.Contracts, not SharpClaw.Core. Dependency path: {dependencyPath}");
        }
    }

    [Test]
    public void In_repo_module_projects_must_not_reference_sharpclaw_core()
    {
        var solutionPath = FindFileFromTestAssembly(".", "SharpClaw.slnx");
        var solutionRoot = Path.GetDirectoryName(solutionPath)!;
        var solution = XDocument.Load(solutionPath);
        var moduleProjectPaths = solution.Descendants("Project")
            .Select(project => (string?)project.Attribute("Path"))
            .Where(path => path is not null
                && path.Contains("SharpClaw.DefaultModules", StringComparison.OrdinalIgnoreCase))
            .Select(path => Path.Combine(solutionRoot, path!))
            .ToList();

        moduleProjectPaths.Should().NotBeEmpty("the in-repo TestHarness modules are module payload fixtures");

        foreach (var projectPath in moduleProjectPaths)
        {
            var project = XDocument.Load(projectPath);
            var sharpClawCorePackageReferences = project.Descendants("PackageReference")
                .Where(reference => string.Equals(
                    (string?)reference.Attribute("Include"),
                    "SharpClaw.Core",
                    StringComparison.OrdinalIgnoreCase))
                .ToList();
            var sharpClawCoreProjectReferences = project.Descendants("ProjectReference")
                .Where(reference => (((string?)reference.Attribute("Include")) ?? "")
                    .Contains("SharpClaw.Core", StringComparison.OrdinalIgnoreCase))
                .ToList();

            sharpClawCorePackageReferences.Should().BeEmpty($"{projectPath} must use SharpClaw.Contracts instead of SharpClaw.Core");
            sharpClawCoreProjectReferences.Should().BeEmpty($"{projectPath} must not project-reference SharpClaw.Core");
        }
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

    private static IEnumerable<string> GetModulePayloadPackageIds(XDocument project)
    {
        return project.Descendants("PackageReference")
            .Select(reference => (string?)reference.Attribute("Include"))
            .Where(id => id is not null
                && id.StartsWith("SharpClaw.Modules.", StringComparison.Ordinal))
            .Select(id => id!)
            .OrderBy(id => id, StringComparer.Ordinal);
    }

    private static IEnumerable<string> GetModuleFacingPackageIds(XDocument project)
    {
        return project.Descendants("PackageReference")
            .Select(reference => (string?)reference.Attribute("Include"))
            .Where(id => id is not null
                && (id.StartsWith("SharpClaw.Modules.", StringComparison.Ordinal)
                    || string.Equals(id, "SharpClaw.ModuleHost.OutOfProcess", StringComparison.Ordinal)))
            .Select(id => id!)
            .OrderBy(id => id, StringComparer.Ordinal);
    }

    private static string? FindSharpClawCoreDependencyPath(
        string rootPackageId,
        IReadOnlyDictionary<string, JsonElement> libraries)
    {
        var queue = new Queue<(string PackageId, string Path)>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        queue.Enqueue((rootPackageId, rootPackageId));

        while (queue.Count > 0)
        {
            var (packageId, path) = queue.Dequeue();
            if (!visited.Add(packageId))
            {
                continue;
            }

            if (string.Equals(packageId, "SharpClaw.Core", StringComparison.OrdinalIgnoreCase))
            {
                return path;
            }

            if (!libraries.TryGetValue(packageId, out var library)
                || !library.TryGetProperty("dependencies", out var dependencies))
            {
                continue;
            }

            foreach (var dependency in dependencies.EnumerateObject())
            {
                queue.Enqueue((dependency.Name, path + " -> " + dependency.Name));
            }
        }

        return null;
    }

    private static string GetPackageId(string libraryKey)
    {
        var slashIndex = libraryKey.IndexOf('/');
        return slashIndex < 0 ? libraryKey : libraryKey[..slashIndex];
    }

    private static void AssertPlaceholderOnly(JsonElement assets, string packageId, string assetKind)
    {
        var assetNames = assets.EnumerateObject()
            .Select(property => property.Name)
            .ToList();

        assetNames.Should().ContainSingle(
            $"{packageId} must not expose real {assetKind} assets through project.assets.json");
        assetNames.Single().Should().EndWith(
            "/_._",
            $"{packageId} is payload-only and should have only the NuGet placeholder {assetKind} asset");
    }
}
