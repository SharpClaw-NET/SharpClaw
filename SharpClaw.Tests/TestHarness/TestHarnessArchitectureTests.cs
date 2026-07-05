using System.Reflection;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Extensions.Configuration;
using SharpClaw.Runtime.BLL.Modules;
using SharpClaw.Contracts.Entities.Core.Context;
using SharpClaw.Modules.TestHarness;
using SharpClaw.Core.Modules;

namespace SharpClaw.Tests.TestHarness;

[TestFixture]
public sealed class TestHarnessArchitectureTests
{
    private static readonly string[] ExpectedCorrectnessDomains =
    [
        "API Key Provider",
        "Architecture Core Dependencies",
        "Architecture Module Dependencies",
        "Clients Finish Reasons",
        "Core Header Tags",
        "Core Chat Cache",
        "Cross Thread History",
        "CLI Channel Commands",
        "CLI REPL",
        "Gateway Abstraction Boundaries",
        "Gateway Internal API Client",
        "Gateway Synthetic Modules",
        "Gateway Endpoint Catalog",
        "Gateway Endpoint Toggles",
        "Gateway Route Parity",
        "Gateway Lifecycle",
        "Gateway Hot Reload",
        "Gateway Chat Surface",
        "Gateway Chat Errors And Cancellation",
        "API Job Contracts",
        "Gateway Job Contracts",
        "Frontend Instances",
        "Frontend Gateway Process",
        "Frontend Uno Client State",
        "Persistence Files",
        "Persistence Cold Store",
        "Persistence Integrity",
        "Persistence Transactions",
        "Persistence Instances",
        "Providers Capabilities",
        "Providers Hosted OpenAI Compatible",
        "Providers Google",
        "LlamaSharp Schema Conversion",
        "LlamaSharp Parameter Validation",
        "LlamaSharp Tool Calling",
        "LlamaSharp Local Inference",
        "Tasks Scripts Compiler Parser",
        "Tasks Scripts Semantics Validator",
        "Tasks Step Keys",
        "Tasks Execution Lifecycle",
        "Tasks Shared Data",
        "Tasks Triggers",
        "Chat Prompt Surface",
        "Chat Tool Permissions",
        "Chat Costs And Budgets",
        "Chat Thread History",
        "Chat Thread Concurrency",
        "Chat Agent Selection",
        "Chat Prompt Assembly",
        "Chat Streaming",
        "Jobs Direct",
        "Jobs Lifecycle",
        "Jobs Streaming",
        "Tools Inline Permissions",
        "Tools Arguments Output And Latency",
        "Tools Provider Stream Bulk",
        "Tools Provider Stream Cache",
        "Tools Provider Stream Lifecycle",
        "Costs Job Accounting",
        "Costs Streaming",
        "Costs Multi Agent",
        "Costs Module Hooks",
        "Cache Header Invalidations",
        "Cache Tool Settings",
        "Agent Orchestration Access",
        "Agent Orchestration History",
        "Defaults Mutations",
        "Defaults Resource Resolution",
        "Module API Host Startup",
        "Module Bundled Outputs",
        "Module External Lifecycle",
        "Module Foreign Runtime",
        "Module Sidecar Parity",
        "Module Harness Integration",
        "Module Harness Cost And Matrix",
        "Module Contracts Architecture",
        "Provider Registration"
    ];

    private static readonly string[] ExpectedPerformanceDomains =
    [
        "Streaming End To End",
        "Streaming API",
        "Streaming Gateway",
        "Streaming Concurrency",
        "Chat Non Streaming",
        "Chat Concurrent Throughput",
        "Chat Sequential Warm Cache",
        "Jobs Direct Actions",
        "Jobs Summaries",
        "Jobs Parallel",
        "Tools Permission Checks",
        "Tools Provider Stream Serial",
        "Tools Provider Stream Parallel",
        "Accessible Threads",
        "Prompt Assembly",
        "Cost Lookup",
        "Provider Resolution",
        "Cold Chat Cache"
    ];

    [Test]
    public void ProductionTemplateDisablesHarnessAndDevTemplateEnablesIt()
    {
        var root = FindSolutionRoot();
        var prod = LoadTemplate(Path.Combine(
            root,
            "SharpClaw.Runtime.INF",
            "Environment",
            ".env.template"));
        var dev = LoadTemplate(Path.Combine(
            root,
            "SharpClaw.Runtime.INF",
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
            Path.Combine(root, "SharpClaw.Runtime.BLL"),
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
    public void HostProjectsUsePackagedCoreContractsWorkflow()
    {
        var root = FindSolutionRoot();
        var projectRelativePaths = new[]
        {
            "SharpClaw.Runtime.Host/SharpClaw.Runtime.Host.csproj",
            "SharpClaw.Runtime.BLL/SharpClaw.Runtime.BLL.csproj",
            "SharpClaw.Runtime.INF/SharpClaw.Runtime.INF.csproj",
            "SharpClaw.Gateway/SharpClaw.Gateway.csproj",
            "SharpClaw.Client.Uno/SharpClaw.Client.Uno.csproj"
        };

        foreach (var relativePath in projectRelativePaths)
        {
            var project = XDocument.Load(Path.Combine(root, relativePath));
            var contractProjectReferences = project.Descendants("ProjectReference")
                .Select(e => e.Attribute("Include")?.Value ?? "")
                .Where(path => path.Contains("SharpClaw.Contracts", StringComparison.OrdinalIgnoreCase))
                .ToList();
            var contractPackageReferences = project.Descendants("PackageReference")
                .Select(e => e.Attribute("Include")?.Value)
                .Where(package => string.Equals(package, "SharpClaw.Contracts", StringComparison.Ordinal))
                .ToList();

            contractProjectReferences
                .Should()
                .BeEmpty("contracts are now consumed only through the published SharpClaw.Contracts package");
            contractPackageReferences
                .Should()
                .ContainSingle();

            project.Descendants("PackageReference")
                .Single(e => string.Equals(
                    e.Attribute("Include")?.Value,
                    "SharpClaw.Contracts",
                    StringComparison.Ordinal))
                .Attribute("Condition")?.Value
                .Should()
                .BeNull("there is no local contracts-project escape hatch anymore");
        }

        var appCoreProject = XDocument.Load(Path.Combine(
            root,
            "SharpClaw.Runtime.BLL",
            "SharpClaw.Runtime.BLL.csproj"));
        appCoreProject.Descendants("PackageReference")
            .Select(e => e.Attribute("Include")?.Value)
            .Should()
            .Contain("SharpClaw.Core");
    }

    [Test]
    public void BundledTestHarnessIsPackagedButNotAProductionCompileReference()
    {
        var root = FindSolutionRoot();
        var apiProject = XDocument.Load(Path.Combine(
            root,
            "SharpClaw.Runtime.Host",
            "SharpClaw.Runtime.Host.csproj"));

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

        var workflowParts = workflow.Split("  performance:", StringSplitOptions.None);
        workflowParts.Should().HaveCount(2);
        var correctnessDomains = ExtractWorkflowDomains(workflowParts[0]);
        var performanceDomains = ExtractWorkflowDomains(workflowParts[1]);

        correctnessDomains.Should().Equal(ExpectedCorrectnessDomains);
        performanceDomains.Should().Equal(ExpectedPerformanceDomains);

        correctnessDomains.Count.Should().BeGreaterThan(70);
        performanceDomains.Count.Should().BeGreaterThan(15);

        workflow.Should().Contain("FullyQualifiedName~GatewaySseProxy_ForwardsRealHttpSsePath");
        workflow.Should().Contain("FullyQualifiedName~ApiJob");
        workflow.Should().Contain("FullyQualifiedName~EffectiveToolDefinitionsStayWarmUntilAgentToolSettingsChange");
        workflow.Should().Contain("FullyQualifiedName~SharpClaw.Tests.Providers.LlamaSharp.LocalInference");
        workflow.Should().Contain("FullyQualifiedName~PerformanceGate_ColdChatAfterCacheClear");
        workflow.Should().Contain("--filter \"TestCategory!=PerformanceDiagnostic&TestCategory!=PerformanceGate&(${{ matrix.filter }})\"");
        workflow.Should().Contain("--filter \"TestCategory=PerformanceGate&(${{ matrix.filter }})\"");
        workflow.Should().NotContain("name: Performance Diagnostics");
        workflow.Should().NotContain("name: Correctness Tests");
        workflow.Should().NotContain("name: Performance Gates");
        workflow.Should().NotContain("domain: Core\n");
        workflow.Should().NotContain("domain: Gateway\n");
        workflow.Should().NotContain("domain: Frontend\n");
        workflow.Should().NotContain("domain: Persistence\n");
        workflow.Should().NotContain("domain: Providers\n");
        workflow.Should().NotContain("domain: Tasks\n");
        workflow.Should().NotContain("domain: Chat\n");
        workflow.Should().NotContain("domain: Jobs\n");
        workflow.Should().NotContain("domain: Tools\n");
        workflow.Should().NotContain("domain: Costs\n");
        workflow.Should().NotContain("domain: Cache\n");
        workflow.Should().NotContain("domain: Streaming\n");
        workflow.Should().NotContain("domain: Gateway Abstractions\n");
        workflow.Should().NotContain("domain: Gateway Routing\n");
        workflow.Should().NotContain("domain: Gateway Harness Surface\n");
        workflow.Should().NotContain("domain: Frontend Runtime\n");
        workflow.Should().NotContain("domain: Providers Local LlamaSharp\n");
        workflow.Should().NotContain("domain: Tasks Scripts\n");
        workflow.Should().NotContain("domain: Tasks Lifecycle\n");
        workflow.Should().NotContain("domain: Chat Pipeline\n");
        workflow.Should().NotContain("domain: Tools Correctness\n");
        workflow.Should().NotContain("domain: Tools Repeated Interactions\n");
        workflow.Should().NotContain("domain: Cache Correctness\n");
        workflow.Should().NotContain("domain: Defaults Resources\n");
        workflow.Should().NotContain("domain: Module Packaging\n");
        workflow.Should().NotContain("domain: Module Contracts Harness\n");
        workflow.Should().NotContain("domain: Chat Throughput\n");
        workflow.Should().NotContain("domain: Cache and Resolution");
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

        var rules = rootElement
            .GetProperty("rules")
            .EnumerateArray()
            .ToList();
        var ruleTypes = rules.Select(rule => rule.GetProperty("type").GetString()).ToArray();
        ruleTypes.Should().Contain("required_status_checks");
        ruleTypes.Should().Contain("pull_request");
        ruleTypes.Should().Contain("code_quality");
        ruleTypes.Should().Contain("code_scanning");

        var statusRuleParameters = rules
            .Single(rule => rule.GetProperty("type").GetString() == "required_status_checks")
            .GetProperty("parameters");
        statusRuleParameters
            .GetProperty("strict_required_status_checks_policy")
            .GetBoolean()
            .Should()
            .BeTrue();
        statusRuleParameters
            .GetProperty("do_not_enforce_on_create")
            .GetBoolean()
            .Should()
            .BeFalse();

        var requiredChecks = statusRuleParameters
            .GetProperty("required_status_checks")
            .EnumerateArray()
            .Select(e => e.GetProperty("context").GetString())
            .ToList();

        var workflow = File.ReadAllText(Path.Combine(root, ".github", "workflows", "ci.yml"));
        var expectedChecks = ExtractRequiredCheckContextsFromWorkflow(workflow);

        expectedChecks.Count.Should().BeGreaterThan(90);
        requiredChecks.Should().Equal(expectedChecks);
    }

    private static IReadOnlyList<string> ExtractRequiredCheckContextsFromWorkflow(string workflow)
    {
        var workflowParts = workflow.Split("  performance:", StringSplitOptions.None);
        workflowParts.Should().HaveCount(2);
        return ExtractWorkflowDomains(workflowParts[0])
            .Select(domain => $"Correctness / {domain}")
            .Concat(ExtractWorkflowDomains(workflowParts[1]).Select(domain => $"Performance / {domain}"))
            .ToArray();
    }

    private static IReadOnlyList<string> ExtractWorkflowDomains(string workflowPart)
    {
        const string marker = "- domain: ";
        return workflowPart
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.StartsWith(marker, StringComparison.Ordinal))
            .Select(line => line[marker.Length..].Trim().Trim('\'', '"'))
            .ToArray();
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
