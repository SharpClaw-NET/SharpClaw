using System.Reflection;
using System.Text.RegularExpressions;

namespace SharpClaw.Tests.Publishing;

[TestFixture]
public sealed class PublishScriptDeploymentTests
{
    [Test]
    public void PublishScriptExposesOnlyApplicationServerAndRuntimeDeploymentTypes()
    {
        var script = ReadPublishScript();

        script.Should().Contain(
            "$deploymentTypes = @(\"Application\", \"Server\", \"Runtime\")",
            "the publish selector must expose the requested public deployment type names exactly");
        script.Should().NotContain("return \"Uno\"");
        script.Should().NotContain("Desktop");
        script.Should().NotContain("Publish-Uno");
        script.Should().NotContain("Publish-Core");
        script.Should().NotContain("Publish-MSIX");
        script.Should().NotContain("Publish-WASM");
        script.Should().Contain("throw \"Unknown deployment type '$type'. Valid values:");
    }

    [Test]
    public void PublishScriptMapsDeploymentTypesToTheExpectedComponents()
    {
        var script = ReadPublishScript();

        var application = ExtractFunction(script, "Publish-Application");
        application.Should().Contain("$clientProject");
        application.Should().Contain("-p:BundleBackend=true");
        application.Should().Contain("SharpClaw.Runtime.Host");
        application.Should().Contain("SharpClaw.Gateway");

        var server = ExtractFunction(script, "Publish-Server");
        server.Should().Contain("$runtimeProject");
        server.Should().Contain("$gatewayProject");
        server.Should().NotContain("$clientProject");

        var runtime = ExtractFunction(script, "Publish-Runtime");
        runtime.Should().Contain("$runtimeProject");
        runtime.Should().NotContain("$gatewayProject");
        runtime.Should().NotContain("$clientProject");
    }

    [Test]
    public void PublishScriptResolvesRepositoryRootFromScriptsDirectory()
    {
        var script = ReadPublishScript();

        script.Should().Contain(
            "$repoRoot = Split-Path -Parent $PSScriptRoot",
            "the publish script lives under scripts/ and must still resolve project paths from the repository root");
        script.Should().Contain(
            "[string]$OutputDir = (Join-Path (Split-Path -Parent $PSScriptRoot) \"publish\")",
            "default publish output should remain at the repository publish/ directory");
    }

    [TestCase("Uno")]
    [TestCase("Core")]
    [TestCase("MSIX")]
    [TestCase("WASM")]
    public void PublishScriptRejectsOldDeploymentTypeNames(string oldName)
    {
        var script = ReadPublishScript();
        var deploymentTypePattern = @"\$deploymentTypes\s*=\s*@\([^)]*""" + Regex.Escape(oldName) + @"""";

        Regex.IsMatch(script, deploymentTypePattern).Should().BeFalse(
            $"{oldName} must not remain as an accepted top-level deployment type");
        script.Should().NotContain(
            $"Publish-{oldName}",
            $"{oldName} must not remain as a live publish branch");
    }

    private static string ExtractFunction(string script, string functionName)
    {
        var pattern = $@"function\s+{Regex.Escape(functionName)}\s*\{{(?<body>.*?)(?=^function\s|\z)";
        var match = Regex.Match(
            script,
            pattern,
            RegexOptions.Singleline | RegexOptions.Multiline | RegexOptions.CultureInvariant);

        match.Success.Should().BeTrue($"scripts/publish.ps1 must define {functionName}");
        return match.Value;
    }

    private static string ReadPublishScript()
        => File.ReadAllText(Path.Combine(FindSolutionRoot(), "scripts", "publish.ps1"));

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
