using System.Reflection;
using System.Text.Json;
using System.Xml.Linq;

namespace SharpClaw.Tests.Architecture;

[TestFixture]
public sealed class ProductionModuleNeutralityTests
{
    [Test]
    public void ProductionProjectsDoNotReferenceOptionalModuleOrProviderPackages()
    {
        var root = ResolveSourceRoot();
        var projectPaths = Directory.EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories)
            .Where(path => !path.Contains("SharpClaw.Tests", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Contains("SharpClaw.DefaultModules", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        projectPaths.Should().NotBeEmpty();
        foreach (var projectPath in projectPaths)
        {
            var project = XDocument.Load(projectPath);
            var packageIds = project.Descendants("PackageReference")
                .Select(reference => (string?)reference.Attribute("Include"))
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .ToArray();

            packageIds.Should().NotContain(id =>
                id!.StartsWith("SharpClaw.Modules.", StringComparison.Ordinal));
            packageIds.Should().NotContain("SharpClaw.Providers.Common");
            packageIds.Should().NotContain("SharpClaw.Providers.LocalCommon");
        }
    }

    [Test]
    public void GenericPayloadTargetDoesNotNameOptionalModules()
    {
        var targetPath = Path.Combine(
            ResolveSourceRoot(),
            "build",
            "PackagedModulePayload.targets");
        var source = File.ReadAllText(targetPath);

        source.Should().NotContain("SharpClaw.Modules.");
        source.Should().NotContain("sharpclaw_providers_");
        source.Should().NotContain("sharpclaw_context");
        source.Should().NotContain("sharpclaw_agents");
    }

    [Test]
    public void RestoredSharpClawPackagesUseExactOwnedDependencyRanges()
    {
        var depsPath = Path.ChangeExtension(
            Assembly.GetExecutingAssembly().Location,
            ".deps.json");
        using var deps = JsonDocument.Parse(File.ReadAllText(depsPath));
        var packageRoot = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
        if (string.IsNullOrWhiteSpace(packageRoot))
        {
            packageRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".nuget",
                "packages");
        }

        var failures = new List<string>();
        foreach (var library in deps.RootElement.GetProperty("libraries").EnumerateObject())
        {
            var separator = library.Name.IndexOf('/');
            if (separator <= 0)
                continue;

            var id = library.Name[..separator];
            var version = library.Name[(separator + 1)..];
            if (!id.StartsWith("SharpClaw.", StringComparison.Ordinal))
                continue;

            var packageDirectory = Path.Combine(
                packageRoot,
                id.ToLowerInvariant(),
                version.ToLowerInvariant());
            var nuspecPath = Directory.Exists(packageDirectory)
                ? Directory.GetFiles(packageDirectory, "*.nuspec").SingleOrDefault()
                : null;
            if (nuspecPath is null)
                continue;

            var nuspec = XDocument.Load(nuspecPath);
            foreach (var dependency in nuspec.Descendants()
                         .Where(element => element.Name.LocalName == "dependency"))
            {
                var dependencyId = (string?)dependency.Attribute("id");
                var dependencyVersion = (string?)dependency.Attribute("version");
                if (dependencyId?.StartsWith("SharpClaw.", StringComparison.Ordinal) != true)
                    continue;

                if (dependencyVersion is null
                    || !dependencyVersion.StartsWith("[", StringComparison.Ordinal)
                    || !dependencyVersion.EndsWith("]", StringComparison.Ordinal)
                    || dependencyVersion.Contains(",", StringComparison.Ordinal))
                {
                    failures.Add($"{id} -> {dependencyId} {dependencyVersion}");
                }
            }
        }

        failures.Should().BeEmpty();
    }

    private static string ResolveSourceRoot()
    {
        var candidates = new[]
        {
            Environment.GetEnvironmentVariable("SHARPCLAW_SOURCE_ROOT"),
            Directory.GetCurrentDirectory(),
            Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location),
        };

        foreach (var candidate in candidates.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            var directory = new DirectoryInfo(candidate!);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "SharpClaw.slnx")))
                    return directory.FullName;
                directory = directory.Parent;
            }
        }

        throw new DirectoryNotFoundException("SharpClaw source root was not found.");
    }
}
