using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Runtime.Host.Routing;
using SharpClaw.Runtime.BLL.Modules;
using SharpClaw.Core.Modules;

namespace SharpClaw.Runtime.Host.Handlers;

/// <summary>
/// Universal resource lookup endpoint.  Type-specific CRUD endpoints
/// are registered by their owning runtime modules via
/// <see cref="SharpClaw.Contracts.Modules.ISharpClawRuntimeModule.MapEndpoints"/>.
/// </summary>
[RouteGroup("/resources")]
public static class ResourceHandlers
{
    // ═══════════════════════════════════════════════════════════════
    // Universal resource lookup
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Returns lightweight <c>[{id, name}]</c> items for the resource type
    /// that backs a given permission access category.  The <paramref name="type"/>
    /// value matches the <see cref="SharpClaw.Contracts.Models.ResourceAccessDB.ResourceType"/> discriminator
    /// contributed by a registered module resource descriptor.  Delegates to the owning module's
    /// <c>LoadLookupItems</c> callback registered via <see cref="SharpClaw.Contracts.Modules.ModuleResourceTypeDescriptor"/>.
    /// </summary>
    [MapGet("/lookup/{type}")]
    public static async Task<IResult> LookupByAccessType(
        string type, ModuleRegistry registry, IServiceScopeFactory scopeFactory, CancellationToken ct)
    {
        var descriptor = registry.GetResourceTypeDescriptor(type);
        if (descriptor?.LoadLookupItems is null)
            return Results.BadRequest(new { error = $"Unknown resource type '{type}'." });

        await using var scope = scopeFactory.CreateAsyncScope();
        var items = await descriptor.LoadLookupItems(scope.ServiceProvider, ct);
        return Results.Ok(items.OrderBy(i => i.Name).Select(i => new { id = i.Id, name = i.Name }));
    }
}
