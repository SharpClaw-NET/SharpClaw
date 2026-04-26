using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace SharpClaw.Application.API.Routing;

public static class EndpointMapper
{
    /// <summary>
    /// Scans all static handler classes decorated with <see cref="RouteGroupAttribute"/>
    /// in the calling assembly and registers their methods as minimal API endpoints.
    /// <para>
    /// Uses <see cref="RequestDelegateFactory"/> so that ASP.NET correctly distinguishes
    /// DI-injected services from route/body/query parameters. This prevents service
    /// parameters (e.g. <c>MyService svc</c>) from being misidentified as HTTP body
    /// parameters, which previously caused all POST requests in a group to return 400.
    /// </para>
    /// <para>
    /// Each handler class is processed in isolation: failures in one class are logged
    /// and skipped so the rest of the API remains operational. Within a handler class,
    /// each endpoint method is likewise wrapped so that a single broken route does not
    /// affect sibling routes in the same group.
    /// </para>
    /// </summary>
    public static IEndpointRouteBuilder MapHandlers(this IEndpointRouteBuilder routes, Assembly? assembly = null)
    {
        assembly ??= Assembly.GetCallingAssembly();

        var logger = routes.ServiceProvider
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger(typeof(EndpointMapper).FullName!);

        Type[] handlerClasses;
        try
        {
            handlerClasses = assembly.GetTypes()
                .Where(t => t is { IsClass: true, IsAbstract: true, IsSealed: true } // static classes
                            && t.GetCustomAttribute<RouteGroupAttribute>() is not null)
                .ToArray();
        }
        catch (ReflectionTypeLoadException ex)
        {
            logger.LogError(ex, "Failed to enumerate handler classes in assembly {Assembly}. " +
                "Partial types will be used.", assembly.FullName);
            handlerClasses = [.. ex.Types.Where(t => t is not null
                && t.IsClass && t.IsAbstract && t.IsSealed
                && t.GetCustomAttribute<RouteGroupAttribute>() is not null)!];
        }

        var totalMapped = 0;
        var totalFailed = 0;

        foreach (var handlerClass in handlerClasses)
        {
            var groupAttr = handlerClass.GetCustomAttribute<RouteGroupAttribute>()!;

            RouteGroupBuilder group;
            try
            {
                group = routes.MapGroup(groupAttr.Prefix);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to create route group '{Prefix}' for {Handler}. " +
                    "All endpoints in this group will be skipped.",
                    groupAttr.Prefix, handlerClass.FullName);
                totalFailed++;
                continue;
            }

            var methods = handlerClass.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(m => m.GetCustomAttribute<MapMethodAttribute>() is not null);

            foreach (var method in methods)
            {
                var attr = method.GetCustomAttribute<MapMethodAttribute>()!;
                try
                {
                    // RequestDelegateFactory.Create understands which parameters are
                    // services (resolved from DI) vs request-bound (route/body/query).
                    // This is what the framework uses internally for lambda-based minimal
                    // APIs and is the only safe way to register MethodInfo handlers that
                    // mix service parameters with route/body parameters.
                    var options = new RequestDelegateFactoryOptions
                    {
                        ServiceProvider = routes.ServiceProvider,
                    };
                    var requestDelegate = RequestDelegateFactory.Create(method, targetFactory: null, options).RequestDelegate;

                    _ = attr.HttpMethod switch
                    {
                        "GET"    => group.MapGet(attr.Pattern, requestDelegate),
                        "POST"   => group.MapPost(attr.Pattern, requestDelegate),
                        "PUT"    => group.MapPut(attr.Pattern, requestDelegate),
                        "DELETE" => group.MapDelete(attr.Pattern, requestDelegate),
                        "PATCH"  => group.MapPatch(attr.Pattern, requestDelegate),
                        _ => throw new NotSupportedException(
                            $"HTTP method '{attr.HttpMethod}' is not supported.")
                    };

                    totalMapped++;
                }
                catch (Exception ex)
                {
                    totalFailed++;
                    logger.LogError(ex,
                        "Failed to map endpoint {Method} {Prefix}{Pattern} from {Handler}.{HandlerMethod}. " +
                        "This route will be unavailable; other routes are unaffected.",
                        attr.HttpMethod, groupAttr.Prefix, attr.Pattern,
                        handlerClass.FullName, method.Name);
                }
            }
        }

        if (totalFailed > 0)
        {
            logger.LogWarning("MapHandlers: mapped {Mapped} endpoints, {Failed} failed.",
                totalMapped, totalFailed);
        }
        else
        {
            logger.LogDebug("MapHandlers: mapped {Mapped} endpoints.", totalMapped);
        }

        return routes;
    }
}

