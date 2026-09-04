using System.Reflection;
using System.Text.Json;
using SharpClaw.Presentation;

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
        source.Should().NotContain("RegistrationPermissionMetadata");
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
        settings.Should().NotContain("Danger Zone");
        settings.Should().Contain("AddTabButton(\"Runtime\"");
        settings.Should().Contain("AddTabButton(\"Gateway\"");
        settings.Should().NotContain("/providers");
        settings.Should().NotContain("/models");
        settings.Should().NotContain("/modules");
        settings.Should().NotContain("/system/factory-reset");
    }

    [Test]
    public void Base_client_exposes_only_kernel_owned_product_routes()
    {
        var sourceRoot = FindSourceRoot();
        var clientRoot = Path.Combine(sourceRoot, "SharpClaw.Client.Uno");
        var app = File.ReadAllText(Path.Combine(clientRoot, "App.xaml.cs"));

        app.Should().Contain("new (\"Boot\"");
        app.Should().Contain("new (\"Main\"");
        app.Should().Contain("new (\"Settings\"");
        app.Should().Contain("new (\"LegalNotices\"");
        app.Should().Contain("new (\"UserGuide\"");
        app.Should().Contain("services.AddTransient<ClientNavigationService>()");
        app.Should().NotContain("services.AddSingleton<ClientNavigationService>()");
        app.Should().NotContain("new (\"Login\"");
        app.Should().NotContain("new (\"FirstSetup\"");
        app.Should().NotContain("new (\"EnvMenu\"");
        app.Should().NotContain("new (\"EnvEditor\"");

        var removedPaths = new[]
        {
            Path.Combine("Presentation", "LoginPage.xaml"),
            Path.Combine("Presentation", "FirstSetupPage.xaml"),
            Path.Combine("Presentation", "EnvMenuPage.xaml"),
            Path.Combine("Presentation", "EnvEditorPage.xaml"),
            Path.Combine("Services", "AccountStore.cs"),
            Path.Combine("Services", "ClientSessionService.cs"),
            Path.Combine("Services", "ClientSettings.cs"),
            Path.Combine("Services", "FirstSetupMarker.cs"),
            Path.Combine("Services", "CoreEnvGuard.cs"),
            Path.Combine("Presentation", "SettingsPage.Modules.cs"),
        };

        removedPaths.Should().OnlyContain(path => !File.Exists(Path.Combine(clientRoot, path)));
    }

    [Test]
    public void Successful_boot_opens_chat_without_a_setup_or_login_transition()
    {
        var sourceRoot = FindSourceRoot();
        var boot = File.ReadAllText(Path.Combine(
            sourceRoot,
            "SharpClaw.Client.Uno",
            "Presentation",
            "BootPage.xaml.cs"));

        boot.Should().Contain("NavigateRouteAsync(this, \"Main\", Qualifiers.ClearBackStack)");
        boot.Should().NotContain("TryAutoLoginAsync");
        boot.Should().NotContain("FirstSetup");
        boot.Should().NotContain("Login");
    }

    [Test]
    public void Main_chat_surface_does_not_show_kernel_implementation_disclaimers()
    {
        var sourceRoot = FindSourceRoot();
        var main = File.ReadAllText(Path.Combine(
            sourceRoot,
            "SharpClaw.Client.Uno",
            "Presentation",
            "MainPage.xaml"));

        main.Should().NotContain("stateless request");
        main.Should().NotContain("Each message is independent");
    }

    [TestCase(null, "sharpclaw chat ")]
    [TestCase("", "sharpclaw chat ")]
    [TestCase("hello from Uno", "sharpclaw chat hello from Uno")]
    public void Main_chat_command_mirrors_the_message_input(string? message, string expected)
        => MainPage.FormatChatCommand(message).Should().Be(expected);

    [Test]
    public void Base_client_uses_Uno_Sdk_6_7_22()
    {
        var sourceRoot = FindSourceRoot();
        using var document = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(sourceRoot, "global.json")));

        document.RootElement
            .GetProperty("msbuild-sdks")
            .GetProperty("Uno.Sdk")
            .GetString()
            .Should()
            .Be("6.7.22");
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
