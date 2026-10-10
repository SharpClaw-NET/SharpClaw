using System.Security.Cryptography;
using System.Text.Json;
using SharpClaw.Contracts.Kernel;
using SharpClaw.Contracts.Providers;
using SharpClaw.Core.Kernel;

namespace SharpClaw.Runtime.BLL.Kernel;


/// <summary>Issues local host authority for one provider-created Tool action.</summary>
internal sealed class RuntimeKernelToolContextIssuer : IKernelToolContextIssuer
{
    public ValueTask<HostActionEntryRequestContext?> IssueAsync(
        KernelToolContextIssueRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (!request.IsWellFormed)
            return ValueTask.FromResult<HostActionEntryRequestContext?>(null);

        var now = DateTimeOffset.UtcNow;
        var parent = request.ParentActionContext;
        var deadline = parent?.Deadline ?? now.AddMinutes(1);
        if (deadline <= now)
            return ValueTask.FromResult<HostActionEntryRequestContext?>(null);

        var payload = JsonSerializer.SerializeToUtf8Bytes(request.Arguments);
        var descriptor = CreateToolDescriptor();
        var inputSchema = descriptor.InputSchema;
        if (inputSchema is null || string.IsNullOrWhiteSpace(inputSchema.ContentHash))
            return ValueTask.FromResult<HostActionEntryRequestContext?>(null);

        var context = new HostActionEntryRequestContext(
            Guid.NewGuid(),
            Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
            HostActionEntryIngress.Tool,
            request.InvocationId,
            Guid.NewGuid(),
            Guid.NewGuid(),
            parent?.Caller ?? RequestPrincipal.Anonymous,
            parent?.Features ?? ExtensionFeatureSet.Empty,
            parent?.TraceId ?? Guid.NewGuid(),
            parent?.IdempotencyKey ?? Guid.NewGuid(),
            deadline,
            deadline)
        {
            Contribution = new HostActionEntryContribution(
                new HostActionEntryIngressBinding(
                    HostActionEntryIngress.Tool,
                    request.ToolName,
                    request.ConversationId?.ToString("D")),
                new HostActionEntryLineage(
                    SharpClawActions.Tools.Invoke,
                    descriptor.Version,
                    HostActionEntryAuthorityValidator.ComputeDescriptorHash(descriptor),
                    typeof(ToolInvocation).AssemblyQualifiedName ?? typeof(ToolInvocation).FullName!,
                    inputSchema.Version,
                    inputSchema.ContentHash,
                    Convert.ToHexString(SHA256.HashData(payload)),
                    payload.Length)),
            ParentInvocationId = parent?.InvocationId,
            Depth = parent is null ? 0 : parent.Depth + 1,
            Attempt = parent?.Attempt > 0 ? parent.Attempt : 1,
        };

        return ValueTask.FromResult<HostActionEntryRequestContext?>(context);
    }

    private static ActionDescriptor<ToolInvocation, ToolInvocationOutcome> CreateToolDescriptor()
    {
        var manifest = KernelActionCatalog.DescriptorFor(SharpClawActions.Tools.Invoke);
        return new ActionDescriptor<ToolInvocation, ToolInvocationOutcome>(
            manifest.Key,
            manifest.Version,
            manifest.Category,
            manifest.Capabilities,
            manifest.ContainsSensitiveData,
            manifest.HasIrreversibleEffects,
            manifest.RepeatPolicy,
            manifest.ContinuationPolicy,
            manifest.DefaultTimeout)
        {
            ProtocolVersionRange = ContractVersionRange.Exact(1),
            SafePoints = manifest.SafePoints,
            InputSchema = manifest.InputSchema,
            ResultSchema = manifest.ResultSchema,
        };
    }
}
