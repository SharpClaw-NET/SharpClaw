using FluentAssertions;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using SharpClaw.Gateway.Contracts;
using SharpClaw.Gateway.Modules;

namespace SharpClaw.Tests.Gateway;

/// <summary>
/// Locks down the resolution invariants of
/// <see cref="GatewayEndpointGroupCatalog"/>. The catalog is the single
/// source of truth that <c>EndpointGateMiddleware</c> consults to map
/// <c>/api/modules/...</c> request paths back to a <c>{moduleId}/{groupId}</c>
/// identity, so its longest-prefix-with-segment-boundary contract is a
/// security property: a regression that lets <c>/api/modules/foo/bars</c>
/// match a <c>/api/modules/foo/bar</c> registration would route requests to
/// the wrong group, and a regression that lets unknown paths resolve to
/// <c>null</c>-vs-arbitrary would change the gate's 404 semantics. These
/// tests exist independently of <see cref="SyntheticGatewayModuleTests"/>,
/// which exercises the full pipeline; this fixture nails the catalog
/// behaviour in isolation.
/// </summary>
[TestFixture]
public sealed class GatewayEndpointGroupCatalogTests
{
    private static GatewayEndpointGroupCatalog NewCatalog()
        => new(new StaticOptions(new GatewayModuleOptions()));

    private static GatewayEndpointGroup Group(string id)
        => new(id, $"Group {id}");

    [Test]
    public void TryRegister_FirstWriteWins_AndDuplicatePrefixIsRejected()
    {
        var c = NewCatalog();
        c.TryRegister("mod", Group("g")).Should().BeTrue();
        c.TryRegister("mod", Group("g")).Should().BeFalse(
            "the catalog must not silently shadow a previous registration; " +
            "the first registration owns the prefix.");
    }

    [Test]
    public void Unregister_RemovesPreviouslyRegisteredPrefix()
    {
        var c = NewCatalog();
        c.TryRegister("mod", Group("g")).Should().BeTrue();
        c.Unregister("mod", "g").Should().BeTrue();
        c.Resolve("/api/modules/mod/g").Should().BeNull();
        c.TryRegister("mod", Group("g")).Should().BeTrue(
            "after unregister the prefix is reusable.");
    }

    [Test]
    public void Resolve_ReturnsNull_ForUnknownModulePath()
    {
        var c = NewCatalog();
        c.Resolve("/api/modules/missing/group").Should().BeNull();
    }

    [Test]
    public void Resolve_RequiresSegmentBoundary()
    {
        var c = NewCatalog();
        c.TryRegister("mod", Group("bar")).Should().BeTrue();

        // A path that begins with the same characters but extends the
        // final segment must not resolve. Without this, a registration
        // for "/api/modules/mod/bar" would shadow "/api/modules/mod/bars".
        c.Resolve("/api/modules/mod/bars").Should().BeNull(
            "longest-prefix matching must respect segment boundaries.");

        c.Resolve("/api/modules/mod/bar").Should().NotBeNull();
        c.Resolve("/api/modules/mod/bar/").Should().NotBeNull();
        c.Resolve("/api/modules/mod/bar/sub").Should().NotBeNull(
            "child paths beneath a registered group resolve to that group.");
    }

    [Test]
    public void Resolve_PicksLongestMatchingPrefix()
    {
        var c = NewCatalog();
        c.TryRegister("mod", Group("a")).Should().BeTrue();
        c.TryRegister("mod", Group("a/b")).Should().BeTrue();

        var match = c.Resolve("/api/modules/mod/a/b/leaf");
        match.Should().NotBeNull();
        match!.Group.GroupId.Should().Be("a/b");

        var shallow = c.Resolve("/api/modules/mod/a/leaf");
        shallow.Should().NotBeNull();
        shallow!.Group.GroupId.Should().Be("a");
    }

    [Test]
    public void Resolve_IsCaseInsensitive_OnPrefix()
    {
        var c = NewCatalog();
        c.TryRegister("Mod", Group("Group")).Should().BeTrue();
        c.Resolve("/api/modules/mod/group").Should().NotBeNull();
        c.Resolve("/API/MODULES/MOD/GROUP").Should().NotBeNull();
    }

    [Test]
    public void IsEnabled_TracksOptionsMonitorSnapshot()
    {
        var moduleOptions = new GatewayModuleOptions();
        var monitor = new StaticOptions(moduleOptions);
        var c = new GatewayEndpointGroupCatalog(monitor);

        c.TryRegister("mod", Group("g")).Should().BeTrue();
        c.IsEnabled("mod", "g").Should().BeFalse(
            "registration alone does not enable a group; configuration must opt in.");

        moduleOptions.Modules["mod"] = true;
        moduleOptions.Groups["mod/g"] = true;
        c.IsEnabled("mod", "g").Should().BeTrue();

        moduleOptions.Modules["mod"] = false;
        c.IsEnabled("mod", "g").Should().BeFalse(
            "disabling the parent module must override an enabled group.");
    }

    [Test]
    public void TryRegister_EmptyGroupId_RegistersAtModuleRoot()
    {
        var c = NewCatalog();
        c.TryRegister("mod", new GatewayEndpointGroup(string.Empty, "Root")).Should().BeTrue();

        var match = c.Resolve("/api/modules/mod");
        match.Should().NotBeNull();
        match!.Prefix.Should().Be("/api/modules/mod");
    }

    private sealed class StaticOptions(GatewayModuleOptions value) : IOptionsMonitor<GatewayModuleOptions>
    {
        public GatewayModuleOptions CurrentValue { get; } = value;
        public GatewayModuleOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<GatewayModuleOptions, string?> listener) => null;
    }
}
