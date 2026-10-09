using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace SharpClaw.Tests.Publishing;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812",
    Justification = "NUnit discovers and constructs this internal fixture through reflection; its tests are executed by the maintained test suite.")]
[TestFixture]
internal sealed class PublishScriptDeploymentTests
{
    [Test]
    public void PublishScriptExposesOnlyApplicationServerAndRuntimeDeploymentTypes()
    {
        var script = ReadPublishScript();

        script.Should().Contain(
            "$deploymentTypes = @(\"Application\", \"Server\", \"Runtime\")",
            "the publish selector must expose the requested public deployment type names exactly");
        script.Should().NotContain("return \"Uno\"");
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
        application.Should().Contain("Publish-ServerComponents");
        application.Should().Contain("SharpClaw.Runtime.Host");
        application.Should().Contain("SharpClaw.Gateway");

        var server = ExtractFunction(script, "Publish-Server");
        server.Should().Contain("Publish-ServerComponents");
        var components = ExtractFunction(script, "Publish-ServerComponents");
        components.Should().Contain("$runtimeProject");
        components.Should().Contain("$gatewayProject");
        server.Should().NotContain("$clientProject");

        var runtime = ExtractFunction(script, "Publish-Runtime");
        runtime.Should().Contain("$runtimeProject");
        runtime.Should().NotContain("$gatewayProject");
        runtime.Should().NotContain("$clientProject");
    }

    [Test]
    public void PublishScriptRequiresVerifiedPayloadAndReturnsSuccessWithoutStrictModeNullCount()
    {
        var script = ReadPublishScript();
        script.Should().Contain("Assert-PublishBom $BomRoot $BomManifestSha256");
        script.Should().Contain("-p:SharpClawContributionPayloadRoot=$($bom.BundleRoot)");
        script.Should().Contain("if (@($results | Where-Object { -not $_.Ok }).Count -gt 0)");
        script.Should().Contain("CreateFromDirectory", "ZIPs must retain dotfiles such as configuration templates");
        script.Should().Contain("Assert-FileInventory", "omitted or extra contribution files must fail publishing");
    }

    [Test]
    public void RuntimePublishesItsPrivateSidecarReferenceIntoDependencyMetadata()
    {
        var project = XDocument.Load(Path.Combine(FindSolutionRoot(), "SharpClaw.Runtime", "Host", "SharpClaw.Runtime.Host.csproj"));
        var reference = project.Descendants("PackageReference")
            .Single(node => node.Attribute("Include")?.Value == "SharpClaw.SidecarHost.OutOfProcess");
        reference.Attribute("PrivateAssets")?.Value.Should().Be("all");
        reference.Attribute("Publish")?.Value.Should().Be("true",
            "the SDK otherwise removes a private reference from the self-contained dependency manifest");
        foreach (var suffix in new[] { "deps.json", "runtimeconfig.json" })
        {
            project.Descendants("Copy").Should().Contain(node =>
                node.Attribute("SourceFiles")!.Value == $"$(PublishDir)SharpClaw.Runtime.Host.{suffix}"
                && node.Attribute("DestinationFiles")!.Value == $"$(PublishDir)SharpClaw.SidecarHost.OutOfProcess.{suffix}"
                && node.Attribute("Condition")!.Value == "'$(SelfContained)' == 'true'",
                "both entry points must have the SDK-generated self-contained runtime closure");
        }
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

    [TestCase("Desktop")]
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
        var configuredRoot = Environment.GetEnvironmentVariable("SHARPCLAW_SOURCE_ROOT");
        if (!string.IsNullOrWhiteSpace(configuredRoot)
            && File.Exists(Path.Combine(configuredRoot, "SharpClaw.slnx")))
        {
            return configuredRoot;
        }

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
