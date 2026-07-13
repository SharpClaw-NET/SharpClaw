using System.Text.Json;
using SharpClaw.Contracts;
using SharpClaw.Contracts.DTOs.Agents;
using SharpClaw.Contracts.Entities.Core;
using SharpClaw.Contracts.Entities.Core.Access;
using SharpClaw.Contracts.Entities.Core.Clearance;
using SharpClaw.Contracts.Enums;
using SharpClaw.Contracts.Providers;
using SharpClaw.Core.Agents;

namespace SharpClaw.Tests.Core;

[TestFixture]
public sealed class AgentAdministrationEngineTests
{
    private readonly AgentAdministrationEngine _engine = new();

    [Test]
    public void Create_MapsCreateRequestAndPreservesExistingCreateHeaderBehavior()
    {
        var model = CreateModel();
        using var document = JsonDocument.Parse("""{"type":"json_object"}""");
        var request = new CreateAgentRequest(
            Name: "agent",
            ModelId: model.Id,
            SystemPrompt: "system",
            MaxCompletionTokens: 128,
            CustomId: "custom",
            Temperature: 0.5f,
            ResponseFormat: document.RootElement.Clone(),
            ProviderParameters: new Dictionary<string, JsonElement>
            {
            ["provider"] = JsonDocument.Parse("true").RootElement.Clone()
            },
            CustomChatHeader: "header",
            DisableToolSchemas: null);

        var agent = _engine.Create(
            request,
            model,
            ICompletionParameterSpec.Passthrough);

        agent.Name.Should().Be("agent");
        agent.ModelId.Should().Be(model.Id);
        agent.Model.Should().BeSameAs(model);
        agent.SystemPrompt.Should().Be("system");
        agent.MaxCompletionTokens.Should().Be(128);
        agent.CustomId.Should().Be("custom");
        agent.Temperature.Should().Be(0.5f);
        agent.ProviderParameters.Should().ContainKey("provider");
        agent.CustomChatHeader.Should().BeNull();
        agent.DisableToolSchemas.Should().BeFalse();
    }

    [Test]
    public void ApplyUpdate_ClearsSentinelAndEmptyValues()
    {
        var model = CreateModel();
        var agent = new AgentDB
        {
            Id = Guid.NewGuid(),
            Name = "agent",
            ModelId = model.Id,
            Model = model,
            Stop = ["old"],
            ProviderParameters = new Dictionary<string, JsonElement>
            {
                ["old"] = JsonDocument.Parse("1").RootElement.Clone()
            },
            CustomChatHeader = "header",
            ToolAwarenessSetId = Guid.NewGuid(),
            DisableToolSchemas = false
        };

        var request = new UpdateAgentRequest(
            Stop: [],
            ProviderParameters: [],
            CustomChatHeader: "",
            ToolAwarenessSetId: Guid.Empty,
            DisableToolSchemas: true);

        _engine.ApplyUpdate(
            agent,
            request,
            replacementModel: null,
            parameterSpec: ICompletionParameterSpec.Passthrough,
            enforceUniqueNames: true,
            existingAgentNames: []);

        agent.Stop.Should().BeNull();
        agent.ProviderParameters.Should().BeNull();
        agent.CustomChatHeader.Should().BeNull();
        agent.ToolAwarenessSetId.Should().BeNull();
        agent.DisableToolSchemas.Should().BeTrue();
    }

    [Test]
    public void ApplyUpdate_WhenNameConflictsAfterTrimAndCase_Throws()
    {
        var model = CreateModel();
        var agent = new AgentDB
        {
            Name = "current",
            ModelId = model.Id,
            Model = model
        };

        var act = () => _engine.ApplyUpdate(
            agent,
            new UpdateAgentRequest(Name: " Admin "),
            replacementModel: null,
            parameterSpec: ICompletionParameterSpec.Passthrough,
            enforceUniqueNames: true,
            existingAgentNames: ["admin"]);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("An agent named ' Admin ' already exists.");
        agent.Name.Should().Be("current");
    }

    [Test]
    public void AssignRole_WhenCallerHasSameRole_SkipsPermissionCoverage()
    {
        var model = CreateModel();
        var role = new RoleDB
        {
            Id = Guid.NewGuid(),
            Name = "target"
        };
        var agent = new AgentDB
        {
            Name = "agent",
            ModelId = model.Id,
            Model = model
        };
        var targetPermissions = new PermissionSetDB();
        targetPermissions.GlobalFlags.Add(new GlobalFlagDB
        {
            FlagKey = "CanDoThing",
            Clearance = PermissionClearance.Independent
        });

        _engine.AssignRole(
            agent,
            role.Id,
            role,
            callerRoleId: role.Id,
            callerPermissionSet: null,
            targetPermissionSet: targetPermissions,
            registeredResourceTypes: ["Module.Resource"]);

        agent.RoleId.Should().Be(role.Id);
        agent.Role.Should().BeSameAs(role);
    }

    [Test]
    public void AssignRole_WhenCallerLacksResourceGrant_Throws()
    {
        var model = CreateModel();
        var role = new RoleDB
        {
            Id = Guid.NewGuid(),
            Name = "target"
        };
        var agent = new AgentDB
        {
            Name = "agent",
            ModelId = model.Id,
            Model = model
        };
        var targetPermissions = new PermissionSetDB();
        targetPermissions.ResourceAccesses.Add(new ResourceAccessDB
        {
            ResourceType = "Module.Resource",
            ResourceId = Guid.NewGuid(),
            Clearance = PermissionClearance.Independent
        });
        var callerPermissions = new PermissionSetDB();

        var act = () => _engine.AssignRole(
            agent,
            role.Id,
            role,
            callerRoleId: null,
            callerPermissionSet: callerPermissions,
            targetPermissionSet: targetPermissions,
            registeredResourceTypes: ["Module.Resource"]);

        act.Should().Throw<UnauthorizedAccessException>()
            .WithMessage("Cannot assign 'target': you hold no Module.Resource grants.");
    }

    [Test]
    public void CreateDefaultAgentIfMissing_UsesCaseInsensitiveKnownNameSet()
    {
        var model = CreateModel(name: "gpt");
        var known = new HashSet<string>(
            ["DEFAULT-GPT-openai"],
            StringComparer.OrdinalIgnoreCase);

        var skipped = _engine.CreateDefaultAgentIfMissing(model, "openai", known);
        skipped.Should().BeNull();

        var created = _engine.CreateDefaultAgentIfMissing(model, "azure", known);
        created.Should().NotBeNull();
        created!.Name.Should().Be("default-gpt-azure");
        known.Should().Contain("default-gpt-azure");
    }

    private static ModelDB CreateModel(string name = "model")
    {
        var provider = new ProviderDB
        {
            Id = Guid.NewGuid(),
            Name = "Provider",
            ProviderKey = "provider"
        };

        return new ModelDB
        {
            Id = Guid.NewGuid(),
            Name = name,
            ProviderId = provider.Id,
            Provider = provider
        };
    }
}
