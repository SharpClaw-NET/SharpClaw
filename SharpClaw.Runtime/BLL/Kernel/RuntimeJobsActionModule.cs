using SharpClaw.Contracts.Modules;
using SharpClaw.Core.Kernel;

namespace SharpClaw.Runtime.BLL.Kernel;

/// <summary>Owns the Runtime registration of the package-defined typed Jobs actions.</summary>
internal sealed class RuntimeJobsActionModule : ISharpClawModule
{
    public const string ModuleId = "sharpclaw.runtime.jobs";

    private readonly Dictionary<string, ActionInterceptionCapabilities> _grants =
        new(StringComparer.Ordinal);
    private readonly List<KernelSensitiveActionApproval> _approvals = [];

    public ModuleIdentity Identity { get; } =
        new(ModuleId, "SharpClaw Runtime Jobs", "jobs");

    public IReadOnlyDictionary<string, ActionInterceptionCapabilities> Grants => _grants;

    public IReadOnlyList<KernelSensitiveActionApproval> Approvals => _approvals;

    public void Configure(ISharpClawModuleBuilder module)
    {
        ArgumentNullException.ThrowIfNull(module);
        AddFamily<SubmitFamily>(module, "jobs.submit");
        AddFamily<ValidateFamily>(module, "jobs.validate");
        AddFamily<IdentityCreateFamily>(module, "jobs.identity.create");
        AddFamily<QueuePersistFamily>(module, "jobs.queue.persist");
        AddFamily<HoldEvaluateFamily>(module, "jobs.hold.evaluate");
        AddFamily<HoldResolveFamily>(module, "jobs.hold.resolve");
        AddFamily<DispatchFamily>(module, "jobs.dispatch");
        AddFamily<StartFamily>(module, "jobs.start");
        AddFamily<HandlerInvokeFamily>(module, "jobs.handler.invoke");
        AddFamily<ProgressFamily>(module, "jobs.progress.report");
        AddFamily<ArtifactSealFamily>(module, "jobs.artifact.seal");
        AddFamily<CompleteFamily>(module, "jobs.complete");
        AddFamily<FailFamily>(module, "jobs.fail");
        AddFamily<CancelFamily>(module, "jobs.cancel");
        AddFamily<CancelRequestFamily>(module, "jobs.cancel.request");
        AddFamily<CancelApplyFamily>(module, "jobs.cancel.apply");
        AddFamily<PauseFamily>(module, "jobs.pause");
        AddFamily<StopFamily>(module, "jobs.stop");
        AddFamily<RecoveryFamily>(module, "jobs.recovery");
        AddFamily<RecoveryScanFamily>(module, "jobs.recovery.scan");
        AddFamily<RecoveryClassifyFamily>(module, "jobs.recovery.classify");
        AddFamily<RetryFamily>(module, "jobs.retry");
        AddFamily<RetryEvaluateFamily>(module, "jobs.retry.evaluate");
        AddFamily<RetryScheduleFamily>(module, "jobs.retry.schedule");
        AddFamily<ResumeFamily>(module, "jobs.resume");
        AddFamily<DeleteFamily>(module, "jobs.delete");
        AddFamily<ReadFamily>(module, "jobs.read");
        AddFamily<ListFamily>(module, "jobs.list");
        AddFamily<LogsReadFamily>(module, "jobs.logs.read");
        AddFamily<AuditReadFamily>(module, "jobs.audit.read");
        AddFamily<ArtifactReadFamily>(module, "jobs.artifact.read");
        AddFamily<EventDeliverFamily>(module, "jobs.event.deliver");
        AddFamily<StateTransitionFamily>(module, "jobs.state.transition");
        AddFamily<StateTransitionPrepareFamily>(module, "jobs.state.transition.prepare");
        AddFamily<StateTransitionCommitFamily>(module, "jobs.state.transition.commit");
        AddFamily<StateTransitionRollbackFamily>(module, "jobs.state.transition.rollback");
        AddFamily<PersistenceFamily>(module, "jobs.persistence");
        AddFamily<PersistencePrepareFamily>(module, "jobs.persistence.prepare");
        AddFamily<PersistenceCommitFamily>(module, "jobs.persistence.commit");
        AddFamily<PersistenceRollbackFamily>(module, "jobs.persistence.rollback");
        AddFamily<InterruptionCheckFamily>(module, "jobs.interruption.check");
        AddFamily<ExternalCallFamily>(module, "jobs.external_call");
        AddFamily<IrreversibleEffectFamily>(module, "jobs.irreversible_effect");
        AddFamily<ExternalEffectPrepareFamily>(module, "jobs.external_effect.prepare");
        AddFamily<ExternalEffectReceiptFamily>(module, "jobs.external_effect.receipt");
        AddFamily<ExternalEffectUncertainFamily>(module, "jobs.external_effect.uncertain");
    }

    private void AddFamily<TFamily>(ISharpClawModuleBuilder module, string family)
    {
        if (!SharpClawActionCatalog.JobsFamilies.Contains(family, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                $"The package-owned Jobs catalog does not define '{family}'.");
        }

        AddDescriptor(module, Descriptor<JobCheckpoint<RuntimeJobsInput<TFamily>>, JobCheckpoint<RuntimeJobsInput<TFamily>>>($"{family}.before"));
        AddDescriptor(module, Descriptor<RuntimeJobsInput<TFamily>, RuntimeJobsResult<TFamily>>(family));
        AddDescriptor(module, Descriptor<JobCheckpoint<RuntimeJobsResult<TFamily>>, JobCheckpoint<RuntimeJobsResult<TFamily>>>($"{family}.after"));
    }

    private void AddDescriptor<TAction, TResult>(
        ISharpClawModuleBuilder module,
        ActionDescriptor<TAction, TResult> descriptor)
    {
        module.Actions.Add(descriptor);
        _grants[descriptor.Key.Value] = descriptor.Capabilities;
        _approvals.Add(new KernelSensitiveActionApproval(
            Identity.Id,
            descriptor.Key,
            descriptor.Version,
            typeof(TAction).AssemblyQualifiedName!,
            typeof(TResult).AssemblyQualifiedName!,
            KernelSchemaIdentity.Action(descriptor)));
    }

    private static ActionDescriptor<TAction, TResult> Descriptor<TAction, TResult>(string key)
    {
        var entry = KernelActionCatalog.DescriptorFor(new SharpClawActionKey(key));
        return new(
            entry.Key,
            entry.Version,
            entry.Category,
            entry.Capabilities,
            entry.ContainsSensitiveData,
            entry.HasIrreversibleEffects,
            entry.RepeatPolicy,
            entry.ContinuationPolicy,
            entry.DefaultTimeout)
        {
            ProtocolVersionRange = ContractVersionRange.Exact(1),
            SafePoints = entry.SafePoints
        };
    }

    internal sealed record RuntimeJobsInput<TFamily>(object? Value);

    internal sealed record RuntimeJobsResult<TFamily>(object? Value);

    internal sealed record SubmitFamily;
    internal sealed record ValidateFamily;
    internal sealed record IdentityCreateFamily;
    internal sealed record QueuePersistFamily;
    internal sealed record HoldEvaluateFamily;
    internal sealed record HoldResolveFamily;
    internal sealed record DispatchFamily;
    internal sealed record StartFamily;
    internal sealed record HandlerInvokeFamily;
    internal sealed record ProgressFamily;
    internal sealed record ArtifactSealFamily;
    internal sealed record CompleteFamily;
    internal sealed record FailFamily;
    internal sealed record CancelFamily;
    internal sealed record CancelRequestFamily;
    internal sealed record CancelApplyFamily;
    internal sealed record PauseFamily;
    internal sealed record StopFamily;
    internal sealed record RecoveryFamily;
    internal sealed record RecoveryScanFamily;
    internal sealed record RecoveryClassifyFamily;
    internal sealed record RetryFamily;
    internal sealed record RetryEvaluateFamily;
    internal sealed record RetryScheduleFamily;
    internal sealed record ResumeFamily;
    internal sealed record DeleteFamily;
    internal sealed record ReadFamily;
    internal sealed record ListFamily;
    internal sealed record LogsReadFamily;
    internal sealed record AuditReadFamily;
    internal sealed record ArtifactReadFamily;
    internal sealed record EventDeliverFamily;
    internal sealed record StateTransitionFamily;
    internal sealed record StateTransitionPrepareFamily;
    internal sealed record StateTransitionCommitFamily;
    internal sealed record StateTransitionRollbackFamily;
    internal sealed record PersistenceFamily;
    internal sealed record PersistencePrepareFamily;
    internal sealed record PersistenceCommitFamily;
    internal sealed record PersistenceRollbackFamily;
    internal sealed record InterruptionCheckFamily;
    internal sealed record ExternalCallFamily;
    internal sealed record IrreversibleEffectFamily;
    internal sealed record ExternalEffectPrepareFamily;
    internal sealed record ExternalEffectReceiptFamily;
    internal sealed record ExternalEffectUncertainFamily;
}
