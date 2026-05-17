using System.Reflection;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Extensions.Configuration;
using SharpClaw.Application.Core.Modules;
using SharpClaw.Contracts.Entities.Core.Context;
using SharpClaw.Modules.TestHarness;

namespace SharpClaw.Tests.TestHarness;

[TestFixture]
public sealed class TestHarnessArchitectureTests
{
    [Test]
    public void ProductionTemplateDisablesHarnessAndDevTemplateEnablesIt()
    {
        var root = FindSolutionRoot();
        var prod = LoadTemplate(Path.Combine(
            root,
            "SharpClaw.Application.Infrastructure",
            "Environment",
            ".env.template"));
        var dev = LoadTemplate(Path.Combine(
            root,
            "SharpClaw.Application.Infrastructure",
            "Environment",
            ".dev.env.template"));

        ModuleLoader.IsEnabledInConfig(TestHarnessConstants.ModuleId, prod).Should().BeFalse();
        ModuleLoader.IsEnabledInConfig(TestHarnessConstants.ModuleId, dev).Should().BeTrue();
    }

    [Test]
    public void DisabledModulesDoNotRemoveCoreChannelProperties()
    {
        var registry = new ModuleRegistry();

        registry.GetModule(TestHarnessConstants.ModuleId).Should().BeNull();
        typeof(ChannelDB).GetProperty(nameof(ChannelDB.AgentId)).Should().NotBeNull();
        typeof(ChannelDB).GetProperty(nameof(ChannelDB.AllowedAgents)).Should().NotBeNull();
        typeof(ChannelDB).GetProperty(nameof(ChannelDB.DisableChatHeader)).Should().NotBeNull();
        typeof(ChannelDB).GetProperty(nameof(ChannelDB.CustomChatHeader)).Should().NotBeNull();
    }

    [Test]
    public void RemovedBridgeAndUnsupportedCoreTestPathsStayOutOfCoreAndTests()
    {
        var root = FindSolutionRoot();
        var searchedRoots = new[]
        {
            Path.Combine(root, "SharpClaw.Application.Core"),
            Path.Combine(root, "SharpClaw.Tests")
        };
        var banned = new[]
        {
            "ChatProcessing" + "Bridge",
            "WellKnown" + "ProviderKeys",
            "trans" + "cription",
            "voi" + "ce",
            "Whis" + "per"
        };

        var offenders = searchedRoots
            .SelectMany(dir => Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                           && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .SelectMany(path =>
            {
                var text = File.ReadAllText(path);
                return banned
                    .Where(term => text.Contains(term, StringComparison.OrdinalIgnoreCase))
                    .Select(term => $"{Path.GetRelativePath(root, path)} contains {term}");
            })
            .ToList();

        offenders.Should().BeEmpty();
    }

    [Test]
    public void HostProjectsConsumeContractsAsPackageReferences()
    {
        var root = FindSolutionRoot();
        var projectRelativePaths = new[]
        {
            "SharpClaw.Application.API/SharpClaw.Application.API.csproj",
            "SharpClaw.Application.Core/SharpClaw.Application.Core.csproj",
            "SharpClaw.Application.Infrastructure/SharpClaw.Application.Infrastructure.csproj",
            "SharpClaw.Gateway/SharpClaw.Gateway.csproj",
            "SharpClaw.Uno/SharpClaw.Uno.csproj"
        };

        foreach (var relativePath in projectRelativePaths)
        {
            var project = XDocument.Load(Path.Combine(root, relativePath));
            project.Descendants("ProjectReference")
                .Select(e => e.Attribute("Include")?.Value ?? "")
                .Should()
                .NotContain(path => path.Contains("SharpClaw.Contracts", StringComparison.OrdinalIgnoreCase));
            project.Descendants("PackageReference")
                .Select(e => e.Attribute("Include")?.Value)
                .Should()
                .Contain("SharpClaw.Contracts");
        }
    }

    [Test]
    public void BundledTestHarnessIsPackagedButNotAProductionCompileReference()
    {
        var root = FindSolutionRoot();
        var apiProject = XDocument.Load(Path.Combine(
            root,
            "SharpClaw.Application.API",
            "SharpClaw.Application.API.csproj"));

        var harnessReference = apiProject.Descendants("ProjectReference")
            .Single(e => (e.Attribute("Include")?.Value ?? "")
                .Contains("DefaultModules\\TestHarness", StringComparison.OrdinalIgnoreCase));

        harnessReference.Element("ReferenceOutputAssembly")!.Value.Should().Be("false");
    }

    [Test]
    public void CiWorkflowSplitsCorrectnessAndPerformanceByDomain()
    {
        var root = FindSolutionRoot();
        var workflow = File.ReadAllText(Path.Combine(root, ".github", "workflows", "ci.yml"));

        workflow.Should().Contain("name: Correctness / ${{ matrix.domain }}");
        workflow.Should().Contain("name: Performance / ${{ matrix.domain }}");
        workflow.Should().Contain("domain: Chat");
        workflow.Should().Contain("domain: Jobs Tools Costs");
        workflow.Should().Contain("domain: Agent Orchestration and Defaults");
        workflow.Should().Contain("FullyQualifiedName~TestHarnessApiGatewaySurfaceTests");
        workflow.Should().Contain("domain: Providers");
        workflow.Should().Contain("domain: Persistence");
        workflow.Should().Contain("domain: Streaming");
        workflow.Should().Contain("domain: Chat Throughput");
        workflow.Should().Contain("domain: Jobs");
        workflow.Should().Contain("domain: Tools");
        workflow.Should().Contain("domain: Cache and Resolution");
        workflow.Should().Contain("--filter \"TestCategory!=PerformanceDiagnostic&TestCategory!=PerformanceGate&(${{ matrix.filter }})\"");
        workflow.Should().Contain("--filter \"TestCategory=PerformanceGate&(${{ matrix.filter }})\"");
        workflow.Should().NotContain("name: Performance Diagnostics");
        workflow.Should().NotContain("name: Correctness Tests");
        workflow.Should().NotContain("name: Performance Gates");
        workflow.Should().NotContain("SHARPCLAW_RUN_PERF_DIAGNOSTICS");
        workflow.Should().NotContain("--filter \"TestCategory=PerformanceDiagnostic\"");
    }

    private static IConfigurationRoot LoadTemplate(string path) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"Modules:{TestHarnessConstants.ModuleId}"] = ReadModuleFlag(path)
            })
            .Build();

    private static string ReadModuleFlag(string path)
    {
        using var doc = JsonDocument.Parse(
            File.ReadAllText(path),
            new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            });

        return doc.RootElement
            .GetProperty("Modules")
            .GetProperty(TestHarnessConstants.ModuleId)
            .GetString() ?? "false";
    }

    private static string FindSolutionRoot()
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "SharpClaw.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate SharpClaw.slnx from test assembly.");
    }
}
