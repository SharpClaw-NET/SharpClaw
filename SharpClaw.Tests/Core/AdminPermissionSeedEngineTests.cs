using SharpClaw.Contracts.Enums;
using SharpClaw.Core.Permissions;

namespace SharpClaw.Tests.Core;

[TestFixture]
public sealed class AdminPermissionSeedEngineTests
{
    private readonly AdminPermissionSeedEngine _engine = new();

    [Test]
    public void BuildCreatePlan_ProjectsRegisteredKeysToIndependentAdminGrants()
    {
        var plan = _engine.BuildCreatePlan(
            ["CanRunShell", "CanEditFiles"],
            ["shell", "documents"]);

        plan.GlobalFlags.Should().Equal(
            new AdminGlobalFlagGrantDescriptor(
                "CanRunShell",
                PermissionClearance.Independent),
            new AdminGlobalFlagGrantDescriptor(
                "CanEditFiles",
                PermissionClearance.Independent));
        plan.WildcardResources.Should().Equal(
            new AdminWildcardResourceGrantDescriptor(
                "shell",
                PermissionClearance.Independent),
            new AdminWildcardResourceGrantDescriptor(
                "documents",
                PermissionClearance.Independent));
    }

    [Test]
    public void BuildReconcilePlan_AddsMissingAndUpgradesNonIndependentGrants()
    {
        var plan = _engine.BuildReconcilePlan(
            ["CanRunShell", "CanEditFiles"],
            ["shell", "documents"],
            [
                new AdminGlobalFlagGrantFact(
                    "CanRunShell",
                    PermissionClearance.Unset)
            ],
            [
                new AdminWildcardResourceGrantFact(
                    "shell",
                    PermissionClearance.ApprovedBySameLevelUser)
            ]);

        plan.HasChanges.Should().BeTrue();
        plan.MissingGlobalFlags.Should().ContainSingle()
            .Which.Should().Be(new AdminGlobalFlagGrantDescriptor(
                "CanEditFiles",
                PermissionClearance.Independent));
        plan.GlobalFlagUpdates.Should().ContainSingle()
            .Which.Should().Be(new AdminGlobalFlagGrantUpdate(
                "CanRunShell",
                PermissionClearance.Independent));
        plan.MissingWildcardResources.Should().ContainSingle()
            .Which.Should().Be(new AdminWildcardResourceGrantDescriptor(
                "documents",
                PermissionClearance.Independent));
        plan.WildcardResourceUpdates.Should().ContainSingle()
            .Which.Should().Be(new AdminWildcardResourceGrantUpdate(
                "shell",
                PermissionClearance.Independent));
    }

    [Test]
    public void BuildReconcilePlan_WhenAdminGrantsAlreadyMatch_ReturnsNoChanges()
    {
        var plan = _engine.BuildReconcilePlan(
            ["CanRunShell"],
            ["shell"],
            [
                new AdminGlobalFlagGrantFact(
                    "CanRunShell",
                    PermissionClearance.Independent)
            ],
            [
                new AdminWildcardResourceGrantFact(
                    "shell",
                    PermissionClearance.Independent)
            ]);

        plan.HasChanges.Should().BeFalse();
        plan.MissingGlobalFlags.Should().BeEmpty();
        plan.GlobalFlagUpdates.Should().BeEmpty();
        plan.MissingWildcardResources.Should().BeEmpty();
        plan.WildcardResourceUpdates.Should().BeEmpty();
    }

    [Test]
    public void BuildReconcilePlan_PreservesUnregisteredExistingFacts()
    {
        var plan = _engine.BuildReconcilePlan(
            ["CanRunShell"],
            ["shell"],
            [
                new AdminGlobalFlagGrantFact(
                    "CanRunShell",
                    PermissionClearance.Independent),
                new AdminGlobalFlagGrantFact(
                    "CanLegacy",
                    PermissionClearance.Restricted)
            ],
            [
                new AdminWildcardResourceGrantFact(
                    "shell",
                    PermissionClearance.Independent),
                new AdminWildcardResourceGrantFact(
                    "legacy",
                    PermissionClearance.Restricted)
            ]);

        plan.HasChanges.Should().BeFalse();
    }
}
