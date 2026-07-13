using SharpClaw.Contracts.DTOs.Tools;
using SharpClaw.Contracts.Entities.Core;
using SharpClaw.Core.Tools;

namespace SharpClaw.Tests.Core;

[TestFixture]
public sealed class ToolAwarenessSetEngineTests
{
    private readonly ToolAwarenessSetEngine _engine = new();

    [Test]
    public void Create_WhenToolsAreNull_UsesEmptyDictionary()
    {
        var entity = _engine.Create(new CreateToolAwarenessSetRequest("Focused"));

        entity.Name.Should().Be("Focused");
        entity.Tools.Should().BeEmpty();
    }

    [Test]
    public void ApplyUpdate_WhenToolsAreProvided_ReplacesDictionary()
    {
        var entity = new ToolAwarenessSetDB
        {
            Name = "Before",
            Tools = new Dictionary<string, bool>
            {
                ["old"] = true
            }
        };

        _engine.ApplyUpdate(
            entity,
            new UpdateToolAwarenessSetRequest(
                Name: "After",
                Tools: new Dictionary<string, bool>
                {
                    ["new"] = false
                }));

        entity.Name.Should().Be("After");
        entity.Tools.Should().ContainSingle()
            .Which.Should().Be(new KeyValuePair<string, bool>("new", false));
    }
}
