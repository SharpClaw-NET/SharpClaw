using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Xml.Linq;

namespace SharpClaw.Tests.Modules;

/// <summary>
/// Verifies that bundled module payloads are present in API build and publish output.
/// This test will fail if the CopyBundledModules MSBuild target in
/// SharpClaw.Runtime.Host.csproj is broken or a module project is removed.
/// </summary>
[TestFixture]
public class BundledModuleOutputTests
{
    private sealed class BundledModuleExpectation
    {
        public BundledModuleExpectation(
            string id,
            string runtime,
            string hostMode,
            string entryAssembly,
            string manifestPath,
            string? packageEntryAssemblyPath = null)
        {
            Id = id;
            Runtime = runtime;
            HostMode = hostMode;
            EntryAssembly = entryAssembly;
            ManifestPath = manifestPath;
            PackageEntryAssemblyPath = packageEntryAssemblyPath;
        }

        public string Id { get; }

        public string Runtime { get; }

        public string HostMode { get; }

        public string EntryAssembly { get; }

        public string ManifestPath { get; }

        public string? PackageEntryAssemblyPath { get; }
    }

    private static IEnumerable<string> ExpectedModuleDlls()
        => ReadBundledModuleExpectations().Select(module => module.EntryAssembly);

    [Test]
    public void AllBundledModuleDllsArePresentInApiOutput()
    {
        var apiOutputDir = ResolveApiOutputDirectory();

        apiOutputDir.Should().NotBeNull();
        Directory.Exists(apiOutputDir).Should().BeTrue(
            $"API output directory should exist at '{apiOutputDir}'");

        var missing = ReadBundledModuleExpectations()
            .Where(module => !File.Exists(Path.Combine(apiOutputDir, module.EntryAssembly)))
            .Select(module => $"{module.Id} ({module.EntryAssembly})")
            .ToList();

        missing.Should().BeEmpty(
            $"the following module DLLs were not found in '{apiOutputDir}': {string.Join(", ", missing)}");
    }

    [TestCaseSource(nameof(ExpectedModuleDlls))]
    public void ModuleDllContainsISharpClawCoreModuleImplementation(string dllName)
    {
        var apiOutputDir = ResolveApiOutputDirectory();
        var dllPath = Path.Combine(apiOutputDir, dllName);

        File.Exists(dllPath).Should().BeTrue($"'{dllName}' must be present in API output");

        var assembly = Assembly.LoadFrom(dllPath);
        var moduleType = Type.GetType(
            "SharpClaw.Contracts.Modules.ISharpClawCoreModule, SharpClaw.Contracts",
            throwOnError: false);

        moduleType.Should().NotBeNull("SharpClaw.Contracts must be loaded");

        var implementations = assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false }
                        && moduleType!.IsAssignableFrom(t)
                        && t.GetConstructor(Type.EmptyTypes) is not null)
            .ToList();

        implementations.Should().NotBeEmpty(
            $"'{dllName}' must contain at least one public parameterless ISharpClawCoreModule implementation");
    }

    [Test]
    public void ApiOutputContainsBundledSidecarLayout()
    {
        AssertBundledSidecarLayout(ResolveApiOutputDirectory());
    }

    [Test]
    public void OutOfProcessModuleHostPackagePayloadIsPresentInNuGetCache()
    {
        var packageRoot = ResolveNuGetPackageRoot("SharpClaw.ModuleHost.OutOfProcess");
        var toolsDir = Path.Combine(packageRoot, "tools", "net10.0", "any");

        File.Exists(Path.Combine(toolsDir, "SharpClaw.ModuleHost.OutOfProcess.dll"))
            .Should().BeTrue("the sidecar host package must expose the executable host DLL");
        File.Exists(Path.Combine(toolsDir, "SharpClaw.ModuleHost.OutOfProcess.deps.json"))
            .Should().BeTrue("the sidecar host package must expose the executable deps file");
        File.Exists(Path.Combine(toolsDir, "SharpClaw.ModuleHost.OutOfProcess.runtimeconfig.json"))
            .Should().BeTrue("the sidecar host package must expose the executable runtimeconfig");
    }

    [Test]
    public async Task PublishedApiOutputContainsBundledSidecarPayload()
    {
        var publishDir = Path.Combine(
            Path.GetTempPath(),
            "sharpclaw-publish-layout-" + Guid.NewGuid().ToString("N"));

        try
        {
            var result = await PublishApiNoBuildAsync(publishDir);

            result.ExitCode.Should().Be(
                0,
                $"dotnet publish should succeed. Output:{Environment.NewLine}{result.Output}");

            AssertBundledSidecarLayout(publishDir);
        }
        finally
        {
            TryDeleteDirectory(publishDir);
        }
    }

    private static void AssertBundledSidecarLayout(string outputDir)
    {
        Directory.Exists(outputDir).Should().BeTrue(
            $"output directory should exist at '{outputDir}'");

        File.Exists(Path.Combine(outputDir, "SharpClaw.ModuleHost.OutOfProcess.dll"))
            .Should().BeTrue("the shared .NET sidecar host DLL must be present");
        File.Exists(Path.Combine(outputDir, "SharpClaw.ModuleHost.OutOfProcess.deps.json"))
            .Should().BeTrue("the shared .NET sidecar host deps file must be present");
        File.Exists(Path.Combine(outputDir, "SharpClaw.ModuleHost.OutOfProcess.runtimeconfig.json"))
            .Should().BeTrue("the shared .NET sidecar host runtimeconfig must be present");

        foreach (var module in ReadBundledModuleExpectations())
        {
            module.Runtime.Should().Be("dotnet", $"{module.ManifestPath} must describe a .NET module");
            module.HostMode.Should().Be("sidecar", $"{module.ManifestPath} must opt into sidecar hosting");

            var entryAssemblyPath = Path.Combine(outputDir, module.EntryAssembly);
            File.Exists(entryAssemblyPath).Should().BeTrue(
                $"bundled module '{module.Id}' entry assembly must be present at '{entryAssemblyPath}'");

            var outputManifestPath = Path.Combine(outputDir, "modules", module.Id, "module.json");
            File.Exists(outputManifestPath).Should().BeTrue(
                $"bundled module '{module.Id}' manifest must be present at '{outputManifestPath}'");

            var outputManifest = ReadBundledModuleExpectation(outputManifestPath);
            outputManifest.Id.Should().Be(module.Id);
            outputManifest.Runtime.Should().Be(module.Runtime);
            outputManifest.HostMode.Should().Be(module.HostMode);
            outputManifest.EntryAssembly.Should().Be(module.EntryAssembly);
        }
    }

    private static string ResolveApiOutputDirectory()
    {
        var testBinDir = ResolveTestOutputDirectory();
        var solutionRoot = ResolveSolutionRoot();
        var config = ResolveConfiguration();
        var tfm = new DirectoryInfo(testBinDir).Name;

        return Path.Combine(solutionRoot, "SharpClaw.Runtime", "Host", "bin", config, tfm);
    }

    private static IReadOnlyList<BundledModuleExpectation> ReadBundledModuleExpectations()
    {
        var solutionRoot = ResolveSolutionRoot();
        var testHarnessManifests = new[]
        {
            Path.Combine(solutionRoot, "SharpClaw.DefaultModules", "TestHarness.OutOfProcess", "module.json"),
        };

        var sourceModules = testHarnessManifests
            .Where(path => File.Exists(path) && !IsBuildOutputPath(path))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Select(ReadBundledModuleExpectation);
        var packagedModules = ReadPackagedModuleExpectations();

        return sourceModules
            .Concat(packagedModules)
            .OrderBy(module => module.Id, StringComparer.Ordinal)
            .ToArray();
    }

    [Test]
    public void PackagedModulePayloadsArePresentInNuGetCache()
    {
        foreach (var module in ReadPackagedModuleExpectations())
        {
            File.Exists(module.ManifestPath).Should().BeTrue(
                $"packaged module '{module.Id}' must expose a module manifest at '{module.ManifestPath}'");
            File.Exists(module.PackageEntryAssemblyPath)
                .Should()
                .BeTrue($"packaged module '{module.Id}' must expose its entry assembly at '{module.PackageEntryAssemblyPath}'");
        }
    }

    [Test]
    public void InProcessTestHarnessPayloadIsBuiltAsSeparateModule()
    {
        var solutionRoot = ResolveSolutionRoot();
        var config = ResolveConfiguration();
        var testBinDir = ResolveTestOutputDirectory();
        var tfm = new DirectoryInfo(testBinDir).Name;
        var outputDir = Path.Combine(
            solutionRoot,
            "SharpClaw.DefaultModules",
            "TestHarness.InProcess",
            "bin",
            config,
            tfm);

        File.Exists(Path.Combine(outputDir, "SharpClaw.DefaultModules.TestHarness.InProcess.dll"))
            .Should()
            .BeTrue("the in-process harness module project must build its own payload assembly");
        File.Exists(Path.Combine(outputDir, "modules", "sharpclaw_test_harness_in_process", "module.json"))
            .Should()
            .BeTrue("the in-process harness module project must produce its own module manifest");
    }

    private static IReadOnlyList<BundledModuleExpectation> ReadPackagedModuleExpectations()
    {
        var packages = new[]
        {
            (
                PackageId: "SharpClaw.Modules.EditorCommon",
                ManifestPath: Path.Combine("sharpclaw", "module.json"),
                EntryAssemblyDirectory: "sharpclaw"),
            (
                PackageId: "SharpClaw.Modules.VS2026Editor",
                ManifestPath: Path.Combine("sharpclaw", "module.json"),
                EntryAssemblyDirectory: "sharpclaw"),
            (
                PackageId: "SharpClaw.Modules.VSCodeEditor",
                ManifestPath: Path.Combine("sharpclaw", "module.json"),
                EntryAssemblyDirectory: "sharpclaw"),
            (
                PackageId: "SharpClaw.Modules.AgentOrchestration",
                ManifestPath: Path.Combine("sharpclaw", "module.json"),
                EntryAssemblyDirectory: "sharpclaw"),
            (
                PackageId: "SharpClaw.Modules.Metrics",
                ManifestPath: Path.Combine("sharpclaw", "module.json"),
                EntryAssemblyDirectory: "sharpclaw"),
            (
                PackageId: "SharpClaw.Modules.ModuleDev",
                ManifestPath: Path.Combine("sharpclaw", "module.json"),
                EntryAssemblyDirectory: "sharpclaw"),
            ProviderPackage(
                "SharpClaw.Modules.Providers.Anthropic",
                "sharpclaw_providers_anthropic"),
            ProviderPackage(
                "SharpClaw.Modules.Providers.Google",
                "sharpclaw_providers_google"),
            ProviderPackage(
                "SharpClaw.Modules.Providers.LlamaSharp",
                "sharpclaw_providers_llamasharp"),
            ProviderPackage(
                "SharpClaw.Modules.Providers.Ollama",
                "sharpclaw_providers_ollama"),
            (
                PackageId: "SharpClaw.Modules.Providers.OpenAICompatible",
                ManifestPath: Path.Combine(
                    "contentFiles",
                    "any",
                    "net10.0",
                    "modules",
                    "sharpclaw_providers_openai_compat",
                    "module.json"),
                EntryAssemblyDirectory: Path.Combine("lib", "net10.0")),
        };

        return packages
            .Select(packageInfo => ReadPackagedModuleExpectation(
                packageInfo.PackageId,
                packageInfo.ManifestPath,
                packageInfo.EntryAssemblyDirectory))
            .ToArray();
    }

    private static (
        string PackageId,
        string ManifestPath,
        string EntryAssemblyDirectory) ProviderPackage(
            string packageId,
            string moduleId) =>
        (
            packageId,
            Path.Combine("contentFiles", "any", "net10.0", "modules", moduleId, "module.json"),
            Path.Combine("lib", "net10.0"));

    private static BundledModuleExpectation ReadPackagedModuleExpectation(
        string packageId,
        string manifestRelativePath,
        string entryAssemblyDirectory)
    {
        var packageRoot = ResolveNuGetPackageRoot(packageId);
        var expectation = ReadBundledModuleExpectation(Path.Combine(packageRoot, manifestRelativePath));

        return new BundledModuleExpectation(
            expectation.Id,
            expectation.Runtime,
            expectation.HostMode,
            expectation.EntryAssembly,
            expectation.ManifestPath,
            Path.Combine(packageRoot, entryAssemblyDirectory, expectation.EntryAssembly));
    }

    private static string ResolveNuGetPackageRoot(string packageId)
    {
        var version = ResolveCentralPackageVersion(packageId);
        var packagesRoot = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
        if (string.IsNullOrWhiteSpace(packagesRoot))
        {
            packagesRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".nuget",
                "packages");
        }

        return Path.Combine(packagesRoot, packageId.ToLowerInvariant(), version.ToLowerInvariant());
    }

    private static string ResolveCentralPackageVersion(string packageId)
    {
        var propsPath = Path.Combine(ResolveSolutionRoot(), "Directory.Packages.props");
        var document = XDocument.Load(propsPath);
        var version = document
            .Descendants("PackageVersion")
            .Where(element => string.Equals(
                (string?)element.Attribute("Include"),
                packageId,
                StringComparison.Ordinal))
            .Select(element => (string?)element.Attribute("Version"))
            .SingleOrDefault();

        version.Should().NotBeNullOrWhiteSpace(
            $"{propsPath} must centrally pin {packageId}");
        return version!;
    }

    private static BundledModuleExpectation ReadBundledModuleExpectation(string manifestPath)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var root = document.RootElement;

        return new BundledModuleExpectation(
            RequiredString(root, "id", manifestPath),
            RequiredString(root, "runtime", manifestPath),
            RequiredString(root, "hostMode", manifestPath),
            RequiredString(root, "entryAssembly", manifestPath),
            manifestPath);
    }

    private static string RequiredString(JsonElement root, string propertyName, string manifestPath)
    {
        root.TryGetProperty(propertyName, out var property).Should().BeTrue(
            $"{manifestPath} must contain '{propertyName}'");

        var value = property.GetString();
        value.Should().NotBeNullOrWhiteSpace(
            $"{manifestPath} must contain a non-empty '{propertyName}'");

        return value!;
    }

    private static bool IsBuildOutputPath(string path)
    {
        var binSegment = $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}";
        var objSegment = $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}";
        return path.Contains(binSegment, StringComparison.OrdinalIgnoreCase)
               || path.Contains(objSegment, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<(int ExitCode, string Output)> PublishApiNoBuildAsync(string publishDir)
    {
        var apiProject = Path.Combine(
            ResolveSolutionRoot(),
            "SharpClaw.Runtime",
            "Host",
            "SharpClaw.Runtime.Host.csproj");

        var startInfo = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("publish");
        startInfo.ArgumentList.Add(apiProject);
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(ResolveConfiguration());
        startInfo.ArgumentList.Add("--no-build");
        startInfo.ArgumentList.Add("-o");
        startInfo.ArgumentList.Add(publishDir);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start dotnet publish.");

        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit(120_000))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // The process may have exited between the timeout and Kill.
            }

            throw new TimeoutException("dotnet publish did not finish within 120 seconds.");
        }

        var output = string.Join(Environment.NewLine, await stdout, await stderr);
        TestContext.Progress.WriteLine(output);

        return (process.ExitCode, output);
    }

    private static string ResolveTestOutputDirectory()
        => Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;

    private static string ResolveConfiguration()
        => new DirectoryInfo(ResolveTestOutputDirectory()).Parent!.Name;

    private static string ResolveSolutionRoot()
    {
        var testBinDir = ResolveTestOutputDirectory();
        return Path.GetFullPath(Path.Combine(testBinDir, "..", "..", "..", ".."));
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TestContext.Progress.WriteLine(
                $"Could not delete temporary publish directory '{path}': {ex.Message}");
        }
    }
}
