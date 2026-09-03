using System.Reflection;

namespace SharpClaw.Tests.ClientUno;

[TestFixture]
public sealed class ClientUnoGuideInventoryTests
{
    [Test]
    public void UserGuide_topics_match_the_default_host_boundary()
    {
        var sourceRoot = FindSourceRoot();
        var pagePath = Path.Combine(
            sourceRoot,
            "SharpClaw.Client.Uno",
            "Presentation",
            "UserGuidePage.xaml.cs");
        var guideDirectory = Path.Combine(sourceRoot, "SharpClaw.Client.Uno", "Assets", "Guide");
        var source = File.ReadAllText(pagePath);
        var topicsStart = source.IndexOf("private static readonly", StringComparison.Ordinal);
        var topicsEnd = source.IndexOf("];", topicsStart, StringComparison.Ordinal);

        topicsStart.Should().BeGreaterThanOrEqualTo(0);
        topicsEnd.Should().BeGreaterThan(topicsStart);

        var topicBlock = source[topicsStart..(topicsEnd + 2)];
        var ids = topicBlock
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => line.StartsWith("(\"", StringComparison.Ordinal))
            .Select(line => line.Split('"')[1])
            .ToArray();

        ids.Should().Equal("welcome", "getting-started", "gateway", "troubleshooting");

        foreach (var id in ids)
            File.Exists(Path.Combine(guideDirectory, id + ".md")).Should().BeTrue();

        new[] { "advanced", "agents-models", "bot-integrations", "channels-threads", "chat-features", "permissions" }
            .Select(id => Path.Combine(guideDirectory, id + ".md"))
            .Should().OnlyContain(path => !File.Exists(path));
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
                var candidate = Path.Combine(directory.FullName, "SharpClaw.Client.Uno", "Presentation", "UserGuidePage.xaml.cs");
                if (File.Exists(candidate))
                    return directory.FullName;

                directory = directory.Parent;
            }
        }

        throw new DirectoryNotFoundException("Could not find the SharpClaw source root.");
    }
}
