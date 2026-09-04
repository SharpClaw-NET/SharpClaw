using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.Kernel;
using SharpClaw.Core.Kernel;

namespace SharpClaw.Services;

/// <summary>Defines the host-owned services for the Uno client graph.</summary>
internal static class ClientActionServiceSet
{
    public const string SourceId = "sharpclaw.client";

    public static IServiceCollection Create(IClientActionContextSink? contextSink = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(new ClientActionObserver(contextSink));
        foreach (var action in ClientActionCatalog.All)
        {
            var ordering = new HookOrdering(
                $"{SourceId}-{action.Value}",
                HookPriority.Normal,
                [],
                [],
                TimeSpan.FromSeconds(5),
                HookFailurePolicy.FailAction);
            services.AddSingleton(new ActionHookBinding(
                SourceId,
                BehaviorTargetKind.Exact,
                action,
                null,
                typeof(ClientActionObserver),
                false,
                ordering,
                typeof(ClientActionObserver).AssemblyQualifiedName!));
        }

        return services;
    }

    public static KernelGraphCompileOptions CreateOptions()
    {
        var grants = ClientActionCatalog.All.ToDictionary(
            action => action.Value,
            static _ => ActionInterceptionCapabilities.Inspect |
                ActionInterceptionCapabilities.Wrap,
            StringComparer.Ordinal);

        return new KernelGraphCompileOptions
        {
            ActionRegistrationCapabilityGrants = new Dictionary<
                string,
                IReadOnlyDictionary<string, ActionInterceptionCapabilities>>
            {
                [SourceId] = grants,
            },
        };
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
