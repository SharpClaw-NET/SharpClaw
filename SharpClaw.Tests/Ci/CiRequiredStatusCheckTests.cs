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

        script.Should().Contain("$persistencePackageVersion = \"0.5.0-dev.20260922.1\"");
        script.Should().Contain("8ff0884018e292c987909685be6d5dc019a2524e");
        script.Should().Contain("Restore-Target -Label \"persistence\"");
        script.Should().Contain("-PackageVersionOverride $persistencePackageVersion");
        script.Should().Contain("SharpClaw.Persistence.SQLServer");
        script.Should().Contain("$moduleDevPackageVersion = \"0.5.0-dev.20260921.2\"");
        script.Should().Contain("929429f22d91832237f35f5f1fd569857321508b");
        script.Should().NotContain("SharpClaw.AgentOrchestration");
        script.Should().NotContain("agent-modules");
    }

    [Test]
    public void FrozenBomKeeps_old_sdk_packages_immutable_and_packs_the_new_core_closure()
    {
        var root = ResolveRepoRoot();
        var script = File.ReadAllText(Path.Combine(root, "build", "BuildFrozenBom.ps1"));

        script.Should().Contain("$corePackageVersion = \"0.5.0-dev.20260925.3\"");
        script.Should().Contain("62f081180a8c6465ce84a7a8bd5cc96dce23c546");
        script.Should().Contain("Name = \"module-sdk\"");
        script.Should().Contain("195ba708050c72d6606b9cba86d0a45a46f7b86c");
        script.Should().Contain("Name = \"module-sdk-sidecar\"");
        script.Should().Contain("1d36241a9e404dd7be99385dcff81ba6bd301fff");
        script.Should().Contain("-PackageVersionOverride $moduleTestingPackageVersion");
        script.Should().Contain("-PackageVersionOverride $moduleHostPackageVersion");
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
