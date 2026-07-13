using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts;
using SharpClaw.Contracts.Entities;
using SharpClaw.Contracts.Entities.Core;
using SharpClaw.Contracts.Entities.Core.Access;
using SharpClaw.Contracts.Entities.Core.Clearance;
using SharpClaw.Contracts.Entities.Core.Context;
using SharpClaw.Contracts.Enums;
using SharpClaw.Contracts.Modules;
using SharpClaw.Contracts.Providers;
using SharpClaw.Core.Clients;
using SharpClaw.Core.Modules;

namespace SharpClaw.Tests.Core;

[TestFixture]
public sealed class ChatHeaderTemplateEngineTests
{
    [Test]
    public async Task ExpandAsync_WhenBuiltInAgentTagIsUsed_ExpandsFromContext()
    {
        var engine = CreateEngine();
        var expanded = await engine.ExpandAsync(
            "[{{agent-name}} via {{via}}]",
            CreateContext(agentName: "Ada"),
            new ChatHeaderExpansionOptions(),
            resourceTags: null,
            new ServiceCollection().BuildServiceProvider(),
            CancellationToken.None);

        expanded.Should().Be("[Ada via api]");
    }

    [Test]
    public void ExtractTagNames_WhenRepeatedTagsExist_ReturnsUniqueNamesInEncounterOrder()
    {
        var names = ChatHeaderTemplateEngine.ExtractTagNames(
            "{{agent-name}} {{Providers:{Name}}} {{agent-name}} {{unknown}}");

        names.Should().Equal("agent-name", "Providers", "unknown");
    }

    [Test]
    public async Task ExpandAsync_WhenResourceTemplateUsesSensitiveField_RedactsIt()
    {
        var provider = new ProviderDB
        {
            Id = Guid.NewGuid(),
            Name = "Custom",
            EncryptedApiKey = "secret"
        };
        var resolver = new StaticResourceTagResolver("providers", [provider]);
        var engine = CreateEngine();

        var expanded = await engine.ExpandAsync(
            "{{Providers:{Name}:{EncryptedApiKey}:{Missing}}}",
            CreateContext(),
            new ChatHeaderExpansionOptions(),
            resolver,
            new ServiceCollection().BuildServiceProvider(),
            CancellationToken.None);

        expanded.Should().Be("Custom:[redacted]:[Missing?]");
    }

    [Test]
    public async Task ExpandAsync_WhenModuleTagIsRegistered_UsesContextAwareResolver()
    {
        var registry = new ModuleRegistry();
        registry.Register(new HeaderModule(
            headerTags:
            [
                new ModuleHeaderTag(
                    "active",
                    static (_, _) => Task.FromResult("fallback"))
                {
                    ResolveWithContext = static (_, ctx, _) => Task.FromResult(
                        $"{ctx.ChannelTitle}|{ctx.AgentName}|{ctx.ClientType}")
                }
            ]));
        var engine = CreateEngine(registry);

        var expanded = await engine.ExpandAsync(
            "{{active}}",
            CreateContext(channelTitle: "Planning", agentName: "Ada"),
            new ChatHeaderExpansionOptions(),
            resourceTags: null,
            new ServiceCollection().BuildServiceProvider(),
            CancellationToken.None);

        expanded.Should().Be("Planning|Ada|api");
    }

    [Test]
    public async Task ExpandAsync_WhenModuleHeaderTagsAreDisabled_ReplacesModuleTagWithEmptyText()
    {
        var registry = new ModuleRegistry();
        registry.Register(new HeaderModule(
            headerTags:
            [
                new ModuleHeaderTag(
                    "active",
                    static (_, _) => Task.FromResult("visible"))
            ]));
        var engine = CreateEngine(registry);

        var expanded = await engine.ExpandAsync(
            "before {{active}} after",
            CreateContext(),
            new ChatHeaderExpansionOptions(DisableModuleHeaderTags: true),
            resourceTags: null,
            new ServiceCollection().BuildServiceProvider(),
            CancellationToken.None);

        expanded.Should().Be("before  after");
    }

    [Test]
    public async Task ExpandAsync_WhenAgentHasWildcardResourceGrant_ExpandsGrantIds()
    {
        var firstId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var secondId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var registry = new ModuleRegistry();
        registry.Register(new HeaderModule(
            resourceTypes:
            [
                new ModuleResourceTypeDescriptor(
                    "documents",
                    "Documents",
                    "CanUseDocuments",
                    static (_, _) => Task.FromResult(new List<Guid>
                    {
                        Guid.Parse("11111111-1111-1111-1111-111111111111"),
                        Guid.Parse("22222222-2222-2222-2222-222222222222")
                    }))
            ]));
        var engine = CreateEngine(registry);
        var permissionSet = new PermissionSetDB
        {
            ResourceAccesses =
            [
                new ResourceAccessDB
                {
                    ResourceType = "documents",
                    ResourceId = WellKnownIds.AllResources,
                    Clearance = PermissionClearance.Independent
                }
            ]
        };

        var expanded = await engine.ExpandAsync(
            "{{agent-role}} | {{agent-grants}}",
            CreateContext(
                agentRole: new RoleDB { Name = "Researcher" },
                agentPermissionSet: permissionSet),
            new ChatHeaderExpansionOptions(),
            resourceTags: null,
            new ServiceCollection().BuildServiceProvider(),
            CancellationToken.None);

        var expectedGrant = $"Documents[{firstId:D},{secondId:D}]";
        expanded.Should().Be($"Researcher ({expectedGrant}) | {expectedGrant}");
    }

    private static ChatHeaderTemplateEngine CreateEngine(ModuleRegistry? registry = null)
    {
        registry ??= new ModuleRegistry();
        return new ChatHeaderTemplateEngine(
            registry,
            new ProviderApiClientFactory([], registry));
    }

    private static ChatHeaderExpansionContext CreateContext(
        string channelTitle = "Channel",
        string agentName = "Agent",
        RoleDB? agentRole = null,
        PermissionSetDB? agentPermissionSet = null)
    {
        return new ChatHeaderExpansionContext(
            new ChannelDB
            {
                Id = Guid.NewGuid(),
                Title = channelTitle
            },
            new AgentDB
            {
                Id = Guid.NewGuid(),
                Name = agentName
            },
            "api",
            User: null,
            UserPs: null,
            AgentRole: agentRole,
            AgentPs: agentPermissionSet);
    }

    private sealed class StaticResourceTagResolver(
        string tagName,
        IReadOnlyList<BaseEntity> entities)
        : IChatHeaderResourceTagResolver
    {
        public Task<IReadOnlyList<BaseEntity>?> LoadEntitiesAsync(
            string requestedTagName,
            CancellationToken ct)
        {
            IReadOnlyList<BaseEntity>? result = requestedTagName.Equals(
                tagName,
                StringComparison.OrdinalIgnoreCase)
                ? entities
                : null;
            return Task.FromResult(result);
        }
    }

    private sealed class HeaderModule(
        IReadOnlyList<ModuleHeaderTag>? headerTags = null,
        IReadOnlyList<ModuleResourceTypeDescriptor>? resourceTypes = null)
        : ISharpClawCoreModule
    {
        public string Id => "header_test";
        public string DisplayName => "Header Test";
        public string ToolPrefix => "headertest";
        public void ConfigureServices(IServiceCollection services) { }
        public IReadOnlyList<ModuleToolDefinition> GetToolDefinitions() => [];
        public IReadOnlyList<ModuleHeaderTag>? GetHeaderTags() => headerTags;
        public IReadOnlyList<ModuleResourceTypeDescriptor> GetResourceTypeDescriptors()
            => resourceTypes ?? [];

        public Task<string> ExecuteToolAsync(
            string toolName,
            JsonElement parameters,
            AgentJobContext job,
            IServiceProvider scopedServices,
            CancellationToken ct)
        {
            return Task.FromResult("");
        }
    }
}
