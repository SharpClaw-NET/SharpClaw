using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.Kernel;
using SharpClaw.Core.Kernel;
using SharpClaw.Runtime.BLL.Kernel;

namespace SharpClaw.Runtime.Host;

internal sealed class RuntimeHostActionContextAccessor
{
    private readonly AsyncLocal<HostActionEntryRequestContext?> _current = new();

    public HostActionEntryRequestContext? Current => _current.Value;

    public IDisposable Push(HostActionEntryRequestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var previous = _current.Value;
        _current.Value = context;
        return new RestoreScope(this, previous);
    }

    private sealed class RestoreScope(
        RuntimeHostActionContextAccessor owner,
        HostActionEntryRequestContext? previous) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                owner._current.Value = previous;
        }
    }
}

internal sealed class RuntimeHostActionEntry(
    IServiceProvider services,
    RuntimeHostActionContextAccessor contexts) : IHostActionEntry
{
    public ValueTask<IActionOutcome<TResult>> InvokeAsync<TAction, TResult>(
        HostActionEntryRequest<TAction, TResult> request,
        IHostActionEntryTerminal<TAction, TResult> terminal,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(terminal);
        var current = contexts.Current;
        if (current is null ||
            !request.IsWellFormed(DateTimeOffset.UtcNow) ||
            !HostActionEntryAuthorityValidator.MatchesRequestContext(request, current))
        {
            return ValueTask.FromResult<IActionOutcome<TResult>>(
                KernelActionOutcome<TResult>.Failed(
                    "host_action_invalid_request",
                    "The action request does not match the active host authority."));
        }

        var runtime = services.GetRequiredService<RuntimeKernelAdapter>();
        ActionDescriptor<TAction, TResult> descriptor;
        try
        {
            descriptor = runtime.Graph.GetActionDescriptor<TAction, TResult>(
                request.Descriptor.Key);
        }
        catch (KernelActionExecutionException exception)
        {
            return ValueTask.FromResult<IActionOutcome<TResult>>(
                KernelActionOutcome<TResult>.Failed(
                    "host_action_unknown_descriptor",
                    exception.Message));
        }

        if (descriptor != request.Descriptor)
        {
            return ValueTask.FromResult<IActionOutcome<TResult>>(
                KernelActionOutcome<TResult>.Failed(
                    "host_action_descriptor_mismatch",
                    "The action descriptor does not match the active host graph."));
        }

        return runtime.RunHostActionEntryAsync(
            ToExecutionContext(current),
            descriptor,
            request.Action,
            this,
            terminal,
            cancellationToken);
    }

    public ValueTask<IActionOutcome<TResult>> InvokeNestedAsync<TParentAction, TAction, TResult>(
        HostActionEntryNestedRequest<TParentAction, TAction, TResult> request,
        IHostActionEntryTerminal<TAction, TResult> terminal,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(terminal);
        var current = contexts.Current;
        if (current is null ||
            !request.IsWellFormed(DateTimeOffset.UtcNow) ||
            !ReferenceEquals(request.ParentContext.HostActionEntry, this))
        {
            return ValueTask.FromResult<IActionOutcome<TResult>>(
                KernelActionOutcome<TResult>.Failed(
                    "host_action_invalid_parent",
                    "The nested action does not match the active host authority."));
        }

        var runtime = services.GetRequiredService<RuntimeKernelAdapter>();
        ActionDescriptor<TAction, TResult> descriptor;
        try
        {
            descriptor = runtime.Graph.GetActionDescriptor<TAction, TResult>(request.ActionKey);
        }
        catch (KernelActionExecutionException exception)
        {
            return ValueTask.FromResult<IActionOutcome<TResult>>(
                KernelActionOutcome<TResult>.Failed(
                    "host_action_unknown_descriptor",
                    exception.Message));
        }

        if (descriptor.Version != request.ActionVersion)
        {
            return ValueTask.FromResult<IActionOutcome<TResult>>(
                KernelActionOutcome<TResult>.Failed(
                    "host_action_descriptor_mismatch",
                    "The nested action version does not match the active host graph."));
        }

        var parent = request.ParentContext;
        return runtime.RunHostActionEntryAsync(
            new KernelActionExecutionContext(
                parent.Caller,
                parent.Features,
                parent.TraceId,
                parent.IdempotencyKey,
                current),
            descriptor,
            request.Action,
            this,
            terminal,
            cancellationToken);
    }

    private static KernelActionExecutionContext ToExecutionContext(
        HostActionEntryRequestContext context) =>
        new(
            context.Caller,
            context.Features,
            context.TraceId,
            context.IdempotencyKey,
            context);
}
