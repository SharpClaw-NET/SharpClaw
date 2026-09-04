using SharpClaw.Core.Modules;

namespace SharpClaw.Tests.Core;

[TestFixture]
public sealed class RegistrationDisableDependencyEngineTests
{
    [Test]
    public void Evaluate_WhenTargetExportsNoContracts_AllowsDisable()
    {
        var decision = new RegistrationDisableDependencyEngine().Evaluate(
            new RegistrationDisableDependencyFacts(
                SourceId: "target_registration",
                ExportedContractNames: [],
                OtherRegistrations:
                [
                    new RegistrationDisableDependencyCandidateFacts(
                        "dependent_registration",
                        [
                            new RegistrationDisableDependencyRequirementFacts(
                                "registration_contract",
                                Optional: false)
                        ])
                ]));

        typeof(RegistrationDisableDependencyEngine).Assembly.GetName().Name
            .Should().Be("SharpClaw.Core");
        decision.CanDisable.Should().BeTrue();
        decision.BlockerRegistrationId.Should().BeNull();
        decision.BlockingContracts.Should().BeEmpty();
    }

    [Test]
    public void Evaluate_WhenRequiredContractMatches_BlocksWithDecisionData()
    {
        var decision = new RegistrationDisableDependencyEngine().Evaluate(
            new RegistrationDisableDependencyFacts(
                SourceId: "target_registration",
                ExportedContractNames: ["registration_contract"],
                OtherRegistrations:
                [
                    new RegistrationDisableDependencyCandidateFacts(
                        "dependent_registration",
                        [
                            new RegistrationDisableDependencyRequirementFacts(
                                "registration_contract",
                                Optional: false)
                        ])
                ]));

        decision.CanDisable.Should().BeFalse();
        decision.SourceId.Should().Be("target_registration");
        decision.BlockerRegistrationId.Should().Be("dependent_registration");
        decision.BlockingContracts.Should().Equal("registration_contract");
    }

    [Test]
    public void Evaluate_IgnoresOptionalRequirements()
    {
        var decision = new RegistrationDisableDependencyEngine().Evaluate(
            new RegistrationDisableDependencyFacts(
                SourceId: "target_registration",
                ExportedContractNames: ["optional_contract"],
                OtherRegistrations:
                [
                    new RegistrationDisableDependencyCandidateFacts(
                        "dependent_registration",
                        [
                            new RegistrationDisableDependencyRequirementFacts(
                                "optional_contract",
                                Optional: true)
                        ])
                ]));

        decision.CanDisable.Should().BeTrue();
    }

    [Test]
    public void Evaluate_IgnoresSelfDependencyForTargetRegistration()
    {
        var decision = new RegistrationDisableDependencyEngine().Evaluate(
            new RegistrationDisableDependencyFacts(
                SourceId: "target_registration",
                ExportedContractNames: ["registration_contract"],
                OtherRegistrations:
                [
                    new RegistrationDisableDependencyCandidateFacts(
                        "target_registration",
                        [
                            new RegistrationDisableDependencyRequirementFacts(
                                "registration_contract",
                                Optional: false)
                        ])
                ]));

        decision.CanDisable.Should().BeTrue();
    }

    [Test]
    public void Evaluate_PreservesFirstBlockerAndRequirementOrderAndDuplicates()
    {
        var decision = new RegistrationDisableDependencyEngine().Evaluate(
            new RegistrationDisableDependencyFacts(
                SourceId: "target_registration",
                ExportedContractNames: ["contract_a", "contract_b"],
                OtherRegistrations:
                [
                    new RegistrationDisableDependencyCandidateFacts(
                        "first_blocker",
                        [
                            new RegistrationDisableDependencyRequirementFacts(
                                "contract_b",
                                Optional: false),
                            new RegistrationDisableDependencyRequirementFacts(
                                "contract_a",
                                Optional: false),
                            new RegistrationDisableDependencyRequirementFacts(
                                "contract_b",
                                Optional: false)
                        ]),
                    new RegistrationDisableDependencyCandidateFacts(
                        "second_blocker",
                        [
                            new RegistrationDisableDependencyRequirementFacts(
                                "contract_a",
                                Optional: false)
                        ])
                ]));

        decision.CanDisable.Should().BeFalse();
        decision.BlockerRegistrationId.Should().Be("first_blocker");
        decision.BlockingContracts.Should().Equal(
            "contract_b",
            "contract_a",
            "contract_b");
    }

    [Test]
    public void Evaluate_TreatsProtocolContractsAsCollectedContractFacts()
    {
        var decision = new RegistrationDisableDependencyEngine().Evaluate(
            new RegistrationDisableDependencyFacts(
                SourceId: "target_registration",
                ExportedContractNames: ["protocol_contract"],
                OtherRegistrations:
                [
                    new RegistrationDisableDependencyCandidateFacts(
                        "protocol_consumer",
                        [
                            new RegistrationDisableDependencyRequirementFacts(
                                "protocol_contract",
                                Optional: false)
                        ])
                ]));

        decision.CanDisable.Should().BeFalse();
        decision.BlockerRegistrationId.Should().Be("protocol_consumer");
        decision.BlockingContracts.Should().Equal("protocol_contract");
    }
}
