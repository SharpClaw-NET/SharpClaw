using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.Modules;
using SharpClaw.Core.Kernel;

namespace SharpClaw.Runtime.BLL.Kernel;

internal sealed record RuntimeModuleActionDeclaration(
    string OwnerModuleId,
    SharpClawActionKey Key,
    int Version,
    Type ActionType,
    Type ResultType,
    bool ContainsSensitiveData);

internal sealed record RuntimeModuleEventDeclaration(
    string OwnerModuleId,
    SharpClawEventKey Key,
    int Version,
    Type EventType,
    bool ContainsSensitiveData);

internal sealed class RuntimeModuleContractCapture(string moduleId)
{
    public string ModuleId { get; } = moduleId;

    public List<RuntimeModuleActionDeclaration> Actions { get; } = [];

    public List<RuntimeModuleEventDeclaration> Events { get; } = [];
}

internal sealed class RuntimeModuleContractModule(
    ISharpClawModule inner,
    RuntimeModuleContractCapture capture) : ISharpClawModule
{
    public ModuleIdentity Identity => inner.Identity;

    public void Configure(ISharpClawModuleBuilder module)
    {
        ArgumentNullException.ThrowIfNull(module);
        inner.Configure(new RuntimeModuleContractBuilder(module, capture));
    }

    public ValueTask StartAsync(ModuleStartContext context, CancellationToken cancellationToken) =>
        inner.StartAsync(context, cancellationToken);

    public ValueTask StopAsync(CancellationToken cancellationToken) =>
        inner.StopAsync(cancellationToken);
}

internal sealed class RuntimeModuleContractBuilder :
    ISharpClawModuleBuilder,
    IBoundModuleContributionBuilder
{
    private readonly ISharpClawModuleBuilder inner;

    public RuntimeModuleContractBuilder(
        ISharpClawModuleBuilder inner,
        RuntimeModuleContractCapture capture)
    {
        this.inner = inner;
        Services = inner.Services;
        Contracts = inner.Contracts;
        Storage = inner.Storage;
        Actions = new RuntimeModuleActionDefinitionBuilder(inner.Actions, capture);
        Hooks = inner.Hooks;
        Events = new RuntimeModuleEventDefinitionBuilder(inner.Events, capture);
        Tools = inner.Tools;
        Chat = inner.Chat;
    }

    public IServiceCollection Services { get; }

    public IModuleContractBuilder Contracts { get; }

    public IModuleStorageBuilder Storage { get; }

    public IActionDefinitionBuilder Actions { get; }

    public IActionHookBuilder Hooks { get; }

    public IEventDefinitionBuilder Events { get; }

    public IToolContributionBuilder Tools { get; }

    public IChatLifecycleBuilder Chat { get; }

    public void AddActionHook(
        SidecarActionSubscription subscription,
        IAnyActionInterceptor interceptor,
        string handlerId) => Bound.AddActionHook(subscription, interceptor, handlerId);

    public void AddEventInterceptor(
        SidecarEventSubscription subscription,
        IAnyEventInterceptor interceptor,
        string handlerId) => Bound.AddEventInterceptor(subscription, interceptor, handlerId);

    public void AddEventListener(
        SidecarEventSubscription subscription,
        IAnyEventListener listener,
        string handlerId) => Bound.AddEventListener(subscription, listener, handlerId);

    public void AddTool(
        ToolDescriptor descriptor,
        IToolHandler handler,
        string handlerId) => Bound.AddTool(descriptor, handler, handlerId);

    public void UseConversationResolver(
        IConversationResolver resolver,
        ExclusiveRegistration registration,
        string handlerId) => Bound.UseConversationResolver(resolver, registration, handlerId);

    public void UseChatProfileResolver(
        IChatProfileResolver resolver,
        ExclusiveRegistration registration,
        string handlerId) => Bound.UseChatProfileResolver(resolver, registration, handlerId);

    public void UseConversationStore(
        IConversationStore store,
        string handlerId) => Bound.UseConversationStore(store, handlerId);

    public void AddContextContributor(
        IChatContextContributor contributor,
        string handlerId) => Bound.AddContextContributor(contributor, handlerId);

    private IBoundModuleContributionBuilder Bound =>
        inner as IBoundModuleContributionBuilder
        ?? throw new KernelGraphCompilationException(
            "The kernel module builder does not support bound external contributions.");
}

internal sealed class RuntimeModuleActionDefinitionBuilder(
    IActionDefinitionBuilder inner,
    RuntimeModuleContractCapture capture) : IActionDefinitionBuilder
{
    public void Add<TAction, TResult>(ActionDescriptor<TAction, TResult> descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        capture.Actions.Add(new RuntimeModuleActionDeclaration(
            capture.ModuleId,
            descriptor.Key,
            descriptor.Version,
            typeof(TAction),
            typeof(TResult),
            descriptor.ContainsSensitiveData));
        inner.Add(descriptor);
    }
}

internal sealed class RuntimeModuleEventDefinitionBuilder(
    IEventDefinitionBuilder inner,
    RuntimeModuleContractCapture capture) : IEventDefinitionBuilder
{
    public void Add<TEvent>(EventDescriptor<TEvent> descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        capture.Events.Add(new RuntimeModuleEventDeclaration(
            capture.ModuleId,
            descriptor.Key,
            descriptor.Version,
            typeof(TEvent),
            descriptor.ContainsSensitiveData));
        inner.Add(descriptor);
    }

    public IEventHookRegistrationBuilder For(SharpClawEventKey key) => inner.For(key);

    public IEventHookRegistrationBuilder Category(string category) => inner.Category(category);

    public IEventHookRegistrationBuilder AnyEvent() => inner.AnyEvent();
}

internal static class RuntimeModuleContractManifest
{
    public static void Validate(
        KernelGraph graph,
        IReadOnlyList<RuntimeModuleContractCapture> captures)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(captures);

        var duplicateActions = captures
            .SelectMany(capture => capture.Actions)
            .GroupBy(declaration => declaration.Key.Value, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToArray();
        if (duplicateActions.Length > 0)
        {
            throw new KernelGraphCompilationException(
                "The module action registry contains duplicate declared keys: " +
                string.Join(", ", duplicateActions));
        }

        var duplicateEvents = captures
            .SelectMany(capture => capture.Events)
            .GroupBy(declaration => declaration.Key.Value, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToArray();
        if (duplicateEvents.Length > 0)
        {
            throw new KernelGraphCompilationException(
                "The module event registry contains duplicate declared keys: " +
                string.Join(", ", duplicateEvents));
        }

        var missingActions = captures
            .SelectMany(capture => capture.Actions)
            .Where(declaration => !graph.ContainsAction(declaration.Key))
            .Select(declaration => $"{declaration.OwnerModuleId}:{declaration.Key.Value}")
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        if (missingActions.Length > 0)
        {
            throw new KernelGraphCompilationException(
                "The compiled graph does not contain declared module actions: " +
                string.Join(", ", missingActions));
        }

        var missingEvents = captures
            .SelectMany(capture => capture.Events)
            .Where(declaration => !graph.ContainsEvent(declaration.Key))
            .Select(declaration => $"{declaration.OwnerModuleId}:{declaration.Key.Value}")
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        if (missingEvents.Length > 0)
        {
            throw new KernelGraphCompilationException(
                "The compiled graph does not contain declared module events: " +
                string.Join(", ", missingEvents));
        }
    }
}
