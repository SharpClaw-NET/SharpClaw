using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.Logging;

using SharpClaw.Contracts.Tasks;

namespace SharpClaw.Modules.Http;

/// <summary>
/// Trigger source for <see cref="HttpTriggerKeys.Webhook"/> bindings.
/// Registers one dynamic POST route per active binding at startup via
/// <see cref="IWebhookRouteRegistrar"/>, and maintains an in-memory
/// registry so that disabled bindings return 404 without requiring a
/// route to be unregistered (minimal-API routes cannot be removed at
/// runtime).
/// <para>
/// Moved out of <c>SharpClaw.Application.Core</c> by the trigger-extraction
/// plan; behavior is preserved verbatim.
/// </para>
/// </summary>
public sealed class WebhookTriggerSource : ITaskTriggerSource, IWebhookTriggerHost
{
    private readonly ILogger<WebhookTriggerSource> _logger;
    private IWebhookRouteRegistrar? _routeRegistrar;

    // Active contexts indexed by route path so the request handler can
    // look them up without scanning the full list on every request.
    private readonly ConcurrentDictionary<string, ITaskTriggerSourceContext> _activeRoutes = new(
        StringComparer.OrdinalIgnoreCase);

    private const string DefaultSignatureHeader = "X-Hub-Signature-256";

    public WebhookTriggerSource(ILogger<WebhookTriggerSource> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Sets the route registrar. Called by the API host after the
    /// <see cref="WebApplication"/> is available so that minimal-API
    /// routes can be registered dynamically.
    /// </summary>
    public void SetRouteRegistrar(IWebhookRouteRegistrar registrar)
    {
        _routeRegistrar = registrar;
    }

    // ── ITaskTriggerSource ────────────────────────────────────────

    /// <inheritdoc />
    public string TriggerKey => HttpTriggerKeys.Webhook;

    /// <inheritdoc />
    public Task StartAsync(IReadOnlyList<ITaskTriggerSourceContext> contexts, CancellationToken ct)
    {
        _activeRoutes.Clear();

        foreach (var ctx in contexts)
        {
            var def = ctx.Definition;
            var route = def.Parameters.GetValueOrDefault(HttpTriggerKeys.WebhookRoute);
            if (string.IsNullOrWhiteSpace(route))
            {
                _logger.LogWarning(
                    "Webhook binding for task {TaskId} has no WebhookRoute — skipping.",
                    ctx.TaskDefinitionId);
                continue;
            }

            var routePath = NormalizeRoute(route);
            _activeRoutes[routePath] = ctx;

            // Register the route once; subsequent bindings to the same path
            // (after a reload) reuse the already-registered route via the
            // active-routes dictionary lookup inside the handler closure.
            _routeRegistrar?.EnsureRegistered(routePath);

            _logger.LogDebug(
                "Webhook route {Route} registered for task {TaskId}.",
                routePath, ctx.TaskDefinitionId);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync()
    {
        _activeRoutes.Clear();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public string? GetBindingValue(TaskTriggerDefinition def) =>
        def.TriggerKey == HttpTriggerKeys.Webhook
            ? def.Parameters.GetValueOrDefault(HttpTriggerKeys.WebhookRoute)
            : null;

    // ── Public helpers ────────────────────────────────────────────

    /// <summary>
    /// Returns the HTTP status code (200 = fire, 401 = bad signature, 404 = not found)
    /// for an incoming webhook request. Exposed for unit testing.
    /// </summary>
    public async Task<int> HandleRequestAsync(
        string routePath,
        string body,
        string headersJson,
        CancellationToken ct = default)
    {
        if (!_activeRoutes.TryGetValue(routePath, out var ctx))
            return 404;

        var def = ctx.Definition;

        // HMAC-SHA256 validation
        var secretEnvVar = def.Parameters.GetValueOrDefault(HttpTriggerKeys.WebhookSecretEnvVar);
        if (!string.IsNullOrWhiteSpace(secretEnvVar))
        {
            var secret = Environment.GetEnvironmentVariable(secretEnvVar);
            if (string.IsNullOrEmpty(secret))
            {
                _logger.LogWarning(
                    "Webhook {Route}: secret env var '{Var}' is not set — rejecting request.",
                    routePath, secretEnvVar);
                return 401;
            }

            var configuredSigHeader = def.Parameters.GetValueOrDefault(HttpTriggerKeys.WebhookSignatureHeader);
            var sigHeader = string.IsNullOrWhiteSpace(configuredSigHeader)
                ? DefaultSignatureHeader
                : configuredSigHeader;

            var providedSig = ExtractHeader(headersJson, sigHeader);
            if (!ValidateHmac(body, secret, providedSig))
            {
                _logger.LogWarning(
                    "Webhook {Route}: HMAC signature mismatch — rejecting request.", routePath);
                return 401;
            }
        }

        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["WebhookBody"]    = body,
            ["WebhookHeaders"] = headersJson,
        };

        try
        {
            await ctx.FireAsync(parameters, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Webhook {Route}: error firing task context for {TaskId}.",
                routePath, ctx.TaskDefinitionId);
        }

        return 202;
    }

    // ── Private helpers ───────────────────────────────────────────

    private static string NormalizeRoute(string route)
    {
        route = route.Trim().TrimStart('/');
        // Already a fully-qualified webhook path?
        if (route.StartsWith("webhooks/", StringComparison.OrdinalIgnoreCase))
            return "/" + route;
        return "/webhooks/tasks/" + route;
    }

    private static bool ValidateHmac(string body, string secret, string? providedSignature)
    {
        if (string.IsNullOrEmpty(providedSignature))
            return false;

        var keyBytes  = Encoding.UTF8.GetBytes(secret);
        var bodyBytes = Encoding.UTF8.GetBytes(body);

        var hashBytes = HMACSHA256.HashData(keyBytes, bodyBytes);
        var computed  = "sha256=" + Convert.ToHexString(hashBytes).ToLowerInvariant();

        // Signature provided may be "sha256=abc123" or just "abc123"
        var normalised = providedSignature.StartsWith("sha256=", StringComparison.OrdinalIgnoreCase)
            ? providedSignature
            : "sha256=" + providedSignature;

        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(computed),
            Encoding.ASCII.GetBytes(normalised));
    }

    private static string? ExtractHeader(string headersJson, string headerName)
    {
        if (string.IsNullOrEmpty(headersJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(headersJson);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (string.Equals(prop.Name, headerName, StringComparison.OrdinalIgnoreCase))
                    return prop.Value.GetString();
            }
        }
        catch { /* malformed JSON — treat as missing */ }
        return null;
    }
}
