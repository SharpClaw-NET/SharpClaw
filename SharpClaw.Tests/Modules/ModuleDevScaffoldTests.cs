using System.Text.Json;

using FluentAssertions;

using SharpClaw.Contracts.Modules;
using SharpClaw.Modules.ModuleDev.Services;

namespace SharpClaw.Tests.Modules;

[TestFixture]
public sealed class ModuleDevScaffoldTests
{
    private string _externalModulesDir = null!;

    [SetUp]
    public void SetUp()
    {
        _externalModulesDir = Path.Combine(
            Path.GetTempPath(),
            "SharpClawModuleDevScaffoldTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_externalModulesDir);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_externalModulesDir))
            Directory.Delete(_externalModulesDir, recursive: true);
    }

    [Test]
    public async Task ScaffoldAsync_WhenRuntimeIsNode_WritesNodeHostFiles()
    {
        var sut = CreateSut();

        var result = await sut.ScaffoldAsync(new ModuleScaffoldService.ScaffoldSpec(
            ModuleId: "sample_node",
            DisplayName: "Sample Node",
            ToolPrefix: "sn",
            Description: "A JavaScript module.",
            Runtime: "node"));

        result.Files.Should().Equal("package.json", "module.mjs", "module.json");
        File.Exists(Path.Combine(result.ModuleDir, "SampleNode.csproj")).Should().BeFalse();

        var manifestText = await File.ReadAllTextAsync(Path.Combine(result.ModuleDir, "module.json"));
        using var manifest = JsonDocument.Parse(manifestText);
        manifest.RootElement.GetProperty("runtime").GetString().Should().Be("node");
        manifest.RootElement.GetProperty("entrypoint").GetString().Should().Be("module.mjs");
        manifest.RootElement.GetProperty("entryAssembly").GetString().Should().BeEmpty();

        var moduleText = await File.ReadAllTextAsync(Path.Combine(result.ModuleDir, "module.mjs"));
        moduleText.Should().Contain("createSharpClawHost");
        moduleText.Should().Contain("/modules/sample_node/ping");

        var packageText = await File.ReadAllTextAsync(Path.Combine(result.ModuleDir, "package.json"));
        packageText.Should().Contain("\"@sharpclaw/module-host\": \"0.1.0-beta\"");
    }

    [Test]
    public async Task ScaffoldAsync_WhenRuntimeIsPython_WritesPythonHostFiles()
    {
        var sut = CreateSut();

        var result = await sut.ScaffoldAsync(new ModuleScaffoldService.ScaffoldSpec(
            ModuleId: "sample_python",
            DisplayName: "Sample Python",
            ToolPrefix: "sp",
            Description: "A Python module.",
            Runtime: "python"));

        result.Files.Should().Equal("pyproject.toml", "module.py", "module.json");
        File.Exists(Path.Combine(result.ModuleDir, "SamplePython.csproj")).Should().BeFalse();

        var manifestText = await File.ReadAllTextAsync(Path.Combine(result.ModuleDir, "module.json"));
        using var manifest = JsonDocument.Parse(manifestText);
        manifest.RootElement.GetProperty("runtime").GetString().Should().Be("python");
        manifest.RootElement.GetProperty("entrypoint").GetString().Should().Be("module.py");
        manifest.RootElement.GetProperty("entryAssembly").GetString().Should().BeEmpty();

        var moduleText = await File.ReadAllTextAsync(Path.Combine(result.ModuleDir, "module.py"));
        moduleText.Should().Contain("create_sharpclaw_host");
        moduleText.Should().Contain("/modules/sample_python/ping");

        var projectText = await File.ReadAllTextAsync(Path.Combine(result.ModuleDir, "pyproject.toml"));
        projectText.Should().Contain("sharpclaw-module-host==0.1.0b0");
    }

    [Test]
    public async Task ScaffoldAsync_WhenRuntimeIsDotNet_UsesContractsPackageReference()
    {
        var sut = CreateSut();

        var result = await sut.ScaffoldAsync(new ModuleScaffoldService.ScaffoldSpec(
            ModuleId: "sample_dotnet",
            DisplayName: "Sample Dotnet",
            ToolPrefix: "sd"));

        result.Files.Should().Equal("SampleDotnet.csproj", "SampleDotnetModule.cs", "module.json");

        var projectText = await File.ReadAllTextAsync(Path.Combine(result.ModuleDir, "SampleDotnet.csproj"));
        projectText.Should().Contain("<PackageReference Include=\"SharpClaw.Contracts\" />");
        projectText.Should().NotContain("<HintPath>");
    }

    [Test]
    public async Task WriteFileAsync_AllowsScriptModuleFiles()
    {
        var lifecycle = new FakeLifecycleManager(_externalModulesDir);
        var workspace = new ModuleWorkspaceService(lifecycle);

        var moduleFile = await workspace.WriteFileAsync("sample_node", "module.mjs", "export {};");
        var typeScriptFile = await workspace.WriteFileAsync("sample_node", "src/index.ts", "export {};");
        var pythonFile = await workspace.WriteFileAsync("sample_python", "module.py", "host = None");
        var pyprojectFile = await workspace.WriteFileAsync("sample_python", "pyproject.toml", "[project]");

        File.Exists(moduleFile.Path).Should().BeTrue();
        File.Exists(typeScriptFile.Path).Should().BeTrue();
        File.Exists(pythonFile.Path).Should().BeTrue();
        File.Exists(pyprojectFile.Path).Should().BeTrue();
    }

    private ModuleScaffoldService CreateSut()
    {
        var lifecycle = new FakeLifecycleManager(_externalModulesDir);
        var workspace = new ModuleWorkspaceService(lifecycle);
        var devEnvironment = new DevEnvironmentService(new FakeModuleInfoProvider(), lifecycle);
        return new ModuleScaffoldService(workspace, devEnvironment, lifecycle);
    }

    private sealed class FakeModuleInfoProvider : IModuleInfoProvider
    {
        public IReadOnlyList<ModuleInfo> GetAllModules() => [];
    }

    private sealed class FakeLifecycleManager(string externalModulesDir) : IModuleLifecycleManager
    {
        public string ExternalModulesDir { get; } = externalModulesDir;

        public bool IsModuleRegistered(string moduleId) => false;

        public bool IsToolPrefixRegistered(string toolPrefix) => false;

        public (ISharpClawModule Module, string ToolName)? FindToolByName(string toolName) => null;

        public Task<ModuleStateResponse> LoadExternalAsync(
            string moduleDir,
            IServiceProvider hostServices,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task UnloadExternalAsync(string moduleId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ModuleStateResponse> ReloadExternalAsync(
            string moduleId,
            IServiceProvider hostServices,
            CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
