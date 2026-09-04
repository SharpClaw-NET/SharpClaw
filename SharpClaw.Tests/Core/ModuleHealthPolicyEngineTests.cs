using SharpClaw.Contracts.Kernel;
using SharpClaw.Core.Modules;

namespace SharpClaw.Tests.Core;

[TestFixture]
public sealed class RegistrationHealthPolicyEngineTests
{
    [Test]
    public void EvaluateStatus_HealthyStatusResetsConsecutiveFailures()
    {
        var engine = new RegistrationHealthPolicyEngine();

        var decision = engine.EvaluateStatus(
            previousConsecutiveFailures: 2,
            failureThreshold: 3,
            new PackageHealthStatus(IsHealthy: true));

        typeof(RegistrationHealthPolicyEngine).Assembly.GetName().Name
            .Should().Be("SharpClaw.Core");
        decision.ConsecutiveFailureCount.Should().Be(0);
        decision.EffectiveFailureThreshold.Should().Be(3);
        decision.IsFailure.Should().BeFalse();
        decision.ShouldResetFailureCount.Should().BeTrue();
        decision.ShouldAutoDisable.Should().BeFalse();
    }

    [Test]
    public void Evaluate_SkippedObservationPreservesFailuresWithoutDisabling()
    {
        var engine = new RegistrationHealthPolicyEngine();

        var decision = engine.Evaluate(new RegistrationHealthPolicyInput(
            PreviousConsecutiveFailures: 2,
            FailureThreshold: 3,
            ResultKind: RegistrationHealthProbeResultKind.Skipped));

        decision.ConsecutiveFailureCount.Should().Be(2);
        decision.EffectiveFailureThreshold.Should().Be(3);
        decision.IsFailure.Should().BeFalse();
        decision.ShouldResetFailureCount.Should().BeFalse();
        decision.ShouldAutoDisable.Should().BeFalse();
    }

    [Test]
    public void EvaluateStatus_UnhealthyStatusIncrementsFailuresBelowThreshold()
    {
        var engine = new RegistrationHealthPolicyEngine();

        var decision = engine.EvaluateStatus(
            previousConsecutiveFailures: 1,
            failureThreshold: 3,
            new PackageHealthStatus(IsHealthy: false, Message: "not ready"));

        decision.ConsecutiveFailureCount.Should().Be(2);
        decision.EffectiveFailureThreshold.Should().Be(3);
        decision.IsFailure.Should().BeTrue();
        decision.ShouldResetFailureCount.Should().BeFalse();
        decision.ShouldAutoDisable.Should().BeFalse();
    }

    [Test]
    public void EvaluateStatus_UnhealthyStatusAtThresholdRequestsAutoDisable()
    {
        var engine = new RegistrationHealthPolicyEngine();

        var decision = engine.EvaluateStatus(
            previousConsecutiveFailures: 2,
            failureThreshold: 3,
            new PackageHealthStatus(IsHealthy: false, Message: "still failing"));

        decision.ConsecutiveFailureCount.Should().Be(3);
        decision.EffectiveFailureThreshold.Should().Be(3);
        decision.IsFailure.Should().BeTrue();
        decision.ShouldResetFailureCount.Should().BeFalse();
        decision.ShouldAutoDisable.Should().BeTrue();
    }

    [Test]
    public void EvaluateStatus_NonPositiveThresholdRequestsDisableOnFirstFailure()
    {
        var engine = new RegistrationHealthPolicyEngine();

        var decision = engine.EvaluateStatus(
            previousConsecutiveFailures: 0,
            failureThreshold: 0,
            new PackageHealthStatus(IsHealthy: false, Message: "failed"));

        decision.ConsecutiveFailureCount.Should().Be(1);
        decision.EffectiveFailureThreshold.Should().Be(1);
        decision.IsFailure.Should().BeTrue();
        decision.ShouldResetFailureCount.Should().BeFalse();
        decision.ShouldAutoDisable.Should().BeTrue();
    }
}
