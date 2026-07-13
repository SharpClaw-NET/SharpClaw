using SharpClaw.Core.Modules;

namespace SharpClaw.Tests.Core;

[TestFixture]
public sealed class ModuleDisableDependencyEngineTests
{
    [Test]
    public void Evaluate_WhenTargetExportsNoContracts_AllowsDisable()
    {
        var decision = new ModuleDisableDependencyEngine().Evaluate(
            new ModuleDisableDependencyFacts(
                ModuleId: "target_module",
                ExportedContractNames: [],
                OtherModules:
                [
                    new ModuleDisableDependencyCandidateFacts(
                        "dependent_module",
                        [
                            new ModuleDisableDependencyRequirementFacts(
                                "module_contract",
                                Optional: false)
                        ])
                ]));

        typeof(ModuleDisableDependencyEngine).Assembly.GetName().Name
            .Should().Be("SharpClaw.Core");
        decision.CanDisable.Should().BeTrue();
        decision.BlockerModuleId.Should().BeNull();
        decision.BlockingContracts.Should().BeEmpty();
    }

    [Test]
    public void Evaluate_WhenRequiredModuleContractMatches_BlocksWithDecisionData()
    {
        var decision = new ModuleDisableDependencyEngine().Evaluate(
            new ModuleDisableDependencyFacts(
                ModuleId: "target_module",
                ExportedContractNames: ["module_contract"],
                OtherModules:
                [
                    new ModuleDisableDependencyCandidateFacts(
                        "dependent_module",
                        [
                            new ModuleDisableDependencyRequirementFacts(
                                "module_contract",
                                Optional: false)
                        ])
                ]));

        decision.CanDisable.Should().BeFalse();
        decision.ModuleId.Should().Be("target_module");
        decision.BlockerModuleId.Should().Be("dependent_module");
        decision.BlockingContracts.Should().Equal("module_contract");
    }

    [Test]
    public void Evaluate_IgnoresOptionalRequirements()
    {
        var decision = new ModuleDisableDependencyEngine().Evaluate(
            new ModuleDisableDependencyFacts(
                ModuleId: "target_module",
                ExportedContractNames: ["optional_contract"],
                OtherModules:
                [
                    new ModuleDisableDependencyCandidateFacts(
                        "dependent_module",
                        [
                            new ModuleDisableDependencyRequirementFacts(
                                "optional_contract",
                                Optional: true)
                        ])
                ]));

        decision.CanDisable.Should().BeTrue();
    }

    [Test]
    public void Evaluate_IgnoresSelfDependencyForTargetModule()
    {
        var decision = new ModuleDisableDependencyEngine().Evaluate(
            new ModuleDisableDependencyFacts(
                ModuleId: "target_module",
                ExportedContractNames: ["module_contract"],
                OtherModules:
                [
                    new ModuleDisableDependencyCandidateFacts(
                        "target_module",
                        [
                            new ModuleDisableDependencyRequirementFacts(
                                "module_contract",
                                Optional: false)
                        ])
                ]));

        decision.CanDisable.Should().BeTrue();
    }

    [Test]
    public void Evaluate_PreservesFirstBlockerAndRequirementOrderAndDuplicates()
    {
        var decision = new ModuleDisableDependencyEngine().Evaluate(
            new ModuleDisableDependencyFacts(
                ModuleId: "target_module",
                ExportedContractNames: ["contract_a", "contract_b"],
                OtherModules:
                [
                    new ModuleDisableDependencyCandidateFacts(
                        "first_blocker",
                        [
                            new ModuleDisableDependencyRequirementFacts(
                                "contract_b",
                                Optional: false),
                            new ModuleDisableDependencyRequirementFacts(
                                "contract_a",
                                Optional: false),
                            new ModuleDisableDependencyRequirementFacts(
                                "contract_b",
                                Optional: false)
                        ]),
                    new ModuleDisableDependencyCandidateFacts(
                        "second_blocker",
                        [
                            new ModuleDisableDependencyRequirementFacts(
                                "contract_a",
                                Optional: false)
                        ])
                ]));

        decision.CanDisable.Should().BeFalse();
        decision.BlockerModuleId.Should().Be("first_blocker");
        decision.BlockingContracts.Should().Equal(
            "contract_b",
            "contract_a",
            "contract_b");
    }

    [Test]
    public void Evaluate_TreatsProtocolContractsAsCollectedContractFacts()
    {
        var decision = new ModuleDisableDependencyEngine().Evaluate(
            new ModuleDisableDependencyFacts(
                ModuleId: "target_module",
                ExportedContractNames: ["protocol_contract"],
                OtherModules:
                [
                    new ModuleDisableDependencyCandidateFacts(
                        "protocol_consumer",
                        [
                            new ModuleDisableDependencyRequirementFacts(
                                "protocol_contract",
                                Optional: false)
                        ])
                ]));

        decision.CanDisable.Should().BeFalse();
        decision.BlockerModuleId.Should().Be("protocol_consumer");
        decision.BlockingContracts.Should().Equal("protocol_contract");
    }
}
