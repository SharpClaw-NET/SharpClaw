using SharpClaw.Contracts.Modules;
using SharpClaw.Core.Modules;

namespace SharpClaw.Tests.Core;

[TestFixture]
public sealed class ModuleHealthPolicyEngineTests
{
    [Test]
    public void EvaluateStatus_HealthyStatusResetsConsecutiveFailures()
    {
        var engine = new ModuleHealthPolicyEngine();

        var decision = engine.EvaluateStatus(
            previousConsecutiveFailures: 2,
            failureThreshold: 3,
            new ModuleHealthStatus(IsHealthy: true));

        typeof(ModuleHealthPolicyEngine).Assembly.GetName().Name
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
        var engine = new ModuleHealthPolicyEngine();

        var decision = engine.Evaluate(new ModuleHealthPolicyInput(
            PreviousConsecutiveFailures: 2,
            FailureThreshold: 3,
            ResultKind: ModuleHealthProbeResultKind.Skipped));

        decision.ConsecutiveFailureCount.Should().Be(2);
        decision.EffectiveFailureThreshold.Should().Be(3);
        decision.IsFailure.Should().BeFalse();
        decision.ShouldResetFailureCount.Should().BeFalse();
        decision.ShouldAutoDisable.Should().BeFalse();
    }

    [Test]
    public void EvaluateStatus_UnhealthyStatusIncrementsFailuresBelowThreshold()
    {
        var engine = new ModuleHealthPolicyEngine();

        var decision = engine.EvaluateStatus(
            previousConsecutiveFailures: 1,
            failureThreshold: 3,
            new ModuleHealthStatus(IsHealthy: false, Message: "not ready"));

        decision.ConsecutiveFailureCount.Should().Be(2);
        decision.EffectiveFailureThreshold.Should().Be(3);
        decision.IsFailure.Should().BeTrue();
        decision.ShouldResetFailureCount.Should().BeFalse();
        decision.ShouldAutoDisable.Should().BeFalse();
    }

    [Test]
    public void EvaluateStatus_UnhealthyStatusAtThresholdRequestsAutoDisable()
    {
        var engine = new ModuleHealthPolicyEngine();

        var decision = engine.EvaluateStatus(
            previousConsecutiveFailures: 2,
            failureThreshold: 3,
            new ModuleHealthStatus(IsHealthy: false, Message: "still failing"));

        decision.ConsecutiveFailureCount.Should().Be(3);
        decision.EffectiveFailureThreshold.Should().Be(3);
        decision.IsFailure.Should().BeTrue();
        decision.ShouldResetFailureCount.Should().BeFalse();
        decision.ShouldAutoDisable.Should().BeTrue();
    }

    [Test]
    public void EvaluateStatus_NonPositiveThresholdRequestsDisableOnFirstFailure()
    {
        var engine = new ModuleHealthPolicyEngine();

        var decision = engine.EvaluateStatus(
            previousConsecutiveFailures: 0,
            failureThreshold: 0,
            new ModuleHealthStatus(IsHealthy: false, Message: "failed"));

        decision.ConsecutiveFailureCount.Should().Be(1);
        decision.EffectiveFailureThreshold.Should().Be(1);
        decision.IsFailure.Should().BeTrue();
        decision.ShouldResetFailureCount.Should().BeFalse();
        decision.ShouldAutoDisable.Should().BeTrue();
    }
}
