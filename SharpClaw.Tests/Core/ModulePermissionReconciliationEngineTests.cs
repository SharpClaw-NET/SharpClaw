using SharpClaw.Contracts.Enums;
using SharpClaw.Core.Permissions;

namespace SharpClaw.Tests.Core;

[TestFixture]
public sealed class ModulePermissionReconciliationEngineTests
{
    private readonly ModulePermissionReconciliationEngine _engine = new();

    [Test]
    public void BuildPlan_AddsMissingModuleGrantsToWildcardPermissionSets()
    {
        var permissionSetId = Guid.NewGuid();

        var plan = _engine.BuildPlan(
            ["existing_flag", "new_flag"],
            ["existing_resource", "new_resource"],
            [
                new ModulePermissionSetReconciliationFact(
                    permissionSetId,
                    ["existing_flag"],
                    ["existing_resource"])
            ]);

        plan.HasChanges.Should().BeTrue();
        plan.PermissionSets.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(
                new ModulePermissionSetReconciliationPlan(
                    permissionSetId,
                    [
                        new ModuleWildcardResourceGrantDescriptor(
                            "new_resource",
                            PermissionClearance.Independent)
                    ],
                    [
                        new ModuleGlobalFlagGrantDescriptor(
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
                new ModulePermissionSetReconciliationFact(
                    Guid.NewGuid(),
                    [],
                    [])
            ]);

        plan.HasChanges.Should().BeFalse();
        plan.PermissionSets.Should().BeEmpty();
    }

    [Test]
    public void BuildPlan_WhenAllModuleGrantsAlreadyExist_ReturnsNoChanges()
    {
        var plan = _engine.BuildPlan(
            ["existing_flag"],
            ["existing_resource"],
            [
                new ModulePermissionSetReconciliationFact(
                    Guid.NewGuid(),
                    ["existing_flag"],
                    ["existing_resource"])
            ]);

        plan.HasChanges.Should().BeFalse();
        plan.PermissionSets.Should().BeEmpty();
    }

    [Test]
    public void BuildPlan_DeduplicatesModuleKeysAgainstPlannedAdditions()
    {
        var permissionSetId = Guid.NewGuid();

        var plan = _engine.BuildPlan(
            ["new_flag", "new_flag"],
            ["new_resource", "new_resource"],
            [
                new ModulePermissionSetReconciliationFact(
                    permissionSetId,
                    [],
                    ["seed_resource"])
            ]);

        var permissionSetPlan = plan.PermissionSets.Should()
            .ContainSingle()
            .Subject;
        permissionSetPlan.MissingGlobalFlags.Should().Equal(
            new ModuleGlobalFlagGrantDescriptor(
                "new_flag",
                PermissionClearance.Independent));
        permissionSetPlan.MissingWildcardResources.Should().Equal(
            new ModuleWildcardResourceGrantDescriptor(
                "new_resource",
                PermissionClearance.Independent));
    }
}
