using System.Reflection;

namespace SharpClaw.Tests.ClientUno;

[TestFixture]
public sealed class BaseClientFeatureOwnershipTests
{
    [Test]
    public void Base_client_does_not_define_permission_metadata_controls_or_endpoint_reader()
    {
        var sourceRoot = FindSourceRoot();
        var paths = new[]
        {
            Path.Combine(sourceRoot, "SharpClaw.Client.Uno", "Helpers", "TerminalUI.cs"),
            Path.Combine(sourceRoot, "SharpClaw.Client.Uno", "Presentation", "SettingsPage.xaml.cs"),
        };

        var source = string.Join("\n", paths.Select(File.ReadAllText));

        source.Should().NotContain("ClearanceOptions");
        source.Should().NotContain("MakeClearanceCombo");
        source.Should().NotContain("permissions-metadata");
        source.Should().NotContain("ModulePermissionMetadata");
    }

    [Test]
    public void Base_settings_do_not_publish_user_administration_surface()
    {
        var sourceRoot = FindSourceRoot();
        var settings = File.ReadAllText(Path.Combine(
            sourceRoot,
            "SharpClaw.Client.Uno",
            "Presentation",
            "SettingsPage.xaml.cs"));

        settings.Should().NotContain("LoadUsersAsync");
        settings.Should().NotContain("UserListEntry");
        settings.Should().NotContain("/users");
        settings.Should().NotContain("isUserAdmin");
        settings.Should().Contain("AddTabButton(\"Danger Zone\"");
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
                var candidate = Path.Combine(
                    directory.FullName,
                    "SharpClaw.Client.Uno",
                    "Helpers",
                    "TerminalUI.cs");
                if (File.Exists(candidate))
                    return directory.FullName;

                directory = directory.Parent;
            }
        }

        throw new DirectoryNotFoundException("Could not find the SharpClaw source root.");
    }
}
