using FluentAssertions;

namespace SharpClaw.Tests.Modules;

[TestFixture]
public sealed class JavaScriptModuleSdkTests
{
    [Test]
    public async Task JavaScriptSdk_ExposesCurrentForeignModuleProtocolShape()
    {
        var sdkPath = Path.Combine(
            FindRepoRoot(),
            "sdk",
            "javascript",
            "sharpclaw-module-host",
            "src",
            "index.mjs");

        var source = await File.ReadAllTextAsync(sdkPath);

        source.Should().Contain("X-SharpClaw-Control-Token");
        source.Should().Contain("SHARPCLAW_MODULE_DIR");
        source.Should().Contain("SHARPCLAW_MODULE_DATA_DIR");
        source.Should().Contain("SHARPCLAW_CONTROL_ADDRESS");
        source.Should().Contain("SHARPCLAW_CONTROL_TOKEN");
        source.Should().Contain("SHARPCLAW_MODULE_ID");
        source.Should().Contain("SHARPCLAW_MODULE_RUNTIME");
        source.Should().Contain("SHARPCLAW_HOST_CAPABILITIES_ADDRESS");
        source.Should().Contain("SHARPCLAW_HOST_CAPABILITIES_TOKEN");
        source.Should().Contain("/.sharpclaw/handshake");
        source.Should().Contain("/.sharpclaw/discovery");
        source.Should().Contain("/.sharpclaw/health");
        source.Should().Contain("/.sharpclaw/initialize");
        source.Should().Contain("/.sharpclaw/shutdown");
        source.Should().Contain("/.sharpclaw/tools/execute");
        source.Should().Contain("/.sharpclaw/tools/stream");
        source.Should().Contain("/.sharpclaw/inline-tools/execute");
        source.Should().Contain("/.sharpclaw/host/config/get");
        source.Should().Contain("/.sharpclaw/host/job/log");
        source.Should().Contain("createHostCapabilitiesClient");
        source.Should().Contain("inlineTools");
        source.Should().Contain("supportsStreaming");
    }

    [Test]
    public async Task NodeTemplates_DeclareModuleHostDependencyAndEntrypoint()
    {
        var repoRoot = FindRepoRoot();
        var packageTemplate = await File.ReadAllTextAsync(Path.Combine(
            repoRoot,
            "DefaultModules",
            "ModuleDev",
            "Templates",
            "NodePackage.json.template"));
        var manifestTemplate = await File.ReadAllTextAsync(Path.Combine(
            repoRoot,
            "DefaultModules",
            "ModuleDev",
            "Templates",
            "NodeManifest.json.template"));

        packageTemplate.Should().Contain("\"@sharpclaw/module-host\": \"0.1.0-beta\"");
        manifestTemplate.Should().Contain("\"runtime\": \"node\"");
        manifestTemplate.Should().Contain("\"entrypoint\": \"module.mjs\"");
    }

    private static string FindRepoRoot()
    {
        foreach (var startingPoint in new[]
                 {
                     TestContext.CurrentContext.TestDirectory,
                     AppContext.BaseDirectory,
                     Directory.GetCurrentDirectory()
                 })
        {
            var current = startingPoint;
            while (!string.IsNullOrWhiteSpace(current))
            {
                if (File.Exists(Path.Combine(current, "Directory.Build.props"))
                    && Directory.Exists(Path.Combine(current, "SharpClaw.Tests")))
                {
                    return current;
                }

                current = Directory.GetParent(current)?.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not locate SharpClaw repository root.");
    }
}
