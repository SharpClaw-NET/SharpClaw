using Microsoft.AspNetCore.Http;
using SharpClaw.Gateway.Abstractions;
using SharpClaw.Modules.WebAccess.Dtos;

namespace SharpClaw.Modules.WebAccess.Gateway;

/// <summary>
/// Phase 4 gateway extension that exposes the WebAccess search engine
/// CRUD surface through the public gateway under
/// <c>/api/modules/webaccess/searchengines</c>. Reads forward through
/// <see cref="IGatewayInternalApi"/>; mutations are queued through
/// <see cref="IGatewayDispatcher"/>. The extension is sibling to
/// <c>WebAccessModule</c>: the API loader picks up <c>ISharpClawModule</c>
/// and the gateway loader picks up this class independently.
/// </summary>
public sealed class WebAccessGatewayExtension : IGatewayModuleExtension
{
    /// <inheritdoc />
    public string ModuleId => "webaccess";

    /// <inheritdoc />
    public string DisplayName => "Web Access";

    /// <inheritdoc />
    public IReadOnlyList<GatewayEndpointGroup> GetEndpointGroups() =>
    [
        new GatewayEndpointGroup(
            GroupId: "searchengines",
            DisplayName: "Search engines",
            Description: "Public CRUD over registered search engines.",
            RateLimitPolicy: null, // null → global policy (60/min sliding)
            DefaultEnabled: false),
    ];

    /// <inheritdoc />
    public void MapEndpoints(IGatewayEndpointGroupBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // ── Reads forward directly through the internal API ──────
        builder.MapGet("/", async (IGatewayInternalApi api, CancellationToken ct) =>
            Results.Ok(await api.GetAsync<IReadOnlyList<SearchEngineResponse>>(
                "/searchengines", ct)));

        builder.MapGet("/{id:guid}", async (
                Guid id, IGatewayInternalApi api, CancellationToken ct) =>
            await api.GetAsync<SearchEngineResponse>($"/searchengines/{id}", ct)
                is { } found
                    ? Results.Ok(found)
                    : Results.NotFound());

        // ── Mutations go through the queue dispatcher ───────────
        builder.MapPost("/", async (
                CreateSearchEngineRequest body,
                IGatewayDispatcher dispatcher,
                CancellationToken ct) =>
            (await dispatcher.PostAsync("/searchengines", body, ct)).ToResult());

        builder.MapPut("/{id:guid}", async (
                Guid id,
                UpdateSearchEngineRequest body,
                IGatewayDispatcher dispatcher,
                CancellationToken ct) =>
            (await dispatcher.PutAsync($"/searchengines/{id}", body, ct)).ToResult());

        builder.MapDelete("/{id:guid}", async (
                Guid id,
                IGatewayDispatcher dispatcher,
                CancellationToken ct) =>
            (await dispatcher.DeleteAsync($"/searchengines/{id}", ct)).ToResult());
    }
}
