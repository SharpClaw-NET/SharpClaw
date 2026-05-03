using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using SharpClaw.Application.Infrastructure.Tasks;
using SharpClaw.Contracts.Tasks;

namespace SharpClaw.Application.API.Webhooks;

/// <summary>
/// ASP.NET Core implementation of <see cref="IWebhookRouteRegistrar"/>.
/// Registers dynamic minimal-API POST routes on the running
/// <see cref="WebApplication"/> so that tasks can receive webhook calls.
///
/// Routes are registered at most once per path; active/inactive state is
/// managed by the module-owned webhook trigger source via its internal
/// route map. The registry is populated during
/// <see cref="IHostApplicationLifetime.ApplicationStarted"/> after
/// <see cref="TaskTriggerHostService"/> has loaded its first set of bindings.
/// </summary>
public sealed class WebhookRouteRegistry(
    WebApplication app,
    IWebhookTriggerHost triggerSource,
    ILogger<WebhookRouteRegistry> logger) : IWebhookRouteRegistrar
{
    private readonly ConcurrentDictionary<string, bool> _registered = new(
        StringComparer.OrdinalIgnoreCase);

    // ── IWebhookRouteRegistrar ────────────────────────────────────

    /// <inheritdoc />
    public void EnsureRegistered(string routePath)
    {
        if (!_registered.TryAdd(routePath, true))
            return; // already registered in this app lifetime

        app.MapPost(routePath, async (HttpContext ctx) =>
        {
            ctx.Request.EnableBuffering();

            string body;
            using (var reader = new System.IO.StreamReader(
                ctx.Request.Body, leaveOpen: true))
            {
                body = await reader.ReadToEndAsync();
            }
            ctx.Request.Body.Position = 0;

            var headersDict = ctx.Request.Headers
                .ToDictionary(
                    h => h.Key,
                    h => h.Value.ToString(),
                    StringComparer.OrdinalIgnoreCase);
            var headersJson = JsonSerializer.Serialize(headersDict);

            var statusCode = await triggerSource.HandleRequestAsync(
                routePath, body, headersJson, ctx.RequestAborted);

            return statusCode switch
            {
                202 => Results.Accepted(),
                401 => Results.Unauthorized(),
                _   => Results.NotFound(),
            };
        });

        logger.LogInformation("Webhook POST route registered: {Route}", routePath);
    }
}
