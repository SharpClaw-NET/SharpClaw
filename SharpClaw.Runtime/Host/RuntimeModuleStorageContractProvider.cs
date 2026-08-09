using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.Modules;

namespace SharpClaw.Runtime.Host;

/// <summary>Collects storage declarations before the Core module graph is compiled.</summary>
internal sealed class RuntimeModuleStorageContractProvider : IModuleStorageContractProvider
{
    private readonly IReadOnlyList<ModuleStorageContractDescriptor> _contracts;

    public RuntimeModuleStorageContractProvider(IEnumerable<ISharpClawModule> modules)
    {
        ArgumentNullException.ThrowIfNull(modules);

        var collector = new StorageDeclarationBuilder();
        foreach (var module in modules.OrderBy(value => value.Identity.Id, StringComparer.Ordinal))
            module.Configure(collector);

        _contracts = collector.StorageContracts
            .GroupBy(contract => (contract.ModuleId, contract.StorageName))
            .Select(group => group.Count() == 1
                ? group.Single()
                : throw new InvalidOperationException(
                    $"Module storage contract '{group.Key.ModuleId}/{group.Key.StorageName}' was declared more than once."))
            .ToArray();
    }

    public IReadOnlyList<ModuleStorageContractDescriptor> GetStorageContracts() => _contracts;

    public ModuleStorageContractDescriptor? FindStorageContract(
        string moduleId,
        string storageName) =>
        _contracts.FirstOrDefault(contract =>
            string.Equals(contract.ModuleId, moduleId, StringComparison.Ordinal)
            && string.Equals(contract.StorageName, storageName, StringComparison.Ordinal));

    private sealed class StorageDeclarationBuilder : ISharpClawModuleBuilder
    {
        public IServiceCollection Services { get; } = new ServiceCollection();
        public IModuleContractBuilder Contracts { get; } = NoOpModuleContractBuilder.Instance;
        public StorageDeclarationCollector Storage { get; } = new();
        IModuleStorageBuilder ISharpClawModuleBuilder.Storage => Storage;
        public IActionDefinitionBuilder Actions { get; } = NoOpActionDefinitionBuilder.Instance;
        public IActionHookBuilder Hooks { get; } = NoOpActionHookBuilder.Instance;
        public IEventDefinitionBuilder Events { get; } = NoOpEventDefinitionBuilder.Instance;
        public IToolContributionBuilder Tools { get; } = NoOpToolContributionBuilder.Instance;
        public IChatLifecycleBuilder Chat { get; } = NoOpChatLifecycleBuilder.Instance;

        public IReadOnlyList<ModuleStorageContractDescriptor> StorageContracts => Storage.Contracts;
    }

    private sealed class StorageDeclarationCollector : IModuleStorageBuilder
    {
        private readonly List<ModuleStorageContractDescriptor> _contracts = [];

        public IReadOnlyList<ModuleStorageContractDescriptor> Contracts => _contracts;

        public void Add(ModuleStorageContractDescriptor contract)
        {
            ArgumentNullException.ThrowIfNull(contract);
            _contracts.Add(contract);
        }
    }

    private sealed class NoOpModuleContractBuilder : IModuleContractBuilder
    {
        public static NoOpModuleContractBuilder Instance { get; } = new();

        public void Export<T>(string contractName, int schemaVersion = 1, int maxBytes = 65_536) { }

        public void Require<T>(
            string contractName,
            int minimumSchemaVersion = 1,
            bool optional = false) { }
    }

    private sealed class NoOpActionDefinitionBuilder : IActionDefinitionBuilder
    {
        public static NoOpActionDefinitionBuilder Instance { get; } = new();

        public void Add<TAction, TResult>(ActionDescriptor<TAction, TResult> descriptor) { }
    }

    private sealed class NoOpActionHookBuilder : IActionHookBuilder
    {
        public static NoOpActionHookBuilder Instance { get; } = new();

        public IActionHookRegistrationBuilder For(SharpClawActionKey key) => NoOpActionHookRegistrationBuilder.Instance;
        public IActionHookRegistrationBuilder Category(string category) => NoOpActionHookRegistrationBuilder.Instance;
        public IActionHookRegistrationBuilder AnyAction() => NoOpActionHookRegistrationBuilder.Instance;
    }

    private sealed class NoOpActionHookRegistrationBuilder : IActionHookRegistrationBuilder
    {
        public static NoOpActionHookRegistrationBuilder Instance { get; } = new();

        public void Use<TInterceptor>(HookOrdering ordering) { }
        public void UseAny<TInterceptor>(HookOrdering ordering) { }
    }

    private sealed class NoOpEventDefinitionBuilder : IEventDefinitionBuilder
    {
        public static NoOpEventDefinitionBuilder Instance { get; } = new();

        public void Add<TEvent>(EventDescriptor<TEvent> descriptor) { }
        public IEventHookRegistrationBuilder For(SharpClawEventKey key) => NoOpEventHookRegistrationBuilder.Instance;
        public IEventHookRegistrationBuilder Category(string category) => NoOpEventHookRegistrationBuilder.Instance;
        public IEventHookRegistrationBuilder AnyEvent() => NoOpEventHookRegistrationBuilder.Instance;
    }

    private sealed class NoOpEventHookRegistrationBuilder : IEventHookRegistrationBuilder
    {
        public static NoOpEventHookRegistrationBuilder Instance { get; } = new();

        public void Intercept<TInterceptor>(HookOrdering ordering) { }
        public void InterceptAny<TInterceptor>(HookOrdering ordering) { }
        public void Listen<TListener>(EventDelivery delivery, HookOrdering ordering) { }
        public void ListenAny<TListener>(EventDelivery delivery, HookOrdering ordering) { }
    }

    private sealed class NoOpToolContributionBuilder : IToolContributionBuilder
    {
        public static NoOpToolContributionBuilder Instance { get; } = new();

        public void Add<THandler>(ToolDescriptor descriptor) where THandler : IToolHandler { }
    }

    private sealed class NoOpChatLifecycleBuilder : IChatLifecycleBuilder
    {
        public static NoOpChatLifecycleBuilder Instance { get; } = new();

        public void UseConversationResolver<TResolver>(ExclusiveRegistration registration)
            where TResolver : IConversationResolver { }

        public void UseChatProfileResolver<TResolver>(ExclusiveRegistration registration)
            where TResolver : IChatProfileResolver { }

        public void AddContextContributor<TContributor>() where TContributor : IChatContextContributor { }
    }
}
