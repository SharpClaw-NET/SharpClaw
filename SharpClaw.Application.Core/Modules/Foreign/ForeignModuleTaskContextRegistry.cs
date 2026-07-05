using System.Collections.Concurrent;
using SharpClaw.Contracts.Tasks;

namespace SharpClaw.Application.Core.Modules.Foreign;

public sealed class ForeignModuleTaskContextRegistry
{
    private readonly ConcurrentDictionary<string, ITaskOperationExecutionContext> _contexts = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, RegisteredEventHandler> _eventHandlers = new(StringComparer.Ordinal);

    internal ForeignModuleTaskContextRegistration Register(ITaskOperationExecutionContext context)
    {
        var contextId = Guid.NewGuid().ToString("N");
        _contexts[contextId] = context;
        return new ForeignModuleTaskContextRegistration(this, contextId);
    }

    internal bool TryGetContext(string contextId, out ITaskOperationExecutionContext context) =>
        _contexts.TryGetValue(contextId, out context!);

    internal bool TryGetEventHandler(
        string handlerId,
        out ITaskOperationExecutionContext context,
        out ITaskEventHandler handler)
    {
        if (_eventHandlers.TryGetValue(handlerId, out var registered)
            && _contexts.TryGetValue(registered.ContextId, out context!))
        {
            handler = registered.Handler;
            return true;
        }

        context = null!;
        handler = null!;
        return false;
    }

    private string RegisterEventHandler(string contextId, ITaskEventHandler handler)
    {
        var handlerId = Guid.NewGuid().ToString("N");
        _eventHandlers[handlerId] = new RegisteredEventHandler(contextId, handler);
        return handlerId;
    }

    private void Remove(string contextId, IReadOnlyCollection<string> handlerIds)
    {
        _contexts.TryRemove(contextId, out _);
        foreach (var handlerId in handlerIds)
            _eventHandlers.TryRemove(handlerId, out _);
    }

    private sealed record RegisteredEventHandler(string ContextId, ITaskEventHandler Handler);

    internal sealed class ForeignModuleTaskContextRegistration : IDisposable
    {
        private readonly ForeignModuleTaskContextRegistry _registry;
        private readonly List<string> _handlerIds = [];
        private bool _disposed;

        public ForeignModuleTaskContextRegistration(
            ForeignModuleTaskContextRegistry registry,
            string contextId)
        {
            _registry = registry;
            ContextId = contextId;
        }

        public string ContextId { get; }

        public string RegisterEventHandler(ITaskEventHandler handler)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var handlerId = _registry.RegisterEventHandler(ContextId, handler);
            _handlerIds.Add(handlerId);
            return handlerId;
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _registry.Remove(ContextId, _handlerIds);
        }
    }
}
