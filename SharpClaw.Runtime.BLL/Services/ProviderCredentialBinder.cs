using System.Reflection;
using System.Runtime.ExceptionServices;
using SharpClaw.Contracts.Providers;

namespace SharpClaw.Runtime.BLL.Services;

internal static class ProviderCredentialBinder
{
    private static readonly Type[] BoundSignature =
    [
        typeof(ProviderClientOptions),
        typeof(string)
    ];

    public static IProviderApiClient CreateClient(
        IProviderPlugin plugin,
        ProviderClientOptions options,
        string credential)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        ArgumentNullException.ThrowIfNull(options);

        if (!plugin.RequiresApiKey)
            return plugin.CreateClient(options);

        EnsureCredential(plugin, credential);
        var method = FindBoundMethod(plugin, nameof(IProviderPlugin.CreateClient), typeof(IProviderApiClient))
            ?? throw MissingBinding(plugin);

        return InvokeBound<IProviderApiClient>(plugin, method, options, credential);
    }

    public static IProviderCostFeed? CreateCostFeed(
        IProviderPlugin plugin,
        ProviderClientOptions options,
        string credential)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        ArgumentNullException.ThrowIfNull(options);

        if (!plugin.SupportsCostFeed)
            return null;

        if (!plugin.RequiresApiKey)
            return plugin.CreateCostFeed(options);

        EnsureCredential(plugin, credential);
        var method = FindBoundMethod(plugin, nameof(IProviderPlugin.CreateCostFeed), typeof(IProviderCostFeed))
            ?? throw MissingBinding(plugin);

        return InvokeBound<IProviderCostFeed?>(plugin, method, options, credential);
    }

    private static void EnsureCredential(IProviderPlugin plugin, string credential)
    {
        if (!string.IsNullOrWhiteSpace(credential))
            return;

        throw new InvalidOperationException(
            $"Provider '{plugin.ProviderKey}' requires credentials, but no credentials are configured.");
    }

    private static InvalidOperationException MissingBinding(IProviderPlugin plugin) =>
        new($"Provider '{plugin.ProviderKey}' requires credentials, but its plugin does not support host-side credential binding.");

    private static MethodInfo? FindBoundMethod(
        IProviderPlugin plugin,
        string name,
        Type expectedReturnType)
    {
        return plugin.GetType()
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .FirstOrDefault(method =>
            {
                if (method.Name != name)
                    return false;

                var parameters = method.GetParameters();
                return parameters.Length == BoundSignature.Length
                    && parameters[0].ParameterType == BoundSignature[0]
                    && parameters[1].ParameterType == BoundSignature[1]
                    && expectedReturnType.IsAssignableFrom(method.ReturnType);
            });
    }

    private static T InvokeBound<T>(
        IProviderPlugin plugin,
        MethodInfo method,
        ProviderClientOptions options,
        string credential)
    {
        try
        {
            return (T)method.Invoke(plugin, [options, credential])!;
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }
}
