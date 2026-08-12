using SharpClaw.Contracts.Modules;
using SharpClaw.Core.Kernel;

namespace SharpClaw.Services;

/// <summary>Defines the host-owned module set for the Uno client graph.</summary>
internal static class ClientActionModuleSet
{
    public const string ModuleId = "sharpclaw.client";

    public static IReadOnlyList<ISharpClawModule> Create(
        IClientActionContextSink? contextSink = null) =>
        [new ClientActionModule(contextSink)];

    public static KernelGraphCompileOptions CreateOptions()
    {
        var grants = ClientActionCatalog.All.ToDictionary(
            action => action.Value,
            static _ => ActionInterceptionCapabilities.Inspect |
                ActionInterceptionCapabilities.Wrap,
            StringComparer.Ordinal);

        return new KernelGraphCompileOptions
        {
            ActionModuleCapabilityGrants = new Dictionary<
                string,
                IReadOnlyDictionary<string, ActionInterceptionCapabilities>>
            {
                [ModuleId] = grants,
            },
        };
    }

    private sealed class ClientActionModule(IClientActionContextSink? contextSink) : ISharpClawModule
    {
        public ModuleIdentity Identity { get; } =
            new(ModuleId, "SharpClaw Uno client", "client");

        public void Configure(ISharpClawModuleBuilder module)
        {
            module.Services.AddSingleton(new ClientActionObserver(contextSink));
            foreach (var action in ClientActionCatalog.All)
            {
                module.Hooks.For(action).Use<ClientActionObserver>(new HookOrdering(
                    $"{ModuleId}-{action.Value}",
                    HookPriority.Normal,
                    [],
                    [],
                    TimeSpan.FromSeconds(5),
                    HookFailurePolicy.FailAction));
            }
        }

        public ValueTask StartAsync(
            ModuleStartContext context,
            CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public ValueTask StopAsync(CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
    }

    internal interface IClientActionContextSink
    {
        void Observe(ActionContext<KernelActionEnvelope> context);
    }

    private sealed class ClientActionObserver(IClientActionContextSink? contextSink)
        : IActionInterceptor<KernelActionEnvelope, object>
    {
        private readonly IClientActionContextSink? _contextSink = contextSink;

        public ValueTask<IActionOutcome<object>> InvokeAsync(
            ActionContext<KernelActionEnvelope> context,
            IActionControl<KernelActionEnvelope, object> control,
            CancellationToken cancellationToken) =>
            ProceedAsync(context, control, cancellationToken);

        private async ValueTask<IActionOutcome<object>> ProceedAsync(
            ActionContext<KernelActionEnvelope> context,
            IActionControl<KernelActionEnvelope, object> control,
            CancellationToken cancellationToken)
        {
            _contextSink?.Observe(context);
            return await control.ProceedAsync(cancellationToken);
        }
    }
}
