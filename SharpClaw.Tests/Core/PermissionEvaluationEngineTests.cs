using FluentAssertions;
using SharpClaw.Contracts;
using SharpClaw.Contracts.DTOs.AgentActions;
using SharpClaw.Contracts.Enums;
using SharpClaw.Core.Permissions;

namespace SharpClaw.Tests.Core;

[TestFixture]
public sealed class PermissionEvaluationEngineTests
{
    private readonly PermissionEvaluationEngine _engine = new();

    [Test]
    public void GlobalFlag_WhenChannelRestrictsGrant_DeniesBeforeRoleIndependent()
    {
        var role = PermissionSet(
            globalFlags: [new("CanClickDesktop", PermissionClearance.Independent)]);
        var channel = PermissionSet(
            globalFlags: [new("CanClickDesktop", PermissionClearance.Restricted)]);

        var result = _engine.EvaluateGlobalFlag(
            "CanClickDesktop",
            role,
            channel,
            contextPermissions: null,
            callerPermissions: null,
            caller: new ActionCaller(AgentId: Guid.NewGuid()));

        result.Verdict.Should().Be(ClearanceVerdict.Denied);
        result.Reason.Should().Contain("channel");
        result.Reason.Should().Contain("No approval path exists");
    }

    [Test]
    public void GlobalFlag_WhenChannelUnsetAndContextRequiresSameLevelUser_UsesContextClearance()
    {
        var callerUserId = Guid.NewGuid();
        var role = PermissionSet(
            globalFlags: [new("CanEditContext", PermissionClearance.Independent)]);
        var channel = PermissionSet(
            globalFlags: [new("CanEditContext", PermissionClearance.Unset)]);
        var context = PermissionSet(
            globalFlags: [new("CanEditContext", PermissionClearance.ApprovedBySameLevelUser)]);
        var caller = PermissionSet(
            globalFlags: [new("CanEditContext", PermissionClearance.Independent)]);

        var result = _engine.EvaluateGlobalFlag(
            "CanEditContext",
            role,
            channel,
            context,
            caller,
            new ActionCaller(UserId: callerUserId));

        result.Verdict.Should().Be(ClearanceVerdict.Approved);
        result.EffectiveClearance.Should().Be(PermissionClearance.ApprovedBySameLevelUser);
        result.Reason.Should().Be("Approved by same-level user.");
    }

    [Test]
    public void GlobalFlag_WhenPermittedAgentIsRequired_UserWithSamePermissionCannotApprove()
    {
        var role = PermissionSet(
            globalFlags: [new("CanRunShell", PermissionClearance.ApprovedByPermittedAgent)]);
        var caller = PermissionSet(
            globalFlags: [new("CanRunShell", PermissionClearance.Independent)]);

        var result = _engine.EvaluateGlobalFlag(
            "CanRunShell",
            role,
            channelPermissions: null,
            contextPermissions: null,
            caller,
            new ActionCaller(UserId: Guid.NewGuid()));

        result.Verdict.Should().Be(ClearanceVerdict.PendingApproval);
        result.EffectiveClearance.Should().Be(PermissionClearance.ApprovedByPermittedAgent);
    }

    [Test]
    public void GlobalFlag_WhenWhitelistedAgentIsRequired_PermittedAgentCanFallbackApprove()
    {
        var callerAgentId = Guid.NewGuid();
        var role = PermissionSet(
            globalFlags: [new("CanCallTool", PermissionClearance.ApprovedByWhitelistedAgent)]);
        var caller = PermissionSet(
            globalFlags: [new("CanCallTool", PermissionClearance.Independent)]);

        var result = _engine.EvaluateGlobalFlag(
            "CanCallTool",
            role,
            channelPermissions: null,
            contextPermissions: null,
            caller,
            new ActionCaller(AgentId: callerAgentId));

        result.Verdict.Should().Be(ClearanceVerdict.Approved);
        result.EffectiveClearance.Should().Be(PermissionClearance.ApprovedByWhitelistedAgent);
        result.Reason.Should().Be("Approved by permitted agent.");
    }

    [Test]
    public void ResourceAccess_WhenRoleHasWildcardGrant_SpecificResourceCanBeApprovedBySameLevelUser()
    {
        var resourceId = Guid.NewGuid();
        var role = PermissionSet(
            resourceAccesses:
            [
                new("AoTask", WellKnownIds.AllResources, PermissionClearance.ApprovedBySameLevelUser)
            ]);
        var caller = PermissionSet(
            resourceAccesses:
            [
                new("AoTask", resourceId, PermissionClearance.Independent)
            ]);

        var result = _engine.EvaluateResourceAccess(
            "AoTask",
            resourceId,
            "AoTask access",
            role,
            channelPermissions: null,
            contextPermissions: null,
            caller,
            new ActionCaller(UserId: Guid.NewGuid()));

        result.Verdict.Should().Be(ClearanceVerdict.Approved);
        result.EffectiveClearance.Should().Be(PermissionClearance.ApprovedBySameLevelUser);
    }

    private static PermissionSetSnapshot PermissionSet(
        IReadOnlyList<GlobalFlagPermissionGrant>? globalFlags = null,
        IReadOnlyList<ResourcePermissionGrant>? resourceAccesses = null,
        IReadOnlySet<Guid>? userWhitelist = null,
        IReadOnlySet<Guid>? agentWhitelist = null) =>
        new(
            globalFlags ?? [],
            resourceAccesses ?? [],
            userWhitelist ?? new HashSet<Guid>(),
            agentWhitelist ?? new HashSet<Guid>());
}
