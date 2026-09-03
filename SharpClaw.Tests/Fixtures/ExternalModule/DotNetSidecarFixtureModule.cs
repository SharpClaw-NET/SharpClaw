using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.Modules;

namespace SharpClaw.TestFixtures.ExternalModule;

public sealed class DotNetSidecarFixtureModule : ISharpClawModule
{
    public const string ModuleId = "synthetic_dotnet_sidecar";
    public const string ToolPrefixValue = "sds";
    public const string JobTool = "dotnet_sidecar_echo";
    public const string InlineTool = "dotnet_sidecar_inline";

    public ModuleIdentity Identity { get; } = new(ModuleId, ".NET sidecar fixture", ToolPrefixValue);

    public void Configure(ISharpClawModuleBuilder module)
    {
        module.Services.AddSingleton<DotNetSidecarToolHandler>();
        module.Tools.Add<DotNetSidecarToolHandler>(
            new ToolDescriptor(JobTool, ".NET sidecar echo tool.", ToolSchemas.EmptyObject));
        module.Tools.Add<DotNetSidecarToolHandler>(
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
