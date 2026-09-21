using System.Text.Json;
using System.Text.RegularExpressions;

namespace SharpClaw.Tests.Ci;

[TestFixture]
public sealed partial class CiRequiredStatusCheckTests
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
    public void CorrectnessRestoreUsesTheReleaseDependencyGraph()
    {
        var root = ResolveRepoRoot();
        var workflow = File.ReadAllText(Path.Combine(root, ".github", "workflows", "ci.yml"));

        workflow.Should().Contain(
            "dotnet restore $env:TEST_PROJECT --configfile $env:SHARPCLAW_NUGET_CONFIG -p:Configuration=Release");
    }

    [Test]
    public void FrozenBomUsesPublishedPersistenceAndExcludesArchivedRepositories()
    {
        var root = ResolveRepoRoot();
        var script = File.ReadAllText(Path.Combine(root, "build", "BuildFrozenBom.ps1"));

        script.Should().Contain("$publishedPackageRepositories");
        script.Should().Contain("https://api.nuget.org/v3-flatcontainer/");
        script.Should().Contain("SharpClaw.Persistence.SQLServer");
        script.Should().Contain("$moduleDevPackageVersion = \"0.5.0-dev.20260921.1\"");
        script.Should().Contain("ac7ada878ace59ada90778b541e954ae200566f9");
        script.Should().NotContain("SharpClaw.AgentOrchestration");
        script.Should().NotContain("agent-modules");
    }

    [Test]
    public void CiRunsOnlyOnCanonicalBranches()
    {
        var root = ResolveRepoRoot();
        var workflow = File.ReadAllText(Path.Combine(root, ".github", "workflows", "ci.yml"));

        workflow.Should().NotContain("release/**");
    }

    private static IReadOnlyList<string> ExtractWorkflowContexts(string workflowPath)
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
                if (name.StartsWith("Dependency graph / ", StringComparison.Ordinal))
                    contexts.Add(name);

                currentJobTemplate = name.Contains("${{ matrix.domain }}", StringComparison.Ordinal)
                    ? name
                    : null;
                inMatrixInclude = false;
                continue;
            }

            if (currentJobTemplate is null)
                continue;

            if (line.Trim() == "include:")
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

    private static IReadOnlyList<string> ExtractRequiredContexts(string rulesetPath)
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

    [GeneratedRegex(@"^\s{4}name:\s*(?<name>.+?)\s*$")]
    private static partial Regex JobNameRegex();

    [GeneratedRegex(@"^\s*-\s*domain:\s*(?<domain>.+?)\s*$")]
    private static partial Regex DomainRegex();
}
