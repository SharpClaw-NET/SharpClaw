using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace SharpClaw.Application.API.Routing;

public static class EndpointMapper
{
    /// <summary>
    /// Scans all static handler classes decorated with <see cref="RouteGroupAttribute"/>
    /// in the calling assembly and registers their methods as minimal API endpoints.
    /// </summary>
    public static IEndpointRouteBuilder MapHandlers(this IEndpointRouteBuilder routes, Assembly? assembly = null)
    {
        assembly ??= Assembly.GetCallingAssembly();

        var handlerClasses = assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: true, IsSealed: true } // static classes
                        && t.GetCustomAttribute<RouteGroupAttribute>() is not null);

        foreach (var handlerClass in handlerClasses)
        {
            var groupAttr = handlerClass.GetCustomAttribute<RouteGroupAttribute>()!;
            var group = routes.MapGroup(groupAttr.Prefix);

            var methods = handlerClass.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(m => m.GetCustomAttribute<MapMethodAttribute>() is not null);

            foreach (var method in methods)
            {
                var attr = method.GetCustomAttribute<MapMethodAttribute>()!;
                var handler = method.CreateDelegate(CreateDelegateType(method), null);

                _ = attr.HttpMethod switch
                {
                    "GET" => group.MapGet(attr.Pattern, handler),
                    "POST" => group.MapPost(attr.Pattern, handler),
                    "PUT" => group.MapPut(attr.Pattern, handler),
                    "DELETE" => group.MapDelete(attr.Pattern, handler),
                    _ => throw new NotSupportedException($"HTTP method '{attr.HttpMethod}' is not supported.")
                };
            }
        }

        return routes;
    }

    private static Type CreateDelegateType(MethodInfo method)
    {
        var paramTypes = method.GetParameters().Select(p => p.ParameterType).ToArray();
        var allTypes = paramTypes.Append(method.ReturnType).ToArray();

        return allTypes.Length switch
        {
            1 => typeof(Func<>).MakeGenericType(allTypes),
            2 => typeof(Func<,>).MakeGenericType(allTypes),
            3 => typeof(Func<,,>).MakeGenericType(allTypes),
            4 => typeof(Func<,,,>).MakeGenericType(allTypes),
            5 => typeof(Func<,,,,>).MakeGenericType(allTypes),
            6 => typeof(Func<,,,,,>).MakeGenericType(allTypes),
            7 => typeof(Func<,,,,,,>).MakeGenericType(allTypes),
            8 => typeof(Func<,,,,,,,>).MakeGenericType(allTypes),
            _ => throw new NotSupportedException($"Handler '{method.Name}' has too many parameters.")
        };
    }
}
