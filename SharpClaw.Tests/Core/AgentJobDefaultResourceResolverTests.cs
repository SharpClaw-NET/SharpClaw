using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.DTOs.AgentActions;
using SharpClaw.Contracts.Enums;
using SharpClaw.Contracts.Modules;
using SharpClaw.Core.Jobs;
using SharpClaw.Core.Modules;
using SharpClaw.Core.Permissions;
using SharpClaw.Core.Resources;

namespace SharpClaw.Tests.Core;

[TestFixture]
public sealed class AgentJobDefaultResourceResolverTests
{
    [Test]
    public void ResolveDefaultResource_WhenActionMapsToDefaultKey_UsesChannelBeforeContextAndPermissions()
    {
        var channelResource = Guid.NewGuid();
        var contextResource = Guid.NewGuid();
        var permissionResource = Guid.NewGuid();
        var resolver = CreateResolver();

        var resolved = resolver.ResolveDefaultResource(
            new AgentJobDefaultResourceResolutionRequest(
                "run",
                CreateRegistry(),
                Set(("task", channelResource)),
                Set(("task", contextResource)),
                [
                    PermissionSet(
                        new ResourcePermissionGrant(
                            "AoTask",
                            permissionResource,
                            PermissionClearance.Independent,
                            IsDefault: true))
                ]));

        resolved.Should().Be(channelResource);
    }

    [Test]
    public void ResolveDefaultResource_WhenDefaultsMiss_UsesPermissionSetByMappedResourceType()
    {
        var firstPermissionResource = Guid.NewGuid();
        var secondPermissionResource = Guid.NewGuid();
        var resolver = CreateResolver();

        var resolved = resolver.ResolveDefaultResource(
            new AgentJobDefaultResourceResolutionRequest(
                "run",
                CreateRegistry(),
                ChannelDefaults: null,
                ContextDefaults: Set(("other", Guid.NewGuid())),
                OrderedPermissionSets:
                [
                    PermissionSet(
                        new ResourcePermissionGrant(
                            "AoTask",
                            firstPermissionResource,
                            PermissionClearance.Independent,
                            IsDefault: true)),
                    PermissionSet(
                        new ResourcePermissionGrant(
                            "AoTask",
                            secondPermissionResource,
                            PermissionClearance.Independent,
                            IsDefault: true))
                ]));

        resolved.Should().Be(firstPermissionResource);
    }

    [Test]
    public void ResolveDefaultResource_WhenActionHasNoDelegate_ReturnsNull()
    {
        var resolver = CreateResolver();

        var resolved = resolver.ResolveDefaultResource(
            new AgentJobDefaultResourceResolutionRequest(
                "plain",
                CreateRegistry(),
                Set(("task", Guid.NewGuid())),
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

        resolved.Should().BeNull();
    }

    private static AgentJobDefaultResourceResolver CreateResolver()
        => new(new AgentJobAdministrationEngine(), new DefaultResourceEngine());

    private static ModuleRegistry CreateRegistry()
    {
        var registry = new ModuleRegistry();
        registry.Register(new TestModule());
        return registry;
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

    private static JsonElement Json(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private sealed class TestModule : ISharpClawCoreModule
    {
        public string Id => "test_module";
        public string DisplayName => "Test Module";
        public string ToolPrefix => "test";

        public void ConfigureServices(IServiceCollection services)
        {
        }

        public IReadOnlyList<ModuleToolDefinition> GetToolDefinitions() =>
        [
            new(
                "run",
                "Run",
                Json("""{"type":"object"}"""),
                new ModuleToolPermission(
                    IsPerResource: true,
                    Check: (_, _, _, _) => Task.FromResult(
                        AgentActionResult.Approve(
                            "ok",
                            PermissionClearance.Independent)),
                    DelegateTo: "AccessTask")),
            new(
                "plain",
                "Plain",
                Json("""{"type":"object"}"""),
                new ModuleToolPermission(
                    IsPerResource: false,
                    Check: (_, _, _, _) => Task.FromResult(
                        AgentActionResult.Approve(
                            "ok",
                            PermissionClearance.Independent))))
        ];

        public IReadOnlyList<ModuleResourceTypeDescriptor>
            GetResourceTypeDescriptors() =>
        [
            new(
                "AoTask",
                "Task",
                "AccessTask",
                (_, _) => Task.FromResult(new List<Guid>()),
                DefaultResourceKey: "task")
        ];

        public Task<string> ExecuteToolAsync(
            string toolName,
            JsonElement parameters,
            AgentJobContext job,
            IServiceProvider scopedServices,
            CancellationToken ct) =>
            throw new NotSupportedException();
    }
}
