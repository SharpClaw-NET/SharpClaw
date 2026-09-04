using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.Kernel;
using SharpClaw.ModuleSDK;

namespace SharpClaw.TestFixtures.ExternalRegistration;

public sealed class DotNetSidecarFixtureRegistration : ISharpClawModule
{
    public const string SourceId = "synthetic_dotnet_sidecar";
    public const string ToolPrefixValue = "sds";
    public const string JobTool = "dotnet_sidecar_echo";
    public const string InlineTool = "dotnet_sidecar_inline";

    public ModuleIdentity Identity { get; } = new(SourceId, ".NET sidecar fixture", ToolPrefixValue);

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<DotNetSidecarToolHandler>();
        services.AddTool<DotNetSidecarToolHandler>(
            new ToolDescriptor(JobTool, ".NET sidecar echo tool.", ToolSchemas.EmptyObject));
        services.AddTool<DotNetSidecarToolHandler>(
            new ToolDescriptor(InlineTool, ".NET sidecar inline tool.", ToolSchemas.EmptyObject));
    }
}

internal sealed class DotNetSidecarToolHandler : IToolHandler
{
    public ValueTask<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var value = invocation.Arguments.ValueKind == JsonValueKind.Object &&
            invocation.Arguments.TryGetProperty("value", out var property)
            ? property.GetString() ?? "missing"
            : "missing";
        return ValueTask.FromResult(ToolResult.Text($"dotnet sidecar {value}"));
    }
}
