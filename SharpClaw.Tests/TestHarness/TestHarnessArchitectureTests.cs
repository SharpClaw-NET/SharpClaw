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
        workflow.Should().Contain("domain: Jobs");
        workflow.Should().Contain("domain: Tools");
        workflow.Should().Contain("domain: Costs");
        workflow.Should().Contain("domain: Cache");
        workflow.Should().Contain("domain: Agent Orchestration");
        workflow.Should().Contain("domain: Defaults");
        workflow.Should().Contain("domain: Module Contracts");
        workflow.Should().Contain("domain: Provider Registration");
        workflow.Should().Contain("FullyQualifiedName~TestHarnessApiGatewaySurfaceTests");
        workflow.Should().Contain("FullyQualifiedName~TestHarnessCacheBehaviorTests");
        workflow.Should().Contain("domain: Providers");
        workflow.Should().Contain("domain: Persistence");
        workflow.Should().Contain("domain: Streaming");
        workflow.Should().Contain("domain: Chat Throughput");
        workflow.Should().Contain("domain: Cache and Resolution");
        workflow.Should().Contain("FullyQualifiedName~PerformanceGate_ColdChatAfterCacheClear");
        workflow.Should().Contain("--filter \"TestCategory!=PerformanceDiagnostic&TestCategory!=PerformanceGate&(${{ matrix.filter }})\"");
        workflow.Should().Contain("--filter \"TestCategory=PerformanceGate&(${{ matrix.filter }})\"");
        workflow.Should().NotContain("name: Performance Diagnostics");
        workflow.Should().NotContain("name: Correctness Tests");
        workflow.Should().NotContain("name: Performance Gates");
        workflow.Should().NotContain("domain: Agent Orchestration and Defaults");
        workflow.Should().NotContain("SHARPCLAW_RUN_PERF_DIAGNOSTICS");
        workflow.Should().NotContain("--filter \"TestCategory=PerformanceDiagnostic\"");
    }

    [Test]
    public void RequiredCiRulesetRequiresEveryPublicDomainCheck()
    {
        var root = FindSolutionRoot();
        var rulesetPath = Path.Combine(root, ".github", "rulesets", "required-ci-domains.json");
        using var ruleset = JsonDocument.Parse(File.ReadAllText(rulesetPath));

        var rootElement = ruleset.RootElement;
        rootElement.GetProperty("name").GetString().Should().Be("Required CI Domains");
        rootElement.GetProperty("target").GetString().Should().Be("branch");
        rootElement.GetProperty("enforcement").GetString().Should().Be("active");

        var includes = rootElement
            .GetProperty("conditions")
            .GetProperty("ref_name")
            .GetProperty("include")
            .EnumerateArray()
            .Select(e => e.GetString())
            .ToList();
        includes.Should().Contain("~DEFAULT_BRANCH");
        includes.Should().Contain("refs/heads/release/**");

        var bypassActors = rootElement
            .GetProperty("bypass_actors")
            .EnumerateArray()
            .ToList();
        bypassActors.Should().ContainSingle();
        bypassActors[0].GetProperty("actor_id").GetInt32().Should().Be(5);
        bypassActors[0].GetProperty("actor_type").GetString().Should().Be("RepositoryRole");
        bypassActors[0].GetProperty("bypass_mode").GetString().Should().Be("always");

        var requiredChecks = rootElement
            .GetProperty("rules")
            .EnumerateArray()
            .Single(rule => rule.GetProperty("type").GetString() == "required_status_checks")
            .GetProperty("parameters")
            .GetProperty("required_status_checks")
            .EnumerateArray()
            .Select(e => e.GetProperty("context").GetString())
            .ToList();

        var expectedChecks = new[]
        {
            "Correctness / Core",
            "Correctness / CLI Commands",
            "Correctness / CLI REPL",
            "Correctness / Gateway",
            "Correctness / Frontend",
            "Correctness / Persistence",
            "Correctness / Providers",
            "Correctness / Tasks",
            "Correctness / Chat",
            "Correctness / Jobs",
            "Correctness / Tools",
            "Correctness / Costs",
            "Correctness / Cache",
            "Correctness / Agent Orchestration",
            "Correctness / Defaults",
            "Correctness / Module Contracts",
            "Correctness / Provider Registration",
            "Performance / Streaming",
            "Performance / Chat Throughput",
            "Performance / Jobs",
            "Performance / Tools",
            "Performance / Cache and Resolution",
        };

        requiredChecks.Should().BeEquivalentTo(expectedChecks);
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
