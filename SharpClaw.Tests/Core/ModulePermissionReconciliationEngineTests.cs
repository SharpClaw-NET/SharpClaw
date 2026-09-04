using SharpClaw.Contracts.Enums;
using SharpClaw.Core.Permissions;

namespace SharpClaw.Tests.Core;

[TestFixture]
public sealed class RegistrationPermissionReconciliationEngineTests
{
    private readonly RegistrationPermissionReconciliationEngine _engine = new();

    [Test]
    public void BuildPlan_AddsMissingRegistrationGrantsToWildcardPermissionSets()
    {
        var permissionSetId = Guid.NewGuid();

        var plan = _engine.BuildPlan(
            ["existing_flag", "new_flag"],
            ["existing_resource", "new_resource"],
            [
                new RegistrationPermissionSetReconciliationFact(
                    permissionSetId,
                    ["existing_flag"],
                    ["existing_resource"])
            ]);

        plan.HasChanges.Should().BeTrue();
        plan.PermissionSets.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(
                new RegistrationPermissionSetReconciliationPlan(
                    permissionSetId,
                    [
                        new RegistrationWildcardResourceGrantDescriptor(
                            "new_resource",
                            PermissionClearance.Independent)
                    ],
                    [
                        new RegistrationGlobalFlagGrantDescriptor(
                            "new_flag",
                            PermissionClearance.Independent)
                    ]));
    }

    [Test]
    public void BuildPlan_SkipsPermissionSetsWithoutWildcardResourceGrants()
    {
        var plan = _engine.BuildPlan(
            ["new_flag"],
            ["new_resource"],
            [
                new RegistrationPermissionSetReconciliationFact(
                    Guid.NewGuid(),
                    [],
                    [])
            ]);

        plan.HasChanges.Should().BeFalse();
        plan.PermissionSets.Should().BeEmpty();
    }

    [Test]
    public void BuildPlan_WhenAllRegistrationGrantsAlreadyExist_ReturnsNoChanges()
    {
        var plan = _engine.BuildPlan(
            ["existing_flag"],
            ["existing_resource"],
            [
                new RegistrationPermissionSetReconciliationFact(
                    Guid.NewGuid(),
                    ["existing_flag"],
                    ["existing_resource"])
            ]);

        plan.HasChanges.Should().BeFalse();
        plan.PermissionSets.Should().BeEmpty();
    }

    [Test]
    public void BuildPlan_DeduplicatesRegistrationKeysAgainstPlannedAdditions()
    {
        var permissionSetId = Guid.NewGuid();

        var plan = _engine.BuildPlan(
            ["new_flag", "new_flag"],
            ["new_resource", "new_resource"],
            [
                new RegistrationPermissionSetReconciliationFact(
                    permissionSetId,
                    [],
                    ["seed_resource"])
            ]);

        var permissionSetPlan = plan.PermissionSets.Should()
            .ContainSingle()
            .Subject;
        permissionSetPlan.MissingGlobalFlags.Should().Equal(
            new RegistrationGlobalFlagGrantDescriptor(
                "new_flag",
                PermissionClearance.Independent));
        permissionSetPlan.MissingWildcardResources.Should().Equal(
            new RegistrationWildcardResourceGrantDescriptor(
                "new_resource",
                PermissionClearance.Independent));
    }
}
