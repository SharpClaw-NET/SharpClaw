using FluentAssertions;
using SharpClaw.Contracts.DTOs.DefaultResources;
using SharpClaw.Contracts.Entities.Core.Context;
using SharpClaw.Contracts.Enums;
using SharpClaw.Core.Permissions;
using SharpClaw.Core.Resources;

namespace SharpClaw.Tests.Core;

[TestFixture]
public sealed class DefaultResourceEngineTests
{
    private readonly DefaultResourceEngine _engine = new();

    [Test]
    public void Merge_WhenChannelAndContextContainSameKey_ChannelOverridesContext()
    {
        var channelId = Guid.NewGuid();
        var contextResource = Guid.NewGuid();
        var channelResource = Guid.NewGuid();

        var response = DefaultResourceEngine.Merge(
            channelId,
            Set(("task", channelResource)),
            Set(("task", contextResource), ("note", Guid.NewGuid())));

        response.Id.Should().Be(channelId);
        response.Entries["task"].Should().Be(channelResource);
        response.Entries.Should().ContainKey("note");
    }

    [Test]
    public void Merge_WhenChannelSetExistsButHasNoEntries_PreservesChannelSetId()
    {
        var channel = Set();

        var response = DefaultResourceEngine.Merge(
            primaryId: Guid.NewGuid(),
            channel,
            context: null);

        response.Id.Should().Be(channel.Id);
        response.Entries.Should().BeEmpty();
    }

    [Test]
    public void ResolveDefaultResource_WhenChannelHasMatchingKey_UsesChannelBeforeContextAndPermissions()
    {
        var channelResource = Guid.NewGuid();
        var contextResource = Guid.NewGuid();
        var roleResource = Guid.NewGuid();

        var result = _engine.ResolveDefaultResource(
            new DefaultResourceResolutionRequest(
                DefaultResourceKey: "task",
                ResourceType: "AoTask",
                ChannelDefaults: Set(("task", channelResource)),
                ContextDefaults: Set(("task", contextResource)),
                OrderedPermissionSets:
                [
                    PermissionSet(
                        new ResourcePermissionGrant(
                            "AoTask",
                            roleResource,
                            PermissionClearance.Independent,
                            IsDefault: true))
                ]));

        result.Should().Be(channelResource);
    }

    [Test]
    public void ResolveDefaultResource_WhenNoSetMatches_UsesFirstPermissionSetDefault()
    {
        var channelPermissionResource = Guid.NewGuid();
        var contextPermissionResource = Guid.NewGuid();

        var result = _engine.ResolveDefaultResource(
            new DefaultResourceResolutionRequest(
                DefaultResourceKey: "task",
                ResourceType: "AoTask",
                ChannelDefaults: Set(("other", Guid.NewGuid())),
                ContextDefaults: null,
                OrderedPermissionSets:
                [
                    PermissionSet(
                        new ResourcePermissionGrant(
                            "AoTask",
                            channelPermissionResource,
                            PermissionClearance.Independent,
                            IsDefault: true)),
                    PermissionSet(
                        new ResourcePermissionGrant(
                            "AoTask",
                            contextPermissionResource,
                            PermissionClearance.Independent,
                            IsDefault: true))
                ]));

        result.Should().Be(channelPermissionResource);
    }

    [Test]
    public void ResolveDefaultResource_WhenDelegateHasNoResourceType_ReturnsNullAfterSetMiss()
    {
        var result = _engine.ResolveDefaultResource(
            new DefaultResourceResolutionRequest(
                DefaultResourceKey: "missing",
                ResourceType: null,
                ChannelDefaults: Set(("other", Guid.NewGuid())),
                ContextDefaults: null,
                OrderedPermissionSets:
                [
                    PermissionSet(
                        new ResourcePermissionGrant(
                            "AoTask",
                            Guid.NewGuid(),
                            PermissionClearance.Independent,
                            IsDefault: true))
                ]));

        result.Should().BeNull();
    }

    [Test]
    public void NormalizeKey_LowercasesForStorage()
    {
        DefaultResourceEngine.NormalizeKey("AoTask").Should().Be("aotask");
    }

    [Test]
    public void Apply_WhenRequestUpdatesAddsAndClearsEntries_MutatesSet()
    {
        var removed = new List<DefaultResourceEntryDB>();
        var setId = Guid.NewGuid();
        var oldTask = Guid.NewGuid();
        var newTask = Guid.NewGuid();
        var note = Guid.NewGuid();
        var set = EntitySet(
            setId,
            ("Task", oldTask),
            ("old", Guid.NewGuid()));

        DefaultResourceEngine.Apply(
            set,
            new SetDefaultResourcesRequest(
                new Dictionary<string, Guid?>
                {
                    ["TASK"] = newTask,
                    ["Note"] = note,
                    ["old"] = null
                }),
            removed.Add);

        set.Entries.Should().HaveCount(2);
        set.Entries.Should().ContainSingle(e =>
            e.ResourceKey == "Task" && e.ResourceId == newTask);
        set.Entries.Should().ContainSingle(e =>
            e.ResourceKey == "note"
            && e.ResourceId == note
            && e.DefaultResourceSetId == setId);
        removed.Should().ContainSingle(e => e.ResourceKey == "old");
    }

    [Test]
    public void ApplyKey_WhenClearingMissingKey_DoesNotCallRemove()
    {
        var removeCalls = 0;
        var set = EntitySet(Guid.NewGuid(), ("task", Guid.NewGuid()));

        DefaultResourceEngine.ApplyKey(
            set,
            "missing",
            null,
            _ => removeCalls++);

        set.Entries.Should().ContainSingle();
        removeCalls.Should().Be(0);
    }

    private static DefaultResourceSetSnapshot Set(
        params (string Key, Guid ResourceId)[] entries) =>
        new(
            Guid.NewGuid(),
            entries.ToDictionary(
                entry => entry.Key,
                entry => entry.ResourceId,
                StringComparer.OrdinalIgnoreCase));

    private static PermissionSetSnapshot PermissionSet(
        params ResourcePermissionGrant[] resources) =>
        new(
            [],
            resources,
            new HashSet<Guid>(),
            new HashSet<Guid>());

    private static DefaultResourceSetDB EntitySet(
        Guid id,
        params (string Key, Guid ResourceId)[] entries) =>
        new()
        {
            Id = id,
            Entries = entries
                .Select(entry => new DefaultResourceEntryDB
                {
                    DefaultResourceSetId = id,
                    ResourceKey = entry.Key,
                    ResourceId = entry.ResourceId
                })
                .ToList()
        };
}
