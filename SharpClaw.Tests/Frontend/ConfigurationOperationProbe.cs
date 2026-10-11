using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.Kernel;
using SharpClaw.Core.Kernel;
using SharpClaw.Services;

namespace SharpClaw.Tests.Frontend;

internal sealed class ConfigurationOperationProbe(string? rejectedOperation = null)
{
    private const string SourceId = "configuration-race-test";
    private readonly string? _rejectedOperation = rejectedOperation;
    private readonly ConcurrentDictionary<string, TaskCompletionSource> _signals = new(StringComparer.Ordinal);
    internal ExecutionError Rejection { get; } = new("CONTROLLED_CLOSE_REJECTED", "Controlled close receipt rejection.");

    internal Task WaitForPhaseAsync(string operation, SharpClawActionKey phase) =>
        Signal(operation, phase).Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.CurrentContext.CancellationToken);

    internal ClientActionDispatcher CreateDispatcher()
    {
        var services = ClientActionServiceSet.Create();
        services.AddSingleton(this);
        services.AddSingleton(static provider => new Interceptor(provider.GetRequiredService<ConfigurationOperationProbe>()));
        foreach (var key in ClientActionCatalog.All)
            services.AddSingleton(new ActionHookBinding(SourceId, BehaviorTargetKind.Exact, key, null,
                typeof(Interceptor), false, new HookOrdering("configuration-race-" + key.Value), typeof(Interceptor).AssemblyQualifiedName!));
        var productionGrants = ClientActionServiceSet.CreateOptions().ActionRegistrationCapabilityGrants
            ?? throw new AssertionException("The production client graph must declare its interception capabilities.");
        var grants = productionGrants.ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal);
        grants[SourceId] = ClientActionCatalog.All.ToDictionary(static key => key.Value,
            static _ => ActionInterceptionCapabilities.Inspect | ActionInterceptionCapabilities.Wrap, StringComparer.Ordinal);
        return new ClientActionDispatcher(services, new KernelGraphCompileOptions { ActionRegistrationCapabilityGrants = grants });
    }

    private TaskCompletionSource Signal(string operation, SharpClawActionKey phase) => _signals.GetOrAdd(
        operation + ":" + phase.Value, static _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

    private sealed class Interceptor(ConfigurationOperationProbe probe) : IActionInterceptor<KernelActionEnvelope, object>
    {
        public async ValueTask<IActionOutcome<object>> InvokeAsync(ActionContext<KernelActionEnvelope> context,
            IActionControl<KernelActionEnvelope, object> control, CancellationToken cancellationToken)
        {
            var operation = context.Action.Payload switch
            {
                ClientCommandInvocation command => command.Operation,
                ClientCommandSignal signal => signal.Operation,
                _ => null,
            };
            if (operation is not null)
                probe.Signal(operation, context.ActionKey).TrySetResult();
            if (context.ActionKey == ClientActionCatalog.CommandDispatch &&
                string.Equals(operation, probe._rejectedOperation, StringComparison.Ordinal) && operation is not null)
                return control.Fail(probe.Rejection);
            return await control.ProceedAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
