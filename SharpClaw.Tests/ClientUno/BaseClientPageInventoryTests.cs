using System.Xml.Linq;

namespace SharpClaw.Tests.ClientUno;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812",
    Justification = "NUnit discovers and constructs this internal fixture through reflection; its tests are executed by the maintained test suite.")]
[TestFixture]
internal sealed class BaseClientPageInventoryTests
{
    [Test]
    public void OnlyThreeDefaultPagesRetainTheExistingBlackTerminalTheme()
    {
        var root = FindSourceRoot();
        var pages = new[] { "BootPage", "SettingsPage", "MainPage" };
        foreach (var page in pages)
        {
            var xml = XDocument.Load(Path.Combine(root, "SharpClaw.Client.Uno", "Presentation", page + ".xaml"));
            xml.Root!.Name.LocalName.Should().Be("Page");
            xml.Root.Attribute("Background")!.Value.Should().Be("Black");
            xml.Descendants().Should().Contain(element => (string?)element.Attribute("Foreground") == "#00FF00");
        }
        var boot = File.ReadAllText(Path.Combine(root, "SharpClaw.Client.Uno", "Presentation", "BootPage.xaml"));
        boot.Should().Contain("Content=\"Install modules\"");
        boot.Should().Contain("Content=\"Settings\"");
        boot.Should().Contain("Content=\"Open stateless chat\"");
        File.Exists(Path.Combine(root, "SharpClaw.Client.Uno", "Presentation", "LegalNoticesPage.xaml")).Should().BeFalse();
        File.Exists(Path.Combine(root, "SharpClaw.Client.Uno", "Presentation", "UserGuidePage.xaml")).Should().BeFalse();
        File.ReadAllText(Path.Combine(root, "SharpClaw.Client.Uno", "Presentation", "SettingsPage.ModuleSettings.cs"))
            .Should().Contain("Open licences and written source offers");
    }

    private static string FindSourceRoot()
    {
        var directory = new DirectoryInfo(Environment.GetEnvironmentVariable("SHARPCLAW_SOURCE_ROOT") ?? Directory.GetCurrentDirectory());
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "SharpClaw.Client.Uno", "App.xaml.cs"))) return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("SharpClaw source root not found.");
    }
}
