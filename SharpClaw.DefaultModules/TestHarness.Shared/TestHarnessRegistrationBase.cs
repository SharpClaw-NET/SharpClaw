using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.Kernel;
using SharpClaw.Contracts.Providers;
using SharpClaw.ModuleSDK;

namespace SharpClaw.DefaultPackages.TestHarness;


#if TEST_HARNESS_OUT_OF_PROCESS
public sealed class TestHarnessOutOfProcessRegistration()
    : TestHarnessRegistrationBase(TestHarnessConstants.OutOfProcessRegistrationId, "Test Harness Out Of Process");
#endif

#if TEST_HARNESS_IN_PROCESS
public sealed class TestHarnessInProcessRegistration()
    : TestHarnessRegistrationBase(TestHarnessConstants.InProcessRegistrationId, "Test Harness In Process");
#endif

/// <summary>Provides deterministic provider and direct-tool behavior for host tests.</summary>
public abstract class TestHarnessRegistrationBase(string SourceId, string displayName) : ISharpClawModule
{
    public ModuleIdentity Identity { get; } = new(SourceId, displayName, TestHarnessConstants.ToolPrefix);

    public void ConfigureServices(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<TestHarnessState>();
        AddProvider(services, TestHarnessConstants.PlainProviderKey, "SharpClaw Test Harness", false);
        AddProvider(services, TestHarnessConstants.StreamingProviderKey, "SharpClaw Test Harness Streaming", true);
        AddProvider(services, TestHarnessConstants.ToolProviderKey, "SharpClaw Test Harness Tools", true);
        AddProvider(services, TestHarnessConstants.FailingProviderKey, "SharpClaw Test Harness Failure", true);
        AddProvider(services, TestHarnessConstants.CostProviderKey, "SharpClaw Test Harness Cost", true);
        AddProvider(services, TestHarnessConstants.EdenStyleProviderKey, "SharpClaw Test Harness EdenAI", true);

        foreach (var descriptor in ToolDescriptors())
            services.AddTool<TestHarnessToolHandler>(descriptor);
#if TEST_HARNESS_IN_PROCESS
        services.AddCliCommand<TestHarnessScopedCliHandler>(new CliCommandDescriptor(
            TestHarnessConstants.ScopedCliCommand,
            [],
            "Reports scoped command execution.",
            new JsonSchemaReference("test-harness.scope.input", 1, "test-harness-scope-input"),
            new JsonSchemaReference("test-harness.scope.result", 1, "test-harness-scope-result")));
#endif
    }

    public int PermissionDescriptorBuilds => 0;

    public void ResetDiagnostics()
    {
    }

    private void AddProvider(
        IServiceCollection services,
        string providerKey,
        string displayName,
        bool supportsNativeToolCalling) =>
        services.AddSingleton<IProviderPlugin>(sp => new TestHarnessProviderPlugin(
            ownerId: Identity.Id,
            providerKey,
            displayName,
            supportsNativeToolCalling,
            sp.GetRequiredService<TestHarnessState>()));

    private static IEnumerable<ToolDescriptor> ToolDescriptors()
    {
        var schema = ToolSchema();
        yield return new(
            TestHarnessConstants.InlineOpenTool,
            "Deterministic direct tool for host pipeline tests.",
            schema);
        yield return new(
            TestHarnessConstants.ControlTool,
            "Configure deterministic test harness behavior.",
            schema);
        yield return new(
            TestHarnessConstants.SnapshotTool,
            "Read deterministic test harness observations.",
            schema);
        yield return new(
            TestHarnessConstants.JobPermissionedTool,
            "Deterministic tool for host pipeline tests.",
            schema);
        yield return new(
            TestHarnessConstants.JobResourceTool,
            "Deterministic resource tool for host pipeline tests.",
            schema);
        yield return new(
            TestHarnessConstants.JobStreamingTool,
            "Deterministic streaming tool for host pipeline tests.",
            schema);
    }

    private static JsonElement ToolSchema()
    {
        using var document = JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "latencyMs": { "type": "integer" },
                "payloadBytes": { "type": "integer" },
                "fail": { "type": "boolean" },
                "result": { "type": "string" }
              },
              "additionalProperties": false
            }
            """);
        return document.RootElement.Clone();
    }
}
