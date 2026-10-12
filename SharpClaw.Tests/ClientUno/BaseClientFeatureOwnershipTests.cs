using System.Reflection;
using System.Text.Json;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using SharpClaw.Presentation;

namespace SharpClaw.Tests.ClientUno;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812",
    Justification = "NUnit discovers and constructs this internal fixture through reflection; its tests are executed by the maintained test suite.")]
[TestFixture]
internal sealed class BaseClientFeatureOwnershipTests
{
    [Test]
    public void InspectionControlsRestoreAdmissionWhenPreviousScratchRetirementFails()
    {
        var source = File.ReadAllText(Path.Combine(FindSourceRoot(), "SharpClaw.Client.Uno", "Presentation", "BootPage.Modules.cs"));
        var root = CSharpSyntaxTree.ParseText(source, cancellationToken: TestContext.CurrentContext.CancellationToken)
            .GetRoot(TestContext.CurrentContext.CancellationToken);
        var method = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Where(item => string.Equals(item.Identifier.ValueText, "OnInspectModuleClick", StringComparison.Ordinal)).Should().ContainSingle().Which;
        var retirement = method.DescendantNodes().OfType<ExpressionStatementSyntax>()
            .Where(item => string.Equals(item.ToString(), "_moduleCandidate?.Dispose();", StringComparison.Ordinal)).Should().ContainSingle().Which;
        var cleanup = retirement.Ancestors().OfType<TryStatementSyntax>().FirstOrDefault(item => item.Finally is not null);
        cleanup.Should().NotBeNull("ordinary candidate retirement failures must restore cached page controls");
        cleanup!.Finally!.ToString().Should().Contain("SetModuleBusy(false)");
        var guarded = cleanup.Block.ToString();
        AssertSourceOrder(guarded, "_moduleCandidate?.Dispose()", "_moduleCandidate = null");
    }

    [Test]
    public void AppAttachesCloseBeforeNavigationAndActivationAndUsesTrackedOwnersForCleanup()
    {
        var source = File.ReadAllText(Path.Combine(FindSourceRoot(), "SharpClaw.Client.Uno", "App.xaml.cs"));
        var launch = MethodBody(source, "LaunchAsync");
        AssertSourceOrder(launch, "_startup.SetMutationOwner", "CreateApplicationBuilder");
        AssertSourceOrder(launch, "AttachWindow(builder.Window)", "BeginOwnedNavigationAsync");
        AssertSourceOrder(launch, "AttachWindow(builder.Window)", "window.Activate()");
        MethodBody(source, "AttachWindow").Should().Contain("MainWindow.Closed += OnWindowClosed");
        MethodBody(source, "OnLaunched").Should().Contain("if (_startup.IsClosed) return;");
        launch.Should().Contain("navigation.ConfigureAwait(false)");
        var acquisition = MethodBody(source, "BeginOwnedNavigationAsync");
        AssertSourceOrder(acquisition, "_startup.AcquireOwnedAsync", "builder.Build()");
        AssertSourceOrder(acquisition, "_startup.CreateOwned", "InitializeHostedNavigationAsync");
        acquisition.Should().Contain("static owner => owner.DisposeAsync()");
        var cleanup = MethodBody(source, "CloseResourcesAsync");
        cleanup.Should().Contain("_startup.GetOwned<SharpClawApiClient>");
        cleanup.Should().Contain("_startup.GetOwned<ModulePackageStore>()?.DisposeAsync()");
        var lifetime = File.ReadAllText(Path.Combine(FindSourceRoot(), "SharpClaw.Client.Uno", "Services", "ClientStartupLifetime.cs"));
        AssertSourceOrder(MethodBody(lifetime, "CloseAsync"), "CloseAdmission()", "_shutdown.ShutdownAsync");
        cleanup.Should().NotContain("GetService<");
        cleanup.Should().NotContain("GetRequiredService<");
        MethodBody(source, "OnWindowClosed").Should().Contain("await ShutdownAsync().ConfigureAwait(false)");
        var program = File.ReadAllText(Path.Combine(FindSourceRoot(), "SharpClaw.Client.Uno", "Platforms", "Desktop", "Program.cs"));
        program.Should().Contain("using var instance = InstalledClientInstanceGuard.Acquire()");
        program.Should().Contain("await desktop.RunHostAsync");
        program.Should().Contain("new App(desktop)");
        cleanup.Should().Contain("_startup.GetOwned<ClientHostOwner>()?.DisposeAsync()");
        MethodBody(source, "InitializeHostedNavigationAsync").Should().NotContain(".Activate()");
        MethodBody(source, "NavigateInitialRouteAsync").Should().Contain("_startup.TryPublish(() => Services = services)");
        program.Should().Contain("application?.ShutdownAsync()");
    }

    private static string MethodBody(string source, string name)
    {
        var root = CSharpSyntaxTree.ParseText(source, cancellationToken: TestContext.CurrentContext.CancellationToken)
            .GetRoot(TestContext.CurrentContext.CancellationToken);
        var method = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Where(method => string.Equals(method.Identifier.ValueText, name, StringComparison.Ordinal)).Should().ContainSingle().Which;
        return method.Body?.ToFullString() ?? throw new AssertionException("The authored App method must have a body.");
    }

    private static void AssertSourceOrder(string body, string before, string after)
    {
        var first = body.IndexOf(before, StringComparison.Ordinal);
        first.Should().BeGreaterThanOrEqualTo(0);
        body.IndexOf(after, StringComparison.Ordinal).Should().BeGreaterThan(first);
    }

    [Test]
    public void Base_client_does_not_define_permission_metadata_controls_or_endpoint_reader()
    {
        var sourceRoot = FindSourceRoot();
        var paths = new[]
        {
            Path.Combine(sourceRoot, "SharpClaw.Client.Uno", "Helpers", "TerminalUI.cs"),
            Path.Combine(sourceRoot, "SharpClaw.Client.Uno", "Presentation", "SettingsPage.xaml.cs"),
        };

        var source = string.Join('\n', paths.Select(File.ReadAllText));

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
        settings.Should().Contain("AddTabButton(\"Modules\"");
        settings.Should().NotContain("AddTabButton(\"Gateway\"");
        settings.Should().NotContain("/providers");
        settings.Should().NotContain("\"/models\"");
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
        app.Should().Contain("new (\"RemoteConnection\"");
        app.Should().NotContain("new (\"LegalNotices\"");
        app.Should().NotContain("new (\"UserGuide\"");
        app.Should().Contain("new (\"Boot\", View: views.FindByView<BootPage>(), IsDefault:true)");
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
    public void Successful_boot_stays_home_and_chat_requires_an_explicit_choice()
    {
        var sourceRoot = FindSourceRoot();
        var boot = File.ReadAllText(Path.Combine(
            sourceRoot,
            "SharpClaw.Client.Uno",
            "Presentation",
            "BootPage.xaml.cs"));

        boot.Should().NotContain("NavigateRouteAsync(this, \"Main\"");
        var menu = File.ReadAllText(Path.Combine(sourceRoot, "SharpClaw.Client.Uno", "Presentation", "BootPage.Modules.cs"));
        menu.Should().Contain("OnStatelessChatClick");
        menu.Should().Contain("NavigateRouteAsync(this, \"Main\"");
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
