using System.Text.Json;
using SharpClaw.Contracts.Providers;

namespace SharpClaw.Tests.Core;

[TestFixture]
public sealed class ChatToolSelectionEngineTests
{
    private readonly ChatToolSelectionEngine _engine = new();

    [Test]
    public void BuildAwarenessFingerprint_WhenAwarenessIsEmpty_ReturnsAll()
    {
        _engine.BuildAwarenessFingerprint(null).Should().Be("all");
        _engine.BuildAwarenessFingerprint(new Dictionary<string, bool>())
            .Should().Be("all");
    }

    [Test]
    public void BuildAwarenessFingerprint_IsStableAcrossDictionaryOrder()
    {
        var first = _engine.BuildAwarenessFingerprint(
            new Dictionary<string, bool>
            {
                ["beta"] = false,
                ["alpha"] = true
            });
        var second = _engine.BuildAwarenessFingerprint(
            new Dictionary<string, bool>
            {
                ["alpha"] = true,
                ["beta"] = false
            });

        first.Should().Be(second);
        first.Should().NotBe("all");
    }

    [Test]
    public void ApplyAwareness_RemovesOnlyExplicitlyDisabledTools()
    {
        var tools = new[] { Tool("alpha"), Tool("beta"), Tool("gamma") };

        var filtered = _engine.ApplyAwareness(
            tools,
            new Dictionary<string, bool>
            {
                ["alpha"] = true,
                ["beta"] = false
            });

        filtered.Select(tool => tool.Name)
            .Should().Equal("alpha", "gamma");
    }

    [Test]
    public void EstimateToolDefinitions_IncludesNamesDescriptionsAndSchemas()
    {
        var tool = Tool("alpha", "does alpha", "{\"type\":\"object\"}");

        var estimate = _engine.EstimateToolDefinitions([tool]);

        estimate.Should().BeGreaterThan(64);
    }

    private static ChatToolDefinition Tool(
        string name,
        string description = "desc",
        string schema = "{}")
    {
        using var doc = JsonDocument.Parse(schema);
        return new ChatToolDefinition(
            name,
            description,
            doc.RootElement.Clone());
    }
}
