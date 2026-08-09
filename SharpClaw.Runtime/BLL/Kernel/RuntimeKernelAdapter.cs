using Microsoft.Extensions.Configuration;
using SharpClaw.Contracts.Modules;
using SharpClaw.Contracts.Providers;
using SharpClaw.Core.Kernel;
using SharpClaw.Shared.Instances;

namespace SharpClaw.Runtime.BLL.Kernel;

/// <summary>Composes one Runtime-owned adapter over the Core kernel.</summary>
public sealed class RuntimeKernelAdapter
{
    private readonly KernelModuleRegistry _moduleRegistry;
    private bool _started;

    public RuntimeKernelAdapter(
        IConfiguration configuration,
        IServiceProvider hostServices,
        IConversationStore conversationStore,
        IEnumerable<ISharpClawModule> modules,
        SharpClawInstancePaths instancePaths,
        IRuntimeProviderClientFactory providerClientFactory)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(hostServices);
        ArgumentNullException.ThrowIfNull(conversationStore);
        ArgumentNullException.ThrowIfNull(modules);
        ArgumentNullException.ThrowIfNull(instancePaths);
        ArgumentNullException.ThrowIfNull(providerClientFactory);

        _moduleRegistry = new KernelModuleRegistry();
        foreach (var module in modules.OrderBy(value => value.Identity.Id, StringComparer.Ordinal))
            _moduleRegistry.Add(module);

        Graph = _moduleRegistry.Compile(hostServices);
        var plugins = (Graph.GetService(typeof(IEnumerable<IProviderPlugin>)) as IEnumerable<IProviderPlugin>)
            ?.ToArray()
            ?? [];
        ValidateConfiguredProviders(configuration, plugins);
        var providerClient = providerClientFactory.Create(configuration, plugins);
        var conversationResolver = ResolveConversationResolver(Graph, instancePaths);
        var profileResolver = ResolveProfileResolver(Graph, configuration);

        Kernel = DirectChatKernelFactory.CreateFromGraph(
            Graph,
            new ProviderKernelTransport(providerClient),
            conversationResolver,
            profileResolver,
            conversationStore);
    }

    public KernelGraph Graph { get; }

    public DirectChatKernel Kernel { get; }

    public async ValueTask StartAsync(
        string hostVersion,
        RequestPrincipal? caller = null,
        ExtensionFeatureSet? features = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostVersion);
        if (_started)
            throw new InvalidOperationException("The Runtime kernel has already started.");

        await _moduleRegistry.StartAsync(
            Graph,
            new KernelActionExecutionContext(
                caller ?? RequestPrincipal.Anonymous,
                features ?? ExtensionFeatureSet.Empty,
                Guid.NewGuid(),
                Guid.NewGuid()),
            hostVersion,
            features ?? ExtensionFeatureSet.Empty,
            cancellationToken);
        _started = true;
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        if (!_started)
            return;

        await _moduleRegistry.StopAsync(
            new KernelActionExecutionContext(
                RequestPrincipal.Anonymous,
                ExtensionFeatureSet.Empty,
                Guid.NewGuid(),
                Guid.NewGuid()),
            cancellationToken);
        _started = false;
    }

    private static IConversationResolver ResolveConversationResolver(
        KernelGraph graph,
        SharpClawInstancePaths instancePaths) =>
        graph.Modules.ConversationResolver is { } resolverType
            ? (IConversationResolver)(graph.GetService(resolverType)
                ?? throw new KernelGraphCompilationException(
                    $"Conversation resolver '{resolverType.FullName}' is not registered."))
            : new SingleConversationResolver(ResolveDefaultConversationId(instancePaths));

    private static Guid ResolveDefaultConversationId(SharpClawInstancePaths instancePaths)
    {
        if (!Guid.TryParse(instancePaths.Manifest.InstanceId, out var conversationId)
            || conversationId == Guid.Empty)
        {
            throw new InvalidOperationException(
                $"The Runtime instance manifest '{instancePaths.ManifestPath}' has no valid instance identifier.");
        }

        return conversationId;
    }

    private static IChatProfileResolver ResolveProfileResolver(
        KernelGraph graph,
        IConfiguration configuration) =>
        graph.Modules.ProfileResolver is { } resolverType
            ? (IChatProfileResolver)(graph.GetService(resolverType)
                ?? throw new KernelGraphCompilationException(
                    $"Chat profile resolver '{resolverType.FullName}' is not registered."))
            : new FixedChatProfileResolver(CreateProfile(configuration));

    private static ChatProfile CreateProfile(IConfiguration configuration)
    {
        var providerKey = configuration["Provider:Key"]
            ?? configuration["Providers:Default"]
            ?? "unconfigured";
        var modelName = configuration["Provider:Model"];
        return new ChatProfile(
            providerKey,
            Guid.Empty,
            modelName,
            configuration["Provider:SystemPrompt"]);
    }

    private static void ValidateConfiguredProviders(
        IConfiguration configuration,
        IReadOnlyList<IProviderPlugin> plugins)
    {
        var duplicateProviderKeys = plugins
            .GroupBy(plugin => plugin.ProviderKey, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .OrderBy(key => key, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (duplicateProviderKeys.Length > 0)
        {
            throw new InvalidOperationException(
                "Duplicate provider registrations were found: "
                + string.Join(", ", duplicateProviderKeys));
        }

        var providerKey = configuration["Provider:Key"]
            ?? configuration["Providers:Default"];
        if (string.IsNullOrWhiteSpace(providerKey))
        {
            throw new InvalidOperationException(
                "Provider:Key or Providers:Default must be configured before Runtime readiness.");
        }

        if (!plugins.Any(plugin => string.Equals(
                plugin.ProviderKey,
                providerKey,
                StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                $"Configured provider '{providerKey}' is not registered by an enabled in-process module.");
        }
    }
}
