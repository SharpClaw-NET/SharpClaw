using System.Reflection;

namespace SharpClaw.Tests.Architecture;

[TestFixture]
public sealed class ReadmeBoundaryTests
{
    [Test]
    public void Readme_describes_the_kernel_registration_and_storage_boundaries()
    {
        var root = FindSourceRoot();
        var readme = File.ReadAllText(Path.Combine(root, "README.md"));

        File.Exists(Path.Combine(root, ".github", "README.md"))
            .Should()
            .BeFalse("the root README must be the repository's single canonical README");

        readme.Should().Contain("## Architecture At A Glance");
        readme.Should().Contain("## Bring Your Own Features");
        readme.Should().Contain("| Area | Bring your own | What it enables |");
        readme.Should().Contain("public neutral contracts");
        readme.Should().Contain("Authorization policy");
        readme.Should().Contain("Authorization consumers");
        readme.Should().Contain("Authorization restrictions");
        readme.Should().Contain("Permission systems");
        readme.Should().Contain("Agents and knowledge");
        readme.Should().Contain("one configured persistence provider");
        readme.Should().Contain("JSONColdStore");
        readme.Should().Contain("PostgreSQL");
        readme.Should().Contain("SQL Server");
        readme.Should().Contain("SQLite");
        readme.Should().Contain("https://github.com/SharpClaw-NET/SharpClaw");
        readme.Should().NotContain("## What You Get By Default");
        readme.Should().NotContain("## Modules And Capabilities");
        readme.Should().NotContain("| Module type | Capability when enabled |");
        readme.Should().NotContain("Development Status");
        readme.Should().NotContain("We're Hiring");
        readme.Should().NotContain("Disclaimer");
        readme.Should().NotContain("github.com/mkn8rn/SharpClaw");
    }

    [Test]
    public void Module_authoring_guides_use_the_current_public_surface()
    {
        var root = FindSourceRoot();
        var guide = File.ReadAllText(Path.Combine(
            root,
            "docs",
            "guides",
            "Module-Creation-Guide.md"));
        var reference = File.ReadAllText(Path.Combine(
            root,
            "docs",
            "guides",
            "Module-Creation-skill.md"));
        var text = guide + Environment.NewLine + reference;

        foreach (var required in new[]
                 {
                     "ISharpClawModule",
                     "PackageManifestLoader",
                     "AddTool<THandler>",
                     "AddAuthorizationPolicy",
                     "AddAuthorizationRestriction",
                     "SharpClawModuleTestBuilder",
                     "ActionEntry",
                     "IHostActionEntry",
                     "hostMode",
                 })
        {
            text.Should().Contain(required, required);
        }

        foreach (var retired in new[]
                 {
                     "IKernelRegistrationSource",
                     "IApplicationRegistrationSource",
                     "RegistrationToolDefinition",
                     "RegistrationInlineToolDefinition",
                     "RegistrationToolPermission",
                     "GetToolDefinitions",
                     "ExecuteToolAsync",
                     "ExecuteInlineToolAsync",
                     "SeedDataAsync",
                     "InitializeAsync",
                     "ShutdownAsync",
                     "ExportedContracts",
                     "RequiredContracts",
                     "AgentJobContext",
                     "ModuleToolDefinition",
                     "ModuleCliCommand",
                     "SharpClaw.Runtime.BLL",
                     "SharpClaw.Runtime.INF",
                 })
        {
            text.Should().NotContain(retired, retired);
        }
    }

    private static string FindSourceRoot()
    {
        var starts = new[]
        {
            Environment.GetEnvironmentVariable("SHARPCLAW_SOURCE_ROOT"),
            Directory.GetCurrentDirectory(),
            Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location),
        };

        foreach (var start in starts.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            var directory = new DirectoryInfo(start!);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "SharpClaw.slnx")))
                    return directory.FullName;

                directory = directory.Parent;
            }
        }

        throw new DirectoryNotFoundException("Could not find the SharpClaw source root.");
    }
}
