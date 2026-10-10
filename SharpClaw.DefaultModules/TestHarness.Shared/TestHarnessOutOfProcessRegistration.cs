namespace SharpClaw.DefaultPackages.TestHarness;

#if TEST_HARNESS_OUT_OF_PROCESS
public sealed class TestHarnessOutOfProcessRegistration()
    : TestHarnessRegistrationBase(TestHarnessConstants.OutOfProcessRegistrationId, "Test Harness Out Of Process");
#endif
