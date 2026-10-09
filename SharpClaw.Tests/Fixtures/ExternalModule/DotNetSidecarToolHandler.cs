using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.Kernel;
using SharpClaw.ModuleSDK;

namespace SharpClaw.TestFixtures.ExternalRegistration;


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
