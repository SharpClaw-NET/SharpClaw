using System.Reflection;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using NUnit.Framework;
using SharpClaw.Runtime.Host.Routing;
using SharpClaw.Gateway.Contracts;

namespace SharpClaw.Tests.Gateway;

/// <summary>
/// Tier-4 route-parity guardrail. Reflects over the core
/// <c>SharpClaw.Runtime.Host</c> handlers (decorated with
/// <see cref="RouteGroupAttribute"/>) and the <c>SharpClaw.Gateway</c>
/// MVC controllers, then asserts that every public core route is either
/// projected through a classic gateway controller, projected through a
/// module gateway extension, or explicitly documented in
/// <see cref="KnownClassicGaps"/>. Private admin surfaces (env, system,
/// admin/db) are excluded by design — see
/// <c>docs/internal/gateway-drift-and-publish-plan-v2.md</c>.
/// </summary>
[TestFixture]
public sealed class GatewayRouteParityTests
{
    /// <summary>
    /// Core <see cref="RouteGroupAttribute"/> prefixes that are deliberately
    /// kept off the public gateway. These admin surfaces never become part
    /// of public parity.
    /// </summary>
    private static readonly HashSet<string> PrivateHandlerPrefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "/admin/db",
        "/env",
        "/system",
        // Module management is an admin-only surface; it must not be projected
        // publicly. If a module surface needs public projection, it must ship
        // a per-module IGatewayModuleExtension instead.
        "/modules",
    };

    /// <summary>
    /// Routes that exist on the core but are intentionally not (yet)
    /// projected through a classic gateway controller. Update this list
    /// when a route lands on the gateway, gets projected through a module
    /// extension, or is removed from the core.
    /// </summary>
    private static readonly HashSet<string> KnownClassicGaps = new(StringComparer.OrdinalIgnoreCase)
    {
        // Tasks / TaskStreaming — no classic controller projection yet.
        "POST /tasks/validate",
        "POST /tasks",
        "GET /tasks",
        "GET /tasks/{*}",
        "PUT /tasks/{*}",
        "DELETE /tasks/{*}",
        "GET /tasks/{*}/preflight",
        "GET /tasks/trigger-sources",
        "POST /tasks/{*}/triggers/enable",
        "POST /tasks/{*}/triggers/disable",
        "POST /tasks/{*}/instances",
        "GET /tasks/{*}/instances",
        "GET /tasks/{*}/instances/{*}",
        "POST /tasks/{*}/instances/{*}/cancel",
        "POST /tasks/{*}/instances/{*}/stop",
        "POST /tasks/{*}/instances/{*}/pause",
        "POST /tasks/{*}/instances/{*}/resume",
        "POST /tasks/{*}/instances/{*}/start",
        "GET /tasks/{*}/instances/{*}/outputs",
        "GET /tasks/{*}/instances/{*}/stream",

        // Tool awareness sets — no classic controller projection yet.
        "POST /tool-awareness-sets",
        "GET /tool-awareness-sets",
        "GET /tool-awareness-sets/{*}",
        "PUT /tool-awareness-sets/{*}",
        "DELETE /tool-awareness-sets/{*}",

        // Resource lookup — no classic controller projection yet.
        "GET /resources/lookup/{*}",

        // Thread watch SSE — bespoke streaming forwarding still missing.
        "GET /channels/{*}/chat/threads/{*}/watch",

        // Streaming proxies — covered by dedicated proxies, not REST.
        "POST /channels/{*}/chat/stream",
        "POST /channels/{*}/chat/stream/approve/{*}",
        "POST /channels/{*}/chat/threads/{*}/stream",
        "POST /channels/{*}/chat/threads/{*}/stream/approve/{*}",
    };

    [Test]
    public void Core_PublicRoutes_AreEitherProjectedByGateway_OrListedAsKnownGaps()
    {
        var coreRoutes = DiscoverCoreRoutes();
        var gatewayRoutes = DiscoverGatewayRoutes();

        var unprojected = coreRoutes
            .Where(r => !gatewayRoutes.Contains(r))
            .Where(r => !KnownClassicGaps.Contains(r))
            .OrderBy(r => r, StringComparer.Ordinal)
            .ToArray();

        unprojected.Should().BeEmpty(
            "every public core route must be projected by SharpClaw.Gateway, " +
            "projected through an IGatewayModuleExtension, or explicitly added " +
            "to KnownClassicGaps with rationale. See " +
            "docs/internal/gateway-drift-and-publish-plan-v2.md.\n" +
            "Unprojected routes:\n  " + string.Join("\n  ", unprojected));
    }

    [Test]
    public void KnownClassicGaps_DoNotReferenceRoutesThatNoLongerExist()
    {
        var coreRoutes = DiscoverCoreRoutes();
        var stale = KnownClassicGaps
            .Where(g => !coreRoutes.Contains(g))
            .OrderBy(g => g, StringComparer.Ordinal)
            .ToArray();

        stale.Should().BeEmpty(
            "KnownClassicGaps entries must reference routes that still exist on " +
            "the core. Remove an entry when the route is deleted or projected.\n" +
            "Stale entries:\n  " + string.Join("\n  ", stale));
    }

    [Test]
    public void GatewayRoutes_ThatAreNotInCore_AreEmpty()
    {
        // Catches gateway-only drift: controllers that point at endpoints
        // the core no longer exposes.
        var coreRoutes = DiscoverCoreRoutes();
        var gatewayRoutes = DiscoverGatewayRoutes();

        // Cost-related aggregate routes on the gateway forward through the
        // internal API but the core exposes them via different prefixes
        // (e.g. /providers/cost/total). They are still in core, so the
        // direct comparison is valid; allowlist any framework-injected
        // routes here only when proven necessary.
        var orphans = gatewayRoutes
            .Where(r => !coreRoutes.Contains(r))
            .OrderBy(r => r, StringComparer.Ordinal)
            .ToArray();

        orphans.Should().BeEmpty(
            "every gateway controller route must correspond to a core handler " +
            "route. A gateway-only route indicates drift in the opposite " +
            "direction.\nOrphan gateway routes:\n  " +
            string.Join("\n  ", orphans));
    }

    private static HashSet<string> DiscoverCoreRoutes()
    {
        var apiAssembly = typeof(EndpointMapper).Assembly;
        var routes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var type in SafeGetTypes(apiAssembly))
        {
            // EndpointMapper looks for static classes (abstract + sealed).
            if (!type.IsClass || !type.IsAbstract || !type.IsSealed) continue;

            var groupAttr = type.GetCustomAttribute<RouteGroupAttribute>();
            if (groupAttr is null) continue;

            if (IsExcludedHandlerPrefix(groupAttr.Prefix)) continue;

            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                var mapAttr = method.GetCustomAttribute<MapMethodAttribute>();
                if (mapAttr is null) continue;

                var combined = CombinePath(groupAttr.Prefix, mapAttr.Pattern);
                routes.Add($"{mapAttr.HttpMethod} {Normalize(combined)}");
            }
        }

        return routes;
    }

    private static HashSet<string> DiscoverGatewayRoutes()
    {
        var gatewayAssembly = typeof(SharpClaw.Gateway.Controllers.AuthController).Assembly;
        var routes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var type in SafeGetTypes(gatewayAssembly))
        {
            if (!type.IsClass || type.IsAbstract) continue;

            // Skip GatewayController; it owns gateway-side metadata routes
            // and is not a projection of a core handler.
            if (string.Equals(type.Name, "GatewayController", StringComparison.Ordinal))
                continue;

            var routeAttr = type.GetCustomAttribute<RouteAttribute>();
            if (routeAttr is null) continue;

            var controllerName = type.Name.EndsWith("Controller", StringComparison.Ordinal)
                ? type.Name[..^"Controller".Length]
                : type.Name;

            var classRoute = (routeAttr.Template ?? string.Empty).Replace(
                "[controller]", controllerName, StringComparison.OrdinalIgnoreCase);

            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                foreach (var http in method.GetCustomAttributes<HttpMethodAttribute>())
                {
                    var template = http.Template ?? string.Empty;
                    var combined = CombinePath(classRoute, template);
                    foreach (var verb in http.HttpMethods)
                    {
                        routes.Add($"{verb} {Normalize(combined)}");
                    }
                }
            }
        }

        return routes;
    }

    private static bool IsExcludedHandlerPrefix(string prefix)
    {
        var trimmed = (prefix ?? string.Empty).TrimEnd('/');
        foreach (var excluded in PrivateHandlerPrefixes)
        {
            if (trimmed.Equals(excluded, StringComparison.OrdinalIgnoreCase))
                return true;
            if (trimmed.StartsWith(excluded + "/", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static IEnumerable<Type> SafeGetTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.OfType<Type>();
        }
    }

    private static string CombinePath(string a, string b)
    {
        var left = (a ?? string.Empty).TrimEnd('/');
        var right = (b ?? string.Empty).TrimStart('/');
        if (right.Length == 0) return left.Length == 0 ? "/" : left;
        if (left.Length == 0) return "/" + right;
        return $"{left}/{right}";
    }

    private static string Normalize(string path)
    {
        var p = (path ?? string.Empty).Trim();
        if (p.StartsWith("/", StringComparison.Ordinal)) p = p[1..];
        if (p.StartsWith("api/", StringComparison.OrdinalIgnoreCase)) p = p[4..];
        else if (p.Equals("api", StringComparison.OrdinalIgnoreCase)) p = string.Empty;

        p = "/" + p.TrimEnd('/');

        var sb = new StringBuilder(p.Length);
        var i = 0;
        while (i < p.Length)
        {
            if (p[i] == '{')
            {
                var end = p.IndexOf('}', i);
                if (end < 0) { sb.Append(p[i..]); break; }
                sb.Append("{*}");
                i = end + 1;
            }
            else
            {
                sb.Append(p[i]);
                i++;
            }
        }
        return sb.ToString().ToLowerInvariant();
    }
}
