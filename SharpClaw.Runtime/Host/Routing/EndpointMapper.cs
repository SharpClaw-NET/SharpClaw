using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace SharpClaw.Runtime.Host.Routing;

internal static partial class EndpointMapper
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
        var logger = routes.ServiceProvider.GetRequiredService<ILoggerFactory>()
            .CreateLogger(typeof(EndpointMapper).FullName!);
        var totalMapped = 0;
        var totalFailed = 0;
        foreach (var handlerClass in GetHandlerClasses(assembly, logger))
        {
            var (mapped, failed) = MapHandlerClass(routes, handlerClass, logger);
            totalMapped += mapped;
            totalFailed += failed;
        }
        if (totalFailed > 0)
            LogMappingFailures(logger, totalMapped, totalFailed);
        else
            LogMappingComplete(logger, totalMapped);
        return routes;
    }

    private static Type[] GetHandlerClasses(Assembly assembly, ILogger logger)
    {
        try
        {
            return assembly.GetTypes().Where(IsHandlerClass).ToArray();
        }
        catch (ReflectionTypeLoadException exception)
        {
            LogPartialTypes(logger, assembly.FullName, exception);
            return exception.Types.OfType<Type>().Where(IsHandlerClass).ToArray();
        }
    }

    private static bool IsHandlerClass(Type type) =>
        type is { IsClass: true, IsAbstract: true, IsSealed: true }
        && Attribute.IsDefined(type, typeof(RouteGroupAttribute));

    private static (int Mapped, int Failed) MapHandlerClass(
        IEndpointRouteBuilder routes, Type handlerClass, ILogger logger)
    {
        var groupAttribute = handlerClass.GetCustomAttribute<RouteGroupAttribute>()!;
        RouteGroupBuilder group;
        try
        {
            group = routes.MapGroup(groupAttribute.Prefix);
        }
#pragma warning disable CA1031 // Isolate a registration's malformed route group so independent handler classes remain available, as this mapper's contract requires.
        catch (Exception exception)
        {
            LogGroupFailure(logger, groupAttribute.Prefix, handlerClass.FullName, exception);
            return (0, 1);
        }
#pragma warning restore CA1031
        var mapped = 0;
        var failed = 0;
        var methods = handlerClass.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(method => Attribute.IsDefined(method, typeof(MapMethodAttribute)));
        foreach (var method in methods)
        {
            var attribute = method.GetCustomAttribute<MapMethodAttribute>()!;
            if (TryMapEndpoint(routes, group, groupAttribute, handlerClass, method, attribute, logger))
                mapped++;
            else
                failed++;
        }
        return (mapped, failed);
    }

    private static bool TryMapEndpoint(
        IEndpointRouteBuilder routes,
        RouteGroupBuilder group,
        RouteGroupAttribute groupAttribute,
        Type handlerClass,
        MethodInfo method,
        MapMethodAttribute attribute,
        ILogger logger)
    {
        try
        {
            // RequestDelegateFactory distinguishes DI services from request-bound parameters.
            var options = new RequestDelegateFactoryOptions { ServiceProvider = routes.ServiceProvider };
            var requestDelegate = RequestDelegateFactory.Create(method, targetFactory: null, options).RequestDelegate;
            _ = attribute.HttpMethod switch
            {
                "GET" => group.MapGet(attribute.Pattern, requestDelegate),
                "POST" => group.MapPost(attribute.Pattern, requestDelegate),
                "PUT" => group.MapPut(attribute.Pattern, requestDelegate),
                "DELETE" => group.MapDelete(attribute.Pattern, requestDelegate),
                "PATCH" => group.MapPatch(attribute.Pattern, requestDelegate),
                _ => throw new NotSupportedException($"HTTP method '{attribute.HttpMethod}' is not supported."),
            };
            return true;
        }
#pragma warning disable CA1031 // Registration methods can fail in reflection or delegate generation; log and skip only that route so sibling routes still load.
        catch (Exception exception)
        {
            LogEndpointFailure(logger, attribute.HttpMethod, groupAttribute.Prefix,
                attribute.Pattern, handlerClass.FullName, method.Name, exception);
            return false;
        }
#pragma warning restore CA1031
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Error,
        Message = "Failed to enumerate handler classes in assembly {Assembly}. Partial types will be used.")]
    private static partial void LogPartialTypes(ILogger logger, string? assembly, Exception exception);

    [LoggerMessage(EventId = 2, Level = LogLevel.Error,
        Message = "Failed to create route group '{Prefix}' for {Handler}. All endpoints in this group will be skipped.")]
    private static partial void LogGroupFailure(ILogger logger, string prefix, string? handler, Exception exception);

    [LoggerMessage(EventId = 3, Level = LogLevel.Error,
        Message = "Failed to map endpoint {Method} {Prefix}{Pattern} from {Handler}.{HandlerMethod}. This route will be unavailable; other routes are unaffected.")]
    private static partial void LogEndpointFailure(ILogger logger, string method, string prefix,
        string pattern, string? handler, string handlerMethod, Exception exception);

    [LoggerMessage(EventId = 4, Level = LogLevel.Warning,
        Message = "MapHandlers: mapped {Mapped} endpoints, {Failed} failed.")]
    private static partial void LogMappingFailures(ILogger logger, int mapped, int failed);

    [LoggerMessage(EventId = 5, Level = LogLevel.Debug, Message = "MapHandlers: mapped {Mapped} endpoints.")]
    private static partial void LogMappingComplete(ILogger logger, int mapped);
}
