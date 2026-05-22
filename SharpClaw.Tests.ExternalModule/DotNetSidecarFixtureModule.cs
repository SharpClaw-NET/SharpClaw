using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.DTOs.AgentActions;
using SharpClaw.Contracts.Enums;
using SharpClaw.Contracts.Modules;

namespace SharpClaw.Tests.ExternalModule;

public sealed class DotNetSidecarFixtureModule : ISharpClawModule
{
    public const string ModuleId = "synthetic_dotnet_sidecar";
    public const string ToolPrefixValue = "sds";
    public const string JobTool = "dotnet_sidecar_echo";
    public const string InlineTool = "dotnet_sidecar_inline";
    public const string HeaderTag = "dotnet_sidecar_config";
    public const string ResourceType = "SharpClaw.DotNetSidecarFixture.Resource";

    public string Id => ModuleId;
    public string DisplayName => "Synthetic .NET Sidecar";
    public string ToolPrefix => ToolPrefixValue;

    public void ConfigureServices(IServiceCollection services)
    {
    }

    public IReadOnlyList<ModuleToolDefinition> GetToolDefinitions() =>
    [
        new(
            JobTool,
            ".NET sidecar echo tool.",
            EmptySchema(),
            new ModuleToolPermission(
                false,
                (_, _, _, _) => Task.FromResult(
                    AgentActionResult.Approve(
                        ".NET sidecar fixture approved.",
                        PermissionClearance.Independent))))
    ];

    public IReadOnlyList<ModuleInlineToolDefinition> GetInlineToolDefinitions() =>
    [
        new(
            InlineTool,
            ".NET sidecar inline tool.",
            EmptySchema())
    ];

    public IReadOnlyList<ModuleHeaderTag>? GetHeaderTags() =>
    [
        new(HeaderTag, async (services, ct) =>
        {
            var store = services.GetRequiredService<IModuleConfigStore>();
            return await store.GetAsync("sidecar:last", ct) ?? "missing";
        })
    ];

    public IReadOnlyList<ModuleResourceTypeDescriptor> GetResourceTypeDescriptors() =>
    [
        new(
            ResourceType,
            "DotNetSidecarResource",
            "UseDotNetSidecarFixtureAsync",
            (_, _) => Task.FromResult(new List<Guid>
            {
                Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            }),
            (_, _) => Task.FromResult(new List<(Guid Id, string Name)>
            {
                (Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), ".NET sidecar resource"),
            }),
            "dotnet_sidecar_fixture")
    ];

    public void MapEndpoints(object app)
    {
        var endpoints = (IEndpointRouteBuilder)app;
        endpoints.MapGet("/modules/dotnet-sidecar/ping", () => Results.Text("dotnet sidecar pong"));
    }

    public async Task<string> ExecuteToolAsync(
        string toolName,
        JsonElement parameters,
        AgentJobContext job,
        IServiceProvider scopedServices,
        CancellationToken ct)
    {
        var value = parameters.TryGetProperty("value", out var property)
            ? property.GetString() ?? "missing"
            : "missing";
        var store = scopedServices.GetRequiredService<IModuleConfigStore>();
        await store.SetAsync("sidecar:last", value, ct);
        return $"dotnet sidecar {await store.GetAsync("sidecar:last", ct)}";
    }

    public Task<string> ExecuteInlineToolAsync(
        string toolName,
        JsonElement parameters,
        InlineToolContext context,
        IServiceProvider scopedServices,
        CancellationToken ct) =>
        Task.FromResult("dotnet sidecar inline");

    private static JsonElement EmptySchema()
    {
        using var doc = JsonDocument.Parse("""{"type":"object","additionalProperties":false}""");
        return doc.RootElement.Clone();
    }
}
