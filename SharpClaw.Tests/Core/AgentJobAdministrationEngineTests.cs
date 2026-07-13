using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts;
using SharpClaw.Contracts.DTOs.AgentActions;
using SharpClaw.Contracts.Entities.Core;
using SharpClaw.Contracts.Entities.Core.Access;
using SharpClaw.Contracts.Entities.Core.Clearance;
using SharpClaw.Contracts.Entities.Core.Context;
using SharpClaw.Contracts.Entities.Core.Jobs;
using SharpClaw.Contracts.Enums;
using SharpClaw.Contracts.Modules;
using SharpClaw.Core.Jobs;
using SharpClaw.Core.Modules;

namespace SharpClaw.Tests.Core;

[TestFixture]
public sealed class AgentJobAdministrationEngineTests
{
    private readonly AgentJobAdministrationEngine _engine = new();

    [Test]
    public void ResolveSubmissionAgent_WhenChannelHasAllowedOverride_UsesRequestedAgent()
    {
        var defaultAgentId = Guid.NewGuid();
        var requestedAgentId = Guid.NewGuid();
        var channel = new ChannelDB
        {
            Id = Guid.NewGuid(),
            Title = "channel",
            AgentId = defaultAgentId
        };
        channel.AllowedAgents.Add(new AgentDB
        {
            Id = requestedAgentId,
            Name = "allowed"
        });

        var resolved = _engine.ResolveSubmissionAgent(
            channel,
            channel.Id,
            requestedAgentId);

        resolved.Should().Be(requestedAgentId);
    }

    [Test]
    public void ResolveSubmissionAgent_WhenChannelHasNoAgent_UsesContextAgent()
    {
        var contextAgentId = Guid.NewGuid();
        var channel = new ChannelDB
        {
            Id = Guid.NewGuid(),
            Title = "channel",
            AgentContext = new ChannelContextDB
            {
                Name = "context",
                AgentId = contextAgentId
            }
        };

        var resolved = _engine.ResolveSubmissionAgent(
            channel,
            channel.Id,
            requestedAgentId: null);

        resolved.Should().Be(contextAgentId);
    }

    [Test]
    public void ResolveSubmissionAgent_WhenRequestedAgentIsNotAllowed_Throws()
    {
        var defaultAgentId = Guid.NewGuid();
        var requestedAgentId = Guid.NewGuid();
        var channel = new ChannelDB
        {
            Id = Guid.NewGuid(),
            Title = "channel",
            AgentId = defaultAgentId
        };

        var act = () => _engine.ResolveSubmissionAgent(
            channel,
            channel.Id,
            requestedAgentId);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"Agent {requestedAgentId} is not allowed on channel {channel.Id}*");
    }

    [Test]
    public void CreateSubmissionJob_MapsRequestAndCaller()
    {
        var channelId = Guid.NewGuid();
        var agentId = Guid.NewGuid();
        var callerUserId = Guid.NewGuid();
        var callerAgentId = Guid.NewGuid();
        var resourceId = Guid.NewGuid();
        var request = new SubmitAgentJobRequest(
            ActionKey: "module.tool",
            ResourceId: null,
            AgentId: Guid.NewGuid(),
            CallerAgentId: callerAgentId,
            ScriptJson: "{}",
            WorkingDirectory: "E:\\work");

        var job = _engine.CreateSubmissionJob(
            channelId,
            agentId,
            request,
            callerUserId,
            resourceId);

        job.ChannelId.Should().Be(channelId);
        job.AgentId.Should().Be(agentId);
        job.CallerUserId.Should().Be(callerUserId);
        job.CallerAgentId.Should().Be(callerAgentId);
        job.ActionKey.Should().Be("module.tool");
        job.ResourceId.Should().Be(resourceId);
        job.ScriptJson.Should().Be("{}");
        job.WorkingDirectory.Should().Be("E:\\work");
    }

    [Test]
    public void ApplyLifecycleDecision_UpdatesJobAndAttachesLogs()
    {
        var started = DateTimeOffset.Parse("2026-06-28T18:00:00Z");
        var job = new AgentJobDB();
        var decision = new AgentJobLifecycleDecision
        {
            Status = AgentJobStatus.Executing,
            UpdateStartedAt = true,
            StartedAt = started,
            Logs =
            [
                new AgentJobLifecycleLog("started", JobLogLevels.Info),
                new AgentJobLifecycleLog("careful", JobLogLevels.Warning)
            ]
        };

        var logs = _engine.ApplyLifecycleDecision(job, decision);

        job.Status.Should().Be(AgentJobStatus.Executing);
        job.StartedAt.Should().Be(started);
        logs.Should().HaveCount(2);
        job.LogEntries.Should().BeEquivalentTo(logs);
        logs.Select(log => log.Message).Should().Equal("started", "careful");
    }

    [Test]
    public void ToResponse_IncludesOrderedLogsAndTokenUsage()
    {
        var job = new AgentJobDB
        {
            Id = Guid.NewGuid(),
            ChannelId = Guid.NewGuid(),
            AgentId = Guid.NewGuid(),
            ActionKey = "module.tool",
            Status = AgentJobStatus.Completed,
            EffectiveClearance = PermissionClearance.Independent,
            PromptTokens = 3,
            CompletionTokens = 4,
            CreatedAt = DateTimeOffset.Parse("2026-06-28T18:00:00Z")
        };
        job.LogEntries.Add(new AgentJobLogEntryDB
        {
            Message = "second",
            CreatedAt = DateTimeOffset.Parse("2026-06-28T18:00:02Z")
        });
        job.LogEntries.Add(new AgentJobLogEntryDB
        {
            Message = "first",
            CreatedAt = DateTimeOffset.Parse("2026-06-28T18:00:01Z")
        });

        var response = _engine.ToResponse(job);

        response.JobCost.Should().NotBeNull();
        response.JobCost!.TotalPromptTokens.Should().Be(3);
        response.JobCost.TotalCompletionTokens.Should().Be(4);
        response.JobCost.TotalTokens.Should().Be(7);
        response.Logs.Select(log => log.Message).Should().Equal("first", "second");
    }

    [Test]
    public void ApplyTokenUsage_SplitsRemaindersOntoFirstJob()
    {
        var first = new AgentJobDB();
        var second = new AgentJobDB();

        _engine.ApplyTokenUsage([first, second], promptTokens: 5, completionTokens: 7);

        first.PromptTokens.Should().Be(3);
        second.PromptTokens.Should().Be(2);
        first.CompletionTokens.Should().Be(4);
        second.CompletionTokens.Should().Be(3);
    }

    [Test]
    public void ApplyTokenUsage_WhenPromptTokensAreNegative_Throws()
    {
        var act = () => _engine.ApplyTokenUsage(
            [new AgentJobDB()],
            promptTokens: -1,
            completionTokens: 0);

        act.Should().Throw<ArgumentOutOfRangeException>()
            .WithMessage("Prompt tokens cannot be negative.*");
    }

    [Test]
    public void ChannelPreauthorizationRules_MatchClearanceLevels()
    {
        _engine.CanUseChannelPreauthorization(
                PermissionClearance.ApprovedByPermittedAgent)
            .Should().BeFalse();
        _engine.CanUseChannelPreauthorization(
                PermissionClearance.ApprovedBySameLevelUser)
            .Should().BeTrue();
        _engine.RequiresCallerGrantForChannelPreauthorization(
                PermissionClearance.ApprovedBySameLevelUser)
            .Should().BeTrue();
        _engine.RequiresCallerGrantForChannelPreauthorization(
                PermissionClearance.ApprovedByWhitelistedUser)
            .Should().BeFalse();
    }

    [Test]
    public void EvaluateChannelPreauthorization_WhenClearanceIsAgentOnly_IsNotApplicable()
    {
        var decision = _engine.EvaluateChannelPreauthorization(
            PermissionClearance.ApprovedByPermittedAgent,
            callerHasGrant: true,
            channelHasGrant: true,
            contextHasGrant: true);

        decision.IsPreauthorized.Should().BeFalse();
        decision.Source.Should().Be(AgentJobChannelPreauthorizationSource.NotApplicable);
        decision.RequiresCallerGrant.Should().BeFalse();
    }

    [Test]
    public void EvaluateChannelPreauthorization_WhenSameLevelUserCallerLacksGrant_RejectsBeforeChannel()
    {
        var decision = _engine.EvaluateChannelPreauthorization(
            PermissionClearance.ApprovedBySameLevelUser,
            callerHasGrant: false,
            channelHasGrant: true,
            contextHasGrant: true);

        decision.IsPreauthorized.Should().BeFalse();
        decision.Source.Should().Be(AgentJobChannelPreauthorizationSource.CallerGrantMissing);
        decision.RequiresCallerGrant.Should().BeTrue();
    }

    [Test]
    public void EvaluateChannelPreauthorization_WhenChannelMatches_UsesChannelBeforeContext()
    {
        var decision = _engine.EvaluateChannelPreauthorization(
            PermissionClearance.ApprovedBySameLevelUser,
            callerHasGrant: true,
            channelHasGrant: true,
            contextHasGrant: true);

        decision.IsPreauthorized.Should().BeTrue();
        decision.Source.Should().Be(AgentJobChannelPreauthorizationSource.Channel);
        decision.RequiresCallerGrant.Should().BeTrue();
    }

    [Test]
    public void EvaluateChannelPreauthorization_WhenOnlyContextMatches_UsesContext()
    {
        var decision = _engine.EvaluateChannelPreauthorization(
            PermissionClearance.ApprovedByWhitelistedUser,
            callerHasGrant: false,
            channelHasGrant: false,
            contextHasGrant: true);

        decision.IsPreauthorized.Should().BeTrue();
        decision.Source.Should().Be(AgentJobChannelPreauthorizationSource.Context);
        decision.RequiresCallerGrant.Should().BeFalse();
    }

    [Test]
    public void EvaluateChannelPreauthorization_WhenNoChannelOrContextGrant_ReturnsNoGrant()
    {
        var decision = _engine.EvaluateChannelPreauthorization(
            PermissionClearance.ApprovedByWhitelistedAgent,
            callerHasGrant: false,
            channelHasGrant: false,
            contextHasGrant: false);

        decision.IsPreauthorized.Should().BeFalse();
        decision.Source.Should().Be(AgentJobChannelPreauthorizationSource.NoGrant);
        decision.RequiresCallerGrant.Should().BeFalse();
    }

    [Test]
    public void IsPerResourceAction_WhenRegisteredDescriptorIsPerResource_ReturnsTrue()
    {
        var registry = CreateRegistry();

        _engine.IsPerResourceAction(registry, "docs_open")
            .Should().BeTrue();
    }

    [Test]
    public void ResolveDelegateTo_WhenActionIsRegistered_ReturnsDescriptorDelegate()
    {
        var registry = CreateRegistry();

        _engine.ResolveDelegateTo(registry, "docs_open")
            .Should().Be("UseDocumentAsync");
    }

    [Test]
    public void HasMatchingGrant_WhenResourceGrantMatchesDelegate_ReturnsTrue()
    {
        var registry = CreateRegistry();
        var resourceId = Guid.NewGuid();
        var permissionSet = new PermissionSetDB
        {
            ResourceAccesses =
            [
                new ResourceAccessDB
                {
                    ResourceType = "documents",
                    ResourceId = resourceId,
                    Clearance = PermissionClearance.Independent
                }
            ]
        };

        _engine.HasMatchingGrant(
                registry,
                permissionSet,
                resourceId,
                "docs_open")
            .Should().BeTrue();
    }

    [Test]
    public void HasGrantByDelegateName_WhenGlobalFlagMatches_ReturnsTrue()
    {
        var registry = CreateRegistry();
        var permissionSet = new PermissionSetDB
        {
            GlobalFlags =
            [
                new GlobalFlagDB
                {
                    FlagKey = "CanAuditJobs",
                    Clearance = PermissionClearance.Independent
                }
            ]
        };

        _engine.HasGrantByDelegateName(
                registry,
                permissionSet,
                "AuditJobsAsync",
                resourceId: null)
            .Should().BeTrue();
    }

    [Test]
    public void BuildActionPrefixPredicate_WhenResourceIsProvided_FiltersByPrefixAndResource()
    {
        var matchingResourceId = Guid.NewGuid();
        var otherResourceId = Guid.NewGuid();
        var predicate = _engine.BuildActionPrefixPredicate(
                "docs_",
                matchingResourceId)
            .Compile();

        predicate(new AgentJobDB
        {
            ActionKey = "docs_open",
            ResourceId = matchingResourceId
        }).Should().BeTrue();
        predicate(new AgentJobDB
        {
            ActionKey = "DOCS_OPEN",
            ResourceId = matchingResourceId
        }).Should().BeTrue();
        predicate(new AgentJobDB
        {
            ActionKey = "docs_open",
            ResourceId = otherResourceId
        }).Should().BeFalse();
    }

    [Test]
    public void OrderMostRecent_SortsJobsByCreatedAtDescending()
    {
        var older = new AgentJobDB
        {
            Id = Guid.NewGuid(),
            CreatedAt = DateTimeOffset.Parse("2026-06-28T18:00:00Z")
        };
        var newer = new AgentJobDB
        {
            Id = Guid.NewGuid(),
            CreatedAt = DateTimeOffset.Parse("2026-06-28T19:00:00Z")
        };

        var ordered = _engine.OrderMostRecent([older, newer]);

        ordered.Select(job => job.Id).Should().Equal(newer.Id, older.Id);
    }

    private static ModuleRegistry CreateRegistry()
    {
        var registry = new ModuleRegistry();
        registry.Register(new JobRulesModule());
        return registry;
    }

    private sealed class JobRulesModule : ISharpClawCoreModule
    {
        private static readonly JsonElement EmptySchema =
            JsonDocument.Parse("{}").RootElement.Clone();

        public string Id => "job_rules";
        public string DisplayName => "Job Rules";
        public string ToolPrefix => "jobrules";
        public void ConfigureServices(IServiceCollection services) { }

        public IReadOnlyList<ModuleToolDefinition> GetToolDefinitions() =>
        [
            new(
                "docs_open",
                "Open a document.",
                EmptySchema,
                new ModuleToolPermission(
                    IsPerResource: true,
                    Check: null,
                    DelegateTo: "UseDocumentAsync"))
        ];

        public IReadOnlyList<ModuleResourceTypeDescriptor> GetResourceTypeDescriptors() =>
        [
            new(
                "documents",
                "Documents",
                "UseDocumentAsync",
                (_, _) => Task.FromResult(new List<Guid>()))
        ];

        public IReadOnlyList<ModuleGlobalFlagDescriptor> GetGlobalFlagDescriptors() =>
        [
            new(
                "CanAuditJobs",
                "Audit Jobs",
                "Audit job execution.",
                "AuditJobsAsync")
        ];

        public Task<string> ExecuteToolAsync(
            string toolName,
            JsonElement parameters,
            AgentJobContext job,
            IServiceProvider scopedServices,
            CancellationToken ct) =>
            Task.FromResult("");
    }
}
