using FluentAssertions;
using SharpClaw.Contracts.DTOs.DefaultResources;
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
            Set(("document", channelResource)),
            Set(("document", contextResource), ("note", Guid.NewGuid())));

        response.Id.Should().Be(channelId);
        response.Entries["document"].Should().Be(channelResource);
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
                DefaultResourceKey: "document",
                ResourceType: "Document",
                ChannelDefaults: Set(("document", channelResource)),
                ContextDefaults: Set(("document", contextResource)),
                OrderedPermissionSets:
                [
                    PermissionSet(
                        new ResourcePermissionGrant(
                            "Document",
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
                DefaultResourceKey: "document",
                ResourceType: "Document",
                ChannelDefaults: Set(("other", Guid.NewGuid())),
                ContextDefaults: null,
                OrderedPermissionSets:
                [
                    PermissionSet(
                        new ResourcePermissionGrant(
                            "Document",
                            channelPermissionResource,
                            PermissionClearance.Independent,
                            IsDefault: true)),
                    PermissionSet(
                        new ResourcePermissionGrant(
                            "Document",
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
                            "Document",
                            Guid.NewGuid(),
                            PermissionClearance.Independent,
                            IsDefault: true))
                ]));

        result.Should().BeNull();
    }

    [Test]
    public void NormalizeKey_LowercasesForStorage()
    {
        DefaultResourceEngine.NormalizeKey("Document").Should().Be("document");
    }

    [Test]
    public void Apply_WhenRequestUpdatesAddsAndClearsEntries_MutatesSet()
    {
        var removed = new List<DefaultResourceEntryState>();
        var setId = Guid.NewGuid();
        var oldResource = Guid.NewGuid();
        var newResource = Guid.NewGuid();
        var note = Guid.NewGuid();
        var set = EntitySet(
            setId,
            ("Document", oldResource),
            ("old", Guid.NewGuid()));

        DefaultResourceEngine.Apply(
            set,
            new SetDefaultResourcesRequest(
                new Dictionary<string, Guid?>
                {
                    ["DOCUMENT"] = newResource,
                    ["Note"] = note,
                    ["old"] = null
                }),
            removed.Add);

        set.Entries.Should().HaveCount(2);
        set.Entries.Should().ContainSingle(e =>
            e.ResourceKey == "Document" && e.ResourceId == newResource);
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
        var set = EntitySet(Guid.NewGuid(), ("document", Guid.NewGuid()));

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

    private static DefaultResourceSetState EntitySet(
        Guid id,
        params (string Key, Guid ResourceId)[] entries) =>
        new()
        {
            Id = id,
            Entries = entries
                .Select(entry => new DefaultResourceEntryState
                {
                    DefaultResourceSetId = id,
                    ResourceKey = entry.Key,
                    ResourceId = entry.ResourceId
                })
                .ToList()
        };
}
