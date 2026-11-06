using System.Text.Json;
using System.Text.RegularExpressions;

namespace SharpClaw.Tests.Ci;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812",
    Justification = "NUnit discovers and constructs this internal fixture through reflection; its tests are executed by the maintained test suite.")]
[TestFixture]
internal sealed partial class CiRequiredStatusCheckTests
{
    [Test]
    public void RequiredStatusChecksTrackWorkflowMatrixDomains()
    {
        var root = ResolveRepoRoot();
        var workflowPath = Path.Combine(root, ".github", "workflows", "ci.yml");
        var rulesetPath = Path.Combine(root, ".github", "rulesets", "required-ci-domains.json");

        var workflowContexts = ExtractWorkflowContexts(workflowPath)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var requiredContexts = ExtractRequiredContexts(rulesetPath)
            .Order(StringComparer.Ordinal)
            .ToArray();

        requiredContexts.Should().Equal(
            workflowContexts,
            "every CI matrix domain should be mirrored in the required-status-check ruleset");
    }

    [Test]
    public void CorrectnessRestoresTheReleaseTestProject()
    {
        var root = ResolveRepoRoot();
        var workflow = File.ReadAllText(Path.Combine(root, ".github", "workflows", "ci.yml"));

        workflow.Should().Contain("dotnet restore \"$TEST_PROJECT\" -p:Configuration=Release");
    }

    [Test]
    public void CiUsesNoPowerShellOrWindowsOnlyTooling()
    {
        var root = ResolveRepoRoot();
        var github = Path.Combine(root, ".github");
        var workflow = File.ReadAllText(Path.Combine(github, "workflows", "ci.yml"));

        foreach (var forbidden in new[] { "pwsh", ".ps1", "runs-on: windows", "$env:" })
            workflow.Should().NotContainEquivalentOf(forbidden, "CI must be reproducible on Linux with the .NET SDK alone");
        workflow.Should().Contain("runs-on: ubuntu-latest");
        Directory.EnumerateFiles(github, "*.ps1", SearchOption.AllDirectories).Should().BeEmpty();
        Directory.EnumerateFiles(github, "*.y*ml", SearchOption.AllDirectories)
            .Where(path => File.ReadAllText(path).Contains("shell: pwsh", StringComparison.OrdinalIgnoreCase))
            .Should().BeEmpty();
    }

    [Test]
    public void TestsThatStartPowerShellAreExcludedFromCi()
    {
        var root = ResolveRepoRoot();
        var workflow = File.ReadAllText(Path.Combine(root, ".github", "workflows", "ci.yml"));
        workflow.Should().Contain("TestCategory!=PowerShell&(${{ matrix.filter }})");

        var thisFile = Path.Combine(root, "SharpClaw.Tests", "Ci", "CiRequiredStatusCheckTests.cs");
        var starters = Directory.EnumerateFiles(Path.Combine(root, "SharpClaw.Tests"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !string.Equals(Path.GetFullPath(path), Path.GetFullPath(thisFile), StringComparison.Ordinal))
            .Where(path => PowerShellStartRegex().IsMatch(File.ReadAllText(path)))
            .ToArray();

        starters.Should().NotBeEmpty("the installer cleanup fixture still exercises scripts/test-installed-msix.ps1");
        foreach (var path in starters)
            File.ReadAllText(path).Should().Contain("[Category(\"PowerShell\")]",
                $"{Path.GetFileName(path)} starts PowerShell and must stay out of CI");
    }

    [Test]
    public void FrozenBomBuildsReviewedPersistenceAndExcludesArchivedRepositories()
    {
        var root = ResolveRepoRoot();
        var script = File.ReadAllText(Path.Combine(root, "build", "BuildFrozenBom.ps1"));

        script.Should().Contain("\"SharpClaw.Persistence\" = \"0.5.0-dev.20260922.3\"");
        script.Should().Contain("\"SharpClaw.Persistence.SQLServer\" = \"0.5.0-dev.20260922.3\"");
        script.Should().Contain("6d09ecc2ac18095494528f022cf4ac8a4dde8605");
        script.Should().Contain("Restore-And-Pack -Label \"persistence\"");
        script.Should().Contain("SharpClaw.Persistence.SQLServer");
        script.Should().Contain("\"SharpClaw.Modules.ModuleDev\" = \"0.5.0-dev.20260921.2\"");
        script.Should().Contain("929429f22d91832237f35f5f1fd569857321508b");
        script.Should().NotContain("SharpClaw.AgentOrchestration");
        script.Should().NotContain("agent-modules");
    }

    [Test]
    public void FrozenBomFreezesEachPackageAtItsPublishedVersionAndCommit()
    {
        var root = ResolveRepoRoot();
        var script = File.ReadAllText(Path.Combine(root, "build", "BuildFrozenBom.ps1"));

        script.Should().Contain("\"SharpClaw.Contracts\" = \"0.5.0-dev.20260920.2\"");
        script.Should().Contain("a78098e4c499d3b23c03121280771fb4f38c775a");
        script.Should().Contain("Name = \"gateway-contracts\"");
        script.Should().Contain("b7defdd58865df33d7d274dba3c8b20accb4ba6e");
        script.Should().Contain("\"SharpClaw.Gateway.Contracts\" = \"0.5.0-dev.20260920.1\"");
        script.Should().Contain("\"SharpClaw.Core\" = \"0.5.0-dev.20260925.4\"");
        script.Should().Contain("0b88f3b0cd267be4d1cd735985bf38b8834469bd");
        script.Should().Contain("Name = \"module-sdk\"");
        script.Should().Contain("08a51f516c6f5cc0be2ee4f891035ef244580bfa");
        script.Should().NotContain("module-sdk-sidecar");
        script.Should().Contain("\"SharpClaw.ModuleSDK\" = \"0.5.0-dev.20260920.2\"");
        script.Should().Contain("\"SharpClaw.ModuleSDK.Testing\" = \"0.5.0-dev.20260925.4\"");
        script.Should().Contain("\"SharpClaw.SidecarHost.OutOfProcess\" = \"0.5.0-dev.20260925.4\"");
        script.Should().Contain("-PackageVersionOverride $packageVersionOverrides[\"SharpClaw.Gateway.Contracts\"]");
        script.Should().Contain("JsonSchema.Net.dll");
        script.Should().Contain("JsonPointer.Net.dll");
        script.Should().Contain("Json.More.dll");
    }

    [Test]
    public void FrozenBomRequiresReviewedProvenanceForEveryPackage()
    {
        var root = ResolveRepoRoot();
        var script = File.ReadAllText(Path.Combine(root, "build", "BuildFrozenBom.ps1"));
        var sourceSection = script.Split(
            "foreach ($repository in $sourceRepositories)", 2, StringSplitOptions.None)[0];
        var packageMappings = PackageMappingRegex().Matches(sourceSection)
            .Cast<Match>()
            .ToArray();
        var packageIds = packageMappings
            .SelectMany(mapping => PackageIdentityRegex().Matches(mapping.Groups["ids"].Value)
                .Cast<Match>()
                .Select(match => match.Groups["id"].Value))
            .ToArray();

        packageMappings.Should().HaveCount(9);
        packageIds.Should().HaveCount(25).And.OnlyHaveUniqueItems();
        script.Should().Contain("Source '$($repository.Name)' has no package provenance mapping.");
        script.Should().Contain("Package '$packageId' has multiple source provenance mappings.");
        script.Should().Contain("Package '$packageId' has no reviewed source provenance.");
        script.Should().Contain("Package '$packageId' does not match its reviewed repository provenance.");
    }

    [Test]
    public void CiRunsOnlyOnCanonicalBranches()
    {
        var root = ResolveRepoRoot();
        var workflow = File.ReadAllText(Path.Combine(root, ".github", "workflows", "ci.yml"));

        workflow.Should().NotContain("release/**");
    }

    [Test]
    public void ProductionModuleDomainRequiresTheSidecarBootstrapRegressionSuite()
    {
        var workflow = File.ReadAllText(Path.Combine(ResolveRepoRoot(), ".github", "workflows", "ci.yml"));
        workflow.Should().Contain(
            "FullyQualifiedName~SharpClaw.Tests.Kernel.DefaultModuleSetHostGateTests|FullyQualifiedName~SharpClaw.Tests.Kernel.PackagedSidecarReadinessTests");
    }

    [Test]
    public void OwnershipAndPrivateIngressRegressionsRemainInCorrectnessMatrix()
    {
        var workflow = File.ReadAllText(Path.Combine(ResolveRepoRoot(), ".github", "workflows", "ci.yml"));
        foreach (var fixture in new[]
        {
            "SharpClaw.Tests.Kernel.RuntimeStartupInterruptionTests",
            "SharpClaw.Tests.Kernel.RuntimeBootstrapInterruptionTests",
            "SharpClaw.Tests.Kernel.RemoteProxyLifecycleTests",
            "SharpClaw.Tests.Kernel.RuntimeProviderModelCatalogTests",
            "SharpClaw.Tests.Gateway.GatewayPrivateConfigurationTests",
            "SharpClaw.Tests.Kernel.RemoteGatewayProxyTests",
            "SharpClaw.Tests.Frontend.ModulePackageStoreTests",
            "SharpClaw.Tests.Persistence.InstanceLockOwnershipTests",
            "SharpClaw.Tests.Kernel.RuntimeTransactionOwnershipTests",
            "SharpClaw.Tests.Gateway.GatewayActionBoundaryTests",
            "SharpClaw.Tests.Gateway.GatewayIngressAdmissionTests",
            "SharpClaw.Tests.Persistence.MigrationAdmissionTests",
            "SharpClaw.Tests.Persistence.DiscoveryCallbackOwnershipTests",
            "SharpClaw.Tests.Kernel.SidecarOutputDrainTests",
        })
            workflow.Should().Contain("FullyQualifiedName~" + fixture);
    }

    private static List<string> ExtractWorkflowContexts(string workflowPath)
    {
        File.Exists(workflowPath).Should().BeTrue();

        var contexts = new List<string>();
        string? currentJobTemplate = null;
        var inMatrixInclude = false;

        foreach (var line in File.ReadLines(workflowPath))
        {
            var jobMatch = JobNameRegex().Match(line);
            if (jobMatch.Success)
            {
                var name = jobMatch.Groups["name"].Value.Trim();
                currentJobTemplate = name.Contains("${{ matrix.domain }}", StringComparison.Ordinal)
                    ? name
                    : null;
                inMatrixInclude = false;
                continue;
            }

            if (currentJobTemplate is null)
                continue;

            if (string.Equals(line.Trim(), "include:", StringComparison.Ordinal))
            {
                inMatrixInclude = true;
                continue;
            }

            if (!inMatrixInclude)
                continue;

            var domainMatch = DomainRegex().Match(line);
            if (!domainMatch.Success)
                continue;

            var domain = domainMatch.Groups["domain"].Value.Trim().Trim('\'', '"');
            contexts.Add(currentJobTemplate.Replace("${{ matrix.domain }}", domain, StringComparison.Ordinal));
        }

        return contexts;
    }

    private static string[] ExtractRequiredContexts(string rulesetPath)
    {
        File.Exists(rulesetPath).Should().BeTrue();

        using var doc = JsonDocument.Parse(File.ReadAllText(rulesetPath));
        var rules = doc.RootElement.GetProperty("rules").EnumerateArray();
        foreach (var rule in rules)
        {
            if (!string.Equals(rule.GetProperty("type").GetString(), "required_status_checks", StringComparison.Ordinal))
                continue;

            return rule
                .GetProperty("parameters")
                .GetProperty("required_status_checks")
                .EnumerateArray()
                .Select(check => check.GetProperty("context").GetString())
                .Where(context => !string.IsNullOrWhiteSpace(context))
                .Select(context => context!)
                .ToArray();
        }

        Assert.Fail("No required_status_checks rule found in required-ci-domains.json.");
        return [];
    }

    private static string ResolveRepoRoot()
    {
        var configuredRoot = Environment.GetEnvironmentVariable("SHARPCLAW_SOURCE_ROOT");
        if (!string.IsNullOrWhiteSpace(configuredRoot)
            && File.Exists(Path.Combine(configuredRoot, "Directory.Packages.props")))
        {
            return configuredRoot;
        }

        var current = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "Directory.Packages.props")))
                return current.FullName;

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate SharpClaw repository root.");
    }

    [GeneratedRegex(@"^\s{4}name:\s*(?<name>.+?)\s*$", RegexOptions.None, 1000)]
    private static partial Regex JobNameRegex();

    [GeneratedRegex(@"^\s*-\s*domain:\s*(?<domain>.+?)\s*$", RegexOptions.None, 1000)]
    private static partial Regex DomainRegex();
    [GeneratedRegex(@"PackageIds = @\((?<ids>.*?)\)", RegexOptions.Singleline, 1000)]
    private static partial Regex PackageMappingRegex();

    [GeneratedRegex("\"(?<id>SharpClaw\\.[^\"]+)\"", RegexOptions.None, 1000)]
    private static partial Regex PackageIdentityRegex();

    [GeneratedRegex(@"ProcessStartInfo\(\s*""(pwsh|powershell)(\.exe)?""", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex PowerShellStartRegex();
}
