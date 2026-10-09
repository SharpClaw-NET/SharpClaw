using System.Text.Json;

using SharpClaw.Contracts.Kernel;
using SharpClaw.Contracts.Providers;

namespace SharpClaw.DefaultPackages.TestHarness;


public static class TestHarnessConstants
{
    public const string OutOfProcessRegistrationId = "sharpclaw_test_harness_out_of_process";
    public const string InProcessRegistrationId = "sharpclaw_test_harness_in_process";
    public const string SourceId = OutOfProcessRegistrationId;
    public const string ToolPrefix = "th";
#if TEST_HARNESS_IN_PROCESS
    public const string ScopedCliCommand = "test-harness-scope";
#endif

    public const string PlainProviderKey = "sharpclaw-test";
    public const string StreamingProviderKey = "sharpclaw-test-stream";
    public const string ToolProviderKey = "sharpclaw-test-tools";
    public const string FailingProviderKey = "sharpclaw-test-failing";
    public const string CostProviderKey = "sharpclaw-test-cost";
    public const string EdenStyleProviderKey = "sharpclaw-test-edenai";

    public const string ModelId = "test-harness-model";
    public const string GlobalFlagKey = "CanUseTestHarnessTools";
    public const string DelegateName = "UseTestHarnessToolAsync";
    public const string ResourceType = "SharpClaw.TestHarness.Resource";
    public const string ResourceGrantLabel = "TestHarnessResource";
    public const string ResourceDelegateName = "UseTestHarnessResourceAsync";
    public const string DefaultResourceKey = "test_harness_resource";

    public const string HeaderTagName = "testharness";
    public const string InlineOpenTool = "test_harness_inline_open";
    public const string InlinePermissionedTool = "test_harness_inline_permissioned";
    public const string InlinePermissionedToolAlias = "test_harness_inline_permissioned_alias";
    public const string ControlTool = "test_harness_control";
    public const string SnapshotTool = "test_harness_snapshot";
    public const string JobPermissionedTool = "test_harness_job_permissioned";
    public const string JobPermissionedToolAlias = "test_harness_job_permissioned_alias";
    public const string JobResourceTool = "test_harness_job_resource";
    public const string JobStreamingTool = "test_harness_job_streaming";
}
