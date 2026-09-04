using SharpClaw.Contracts.Kernel;
using SharpClaw.Core.Modules;

namespace SharpClaw.Tests.Core;

[TestFixture]
public sealed class RegistrationLifecycleProjectionEngineTests
{
    [Test]
    public void ProjectState_UsesManifestVersionAndExternalRegistrationRules()
    {
        var engine = new RegistrationLifecycleProjectionEngine();
        var createdAt = DateTimeOffset.Parse("2026-07-02T10:00:00Z");
        var updatedAt = DateTimeOffset.Parse("2026-07-02T11:00:00Z");

        var response = engine.ProjectState(new RegistrationLifecycleStateFacts(
            SourceId: "sample",
            DisplayName: "Sample",
            ToolPrefix: "sam",
            IsExternal: true,
            HasPersistedState: false,
            StateEnabled: false,
            StateVersion: "0.9.0",
            CreatedAt: createdAt,
            UpdatedAt: updatedAt,
            ManifestVersion: "1.0.0"));

        typeof(RegistrationLifecycleProjectionEngine).Assembly.GetName().Name
            .Should().Be("SharpClaw.Core");
        typeof(RegistrationStateResponse).Assembly.GetName().Name
            .Should().Be("SharpClaw.Contracts");
        response.Enabled.Should().BeTrue();
        response.Registered.Should().BeTrue();
        response.Version.Should().Be("1.0.0");
        response.CreatedAt.Should().Be(createdAt);
        response.UpdatedAt.Should().Be(updatedAt);
    }

    [Test]
    public void ProjectState_UsesPersistedStateForBundledRegistrations()
    {
        var engine = new RegistrationLifecycleProjectionEngine();

        var response = engine.ProjectState(new RegistrationLifecycleStateFacts(
            SourceId: "bundled",
            DisplayName: "Bundled",
            ToolPrefix: "bun",
            IsExternal: false,
            HasPersistedState: true,
            StateEnabled: true,
            StateVersion: "0.8.0",
            CreatedAt: null,
            UpdatedAt: null,
            ManifestVersion: null));

        response.Enabled.Should().BeTrue();
        response.Registered.Should().BeTrue();
        response.Version.Should().Be("0.8.0");
    }

    [Test]
    public void ProjectDetail_ProjectsCountsContractsAndSatisfiedRequirements()
    {
        var engine = new RegistrationLifecycleProjectionEngine();

        var detail = engine.ProjectDetail(new RegistrationLifecycleDetailFacts(
            State: new RegistrationLifecycleStateFacts(
                SourceId: "detail",
                DisplayName: "Detail",
                ToolPrefix: "det",
                IsExternal: false,
                HasPersistedState: true,
                StateEnabled: true,
                StateVersion: "1.0.0",
                CreatedAt: null,
                UpdatedAt: null,
                ManifestVersion: "1.1.0"),
            Author: "SharpClaw",
            Description: "Projection test",
            License: "MIT",
            Platforms: ["win-x64"],
            ExecutionTimeoutSeconds: 60,
            ToolCount: 3,
            InlineToolCount: 2,
            ExportedContractNames: ["module.export", "protocol.export", "module.export"],
            RequiredContracts:
            [
                new RegistrationLifecycleRequirementFacts("module.required", Optional: false, IsSatisfied: true),
                new RegistrationLifecycleRequirementFacts("protocol.optional", Optional: true, IsSatisfied: false),
                new RegistrationLifecycleRequirementFacts("module.required", Optional: false, IsSatisfied: true)
            ]));

        typeof(PackageDetailResponse).Assembly.GetName().Name
            .Should().Be("SharpClaw.Contracts");
        detail.ExecutionTimeoutSeconds.Should().Be(60);
        detail.ToolCount.Should().Be(3);
        detail.InlineToolCount.Should().Be(2);
        detail.ExportedContracts.Should().Equal(
            "module.export",
            "protocol.export",
            "module.export");
        detail.RequiredContracts.Should().Equal(
            "module.required",
            "protocol.optional",
            "module.required");
        detail.AllRequirementsSatisfied.Should().BeTrue();
    }

    [Test]
    public void ProjectDetail_PreservesExplicitTimeoutFacts()
    {
        var engine = new RegistrationLifecycleProjectionEngine();

        var zero = engine.ProjectDetail(CreateDetailFacts(timeout: 0));
        var negative = engine.ProjectDetail(CreateDetailFacts(timeout: -5));

        zero.ExecutionTimeoutSeconds.Should().Be(0);
        negative.ExecutionTimeoutSeconds.Should().Be(-5);
    }

    [Test]
    public void ProjectDetail_FailsRequirementsWhenNonOptionalRequirementIsMissing()
    {
        var engine = new RegistrationLifecycleProjectionEngine();

        var detail = engine.ProjectDetail(new RegistrationLifecycleDetailFacts(
            State: new RegistrationLifecycleStateFacts(
                SourceId: "detail",
                DisplayName: "Detail",
                ToolPrefix: "det",
                IsExternal: false,
                HasPersistedState: true,
                StateEnabled: true,
                StateVersion: null,
                CreatedAt: null,
                UpdatedAt: null,
                ManifestVersion: null),
            Author: null,
            Description: null,
            License: null,
            Platforms: null,
            ExecutionTimeoutSeconds: 30,
            ToolCount: 0,
            InlineToolCount: 0,
            ExportedContractNames: [],
            RequiredContracts:
            [
                new RegistrationLifecycleRequirementFacts("required.missing", Optional: false, IsSatisfied: false)
            ]));

        detail.ExecutionTimeoutSeconds.Should().Be(30);
        detail.AllRequirementsSatisfied.Should().BeFalse();
    }

    private static RegistrationLifecycleDetailFacts CreateDetailFacts(int timeout) =>
        new(
            State: new RegistrationLifecycleStateFacts(
                SourceId: "timeout",
                DisplayName: "Timeout",
                ToolPrefix: "tim",
                IsExternal: false,
                HasPersistedState: true,
                StateEnabled: true,
                StateVersion: null,
                CreatedAt: null,
                UpdatedAt: null,
                ManifestVersion: null),
            Author: null,
            Description: null,
            License: null,
            Platforms: null,
            ExecutionTimeoutSeconds: timeout,
            ToolCount: 0,
            InlineToolCount: 0,
            ExportedContractNames: [],
            RequiredContracts: []);
}
