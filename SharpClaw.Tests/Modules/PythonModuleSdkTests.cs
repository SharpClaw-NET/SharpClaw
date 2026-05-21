using FluentAssertions;

namespace SharpClaw.Tests.Modules;

[TestFixture]
public sealed class PythonModuleSdkTests
{
    [Test]
    public async Task PythonSdk_ExposesCurrentForeignModuleProtocolShape()
    {
        var sdkPath = Path.Combine(
            FindRepoRoot(),
            "sdk",
            "python",
            "sharpclaw-module-host",
            "src",
            "sharpclaw_module_host",
            "host.py");

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
        source.Should().Contain("HostCapabilitiesClient");
        source.Should().Contain("InlineToolExecutionContext");
        source.Should().Contain("supportsStreaming");
        source.Should().Contain("asgi_app");
    }

    [Test]
    public async Task PythonTemplates_DeclareRuntimeAndEntrypoint()
    {
        var repoRoot = FindRepoRoot();
        var projectTemplate = await File.ReadAllTextAsync(Path.Combine(
            repoRoot,
            "DefaultModules",
            "ModuleDev",
            "Templates",
            "PythonPyproject.toml.template"));
        var manifestTemplate = await File.ReadAllTextAsync(Path.Combine(
            repoRoot,
            "DefaultModules",
            "ModuleDev",
            "Templates",
            "PythonManifest.json.template"));

        projectTemplate.Should().Contain("sharpclaw-module-host==0.1.0b0");
        manifestTemplate.Should().Contain("\"runtime\": \"python\"");
        manifestTemplate.Should().Contain("\"entrypoint\": \"module.py\"");
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
