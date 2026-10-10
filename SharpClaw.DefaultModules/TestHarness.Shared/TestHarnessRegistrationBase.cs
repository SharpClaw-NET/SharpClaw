using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.Kernel;
using SharpClaw.Contracts.Providers;
using SharpClaw.ModuleSDK;

namespace SharpClaw.DefaultPackages.TestHarness;




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

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822",
        Justification = "The test harness exposes diagnostics through this existing instance API; making it static would break consumers.")]
    public int PermissionDescriptorBuilds => 0;

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822",
        Justification = "Existing harness consumers invoke the per-registration diagnostic reset as an instance operation.")]
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
        using var document = JsonDocument.Parse("{\n  \"type\": \"object\",\n  \"properties\": {\n    \"latencyMs\": { \"type\": \"integer\" },\n    \"payloadBytes\": { \"type\": \"integer\" },\n    \"fail\": { \"type\": \"boolean\" },\n    \"result\": { \"type\": \"string\" }\n  },\n  \"additionalProperties\": false\n}");
        return document.RootElement.Clone();
    }
}
