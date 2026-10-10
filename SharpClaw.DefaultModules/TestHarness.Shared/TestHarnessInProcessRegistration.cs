namespace SharpClaw.DefaultPackages.TestHarness;

#if TEST_HARNESS_IN_PROCESS
public sealed class TestHarnessInProcessRegistration()
    : TestHarnessRegistrationBase(TestHarnessConstants.InProcessRegistrationId, "Test Harness In Process");
#endif
