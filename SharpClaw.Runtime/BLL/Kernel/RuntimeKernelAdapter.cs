using System.Runtime.ExceptionServices;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.Kernel;
using SharpClaw.Contracts.Providers;
using SharpClaw.Core.Kernel;
using SharpClaw.Runtime.INF.Persistence;
using SharpClaw.Shared.Instances;

namespace SharpClaw.Runtime.BLL.Kernel;

/// <summary>Composes one Runtime-owned adapter over the Core kernel.</summary>
public sealed class RuntimeKernelAdapter :
    IRuntimePersistenceActionBoundary,
    IRuntimeTransactionActionBoundary,
    IRuntimeEventActionBoundary,
    IRuntimeEventPublisher
{
    private readonly IReadOnlyList<IServiceLifecycle> _lifecycleServices;
    private readonly List<IServiceLifecycle> _startedServices = [];
    private readonly KernelActionDispatcher _actionDispatcher;
    private readonly KernelEventDispatcher _eventDispatcher;
    private readonly IKernelEventDeliverySink _eventDeliverySink;
    private readonly KernelJobsActionRunner _jobsActionRunner;
    private bool _started;
    private static readonly JsonSerializerOptions EventActionJsonOptions =
        new(JsonSerializerDefaults.General)
        {
            PropertyNameCaseInsensitive = true,
        };

    public RuntimeKernelAdapter(
        IConfiguration configuration,
        IServiceProvider hostServices,
        KernelJobsBindings jobsBindings,
        IEnumerable<IServiceLifecycle> lifecycleServices,
        SharpClawInstancePaths instancePaths,
        IRuntimeProviderClientFactory providerClientFactory,
        KernelGraphCompileOptions? graphCompileOptions = null,
        IKernelActionRepeatEvidenceAuthority? repeatEvidenceAuthority = null,
        IKernelEventDeliverySink? eventDeliverySink = null,
        KernelExternalAuthoritySessionRegistry? externalAuthorityRegistry = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(hostServices);
        ArgumentNullException.ThrowIfNull(jobsBindings);
        ArgumentNullException.ThrowIfNull(lifecycleServices);
        ArgumentNullException.ThrowIfNull(instancePaths);
        ArgumentNullException.ThrowIfNull(providerClientFactory);

        _lifecycleServices = lifecycleServices.ToArray();
        var graphBuilder = new KernelGraphBuilder();
        jobsBindings.AddTo(graphBuilder);
        RuntimeEventBindings.AddTo(graphBuilder);
        Graph = graphBuilder.Compile(
            hostServices,
            AddRuntimeEventGrant(
                MergeExternalBehaviorAuthority(
                    graphCompileOptions,
                    hostServices.GetServices<IExternalBehaviorAuthority>()),
                jobsBindings));
        RuntimeProviderActionManifest.Validate(Graph);
        RuntimeToolActionManifest.Validate(Graph);
        RuntimePersistenceActionManifest.Validate(Graph);
        RuntimeTransactionActionManifest.Validate(Graph);
        RuntimeEventActionManifest.Validate(Graph);
        _eventDeliverySink = eventDeliverySink ?? new InMemoryEventDeliverySink(supportsDurable: true);
        _eventDispatcher = new KernelEventDispatcher(Graph, _eventDeliverySink);
        _actionDispatcher = new KernelActionDispatcher(
            Graph,
            new KernelActionExecutionContext(
                RequestPrincipal.Anonymous,
                ExtensionFeatureSet.Empty,
                Guid.NewGuid(),
                Guid.NewGuid()),
            eventWriter: _eventDispatcher,
            resultSnapshotter: new RuntimeEventActionResultSnapshotter(),
            repeatEvidenceAuthority: repeatEvidenceAuthority,
            externalAuthorityRegistry: externalAuthorityRegistry);
        _jobsActionRunner = new KernelJobsActionRunner(Graph, _actionDispatcher);
        var plugins = (Graph.GetService(typeof(IEnumerable<IProviderPlugin>)) as IEnumerable<IProviderPlugin>)
            ?.ToArray()
            ?? [];
        ValidateConfiguredProviders(configuration, plugins);
        var providerClient = providerClientFactory.Create(configuration, plugins);
        var conversationResolver = ResolveConversationResolver(Graph);
        var effectiveConversationStore = ResolveConversationStore(Graph);
        var profileResolver = ResolveProfileResolver(Graph, configuration);

        Kernel = DirectChatKernelFactory.CreateFromGraph(
            Graph,
            _actionDispatcher,
            new ProviderKernelTransport(providerClient),
            conversationResolver,
            profileResolver,
            effectiveConversationStore);
    }

    public KernelGraph Graph { get; }

    public DirectChatKernel Kernel { get; }

    public IActionDispatcher ActionDispatcher => _actionDispatcher;

    internal KernelActionDispatcher CoreActionDispatcher => _actionDispatcher;

    internal KernelJobsActionRunner JobsActionRunner => _jobsActionRunner;

    public async ValueTask<RuntimeEventPublishResult> PublishAsync(
        RuntimeEventPayload payload,
        EventDelivery delivery = EventDelivery.Inline,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
        payload.Validate();
        var eventId = Guid.NewGuid();
        var invocation = new RuntimeEventActionInvocation(
            RuntimeEventDefinitions.CommittedKey,
            eventId,
            delivery,
            "define",
            payload);

        var defined = await RunEventActionAsync(
            new SharpClawActionKey("event.define"),
            invocation,
            static (effective, _) =>
            {
                ValidateEventInvocation(effective, "define");
                return ValueTask.FromResult(effective);
            },
            cancellationToken);

        var preview = await RunEventActionAsync(
            new SharpClawActionKey("event.publish.preview"),
            defined with { Phase = "preview" },
            static (effective, _) =>
            {
                if (effective.Payload is not RuntimeEventPayload effectivePayload)
                {
                    throw new KernelActionExecutionException(
                        "The event preview action returned an invalid payload.");
                }

                return ValueTask.FromResult(effectivePayload.Validate());
            },
            cancellationToken);

        var committedInvocation = defined with
        {
            Phase = "commit",
            Payload = preview,
        };
        var committed = await RunEventActionAsync(
            new SharpClawActionKey("event.publish.commit"),
            committedInvocation,
            async (effective, ct) =>
            {
                return await RunEventActionAsync(
                    new SharpClawActionKey("event.deliver"),
                    effective with { Phase = "deliver" },
                    async (deliveryInvocation, deliveryCt) =>
                    {
                        if (deliveryInvocation.Payload is not RuntimeEventPayload eventPayload)
                        {
                            throw new KernelActionExecutionException(
                                "The event delivery action returned an invalid payload.");
                        }

                        await _eventDispatcher.PublishAsync(
                            RuntimeEventDefinitions.Committed,
                            eventPayload.Validate(),
                            deliveryCt);
                        if (deliveryInvocation.Delivery != EventDelivery.Inline)
                        {
                            await _eventDeliverySink.EnqueueAsync(
                                RuntimeEventDefinitions.CommittedKey,
                                new EventEnvelope<RuntimeEventPayload>(
                                    deliveryInvocation.EventId,
                                    null,
                                    Guid.NewGuid(),
                                    DateTimeOffset.UtcNow,
                                    RuntimeEventDefinitions.SourceId,
                                    eventPayload),
                                deliveryInvocation.Delivery,
                                deliveryCt,
                                "runtime-event-outbox");
                        }

                        return deliveryInvocation;
                    },
                    ct);
            },
            cancellationToken);

        ValidateEventInvocation(committed, "commit");
        if (committed.Payload is not RuntimeEventPayload committedPayload)
        {
            throw new KernelActionExecutionException(
                "The event commit action returned an invalid payload.");
        }

        return new RuntimeEventPublishResult(
            committed.EventId,
            committedPayload.Validate(),
            committed.Delivery);
    }

    private static void ValidateEventInvocation(
        RuntimeEventActionInvocation invocation,
        string phase)
    {
        if (invocation.EventId == Guid.Empty ||
            invocation.EventKey != RuntimeEventDefinitions.CommittedKey ||
            invocation.Payload is not RuntimeEventPayload payload)
        {
            throw new KernelActionExecutionException(
                $"The event {phase} action returned an invalid invocation.");
        }

        payload.Validate();
    }

    public async ValueTask<TResult> RunEventActionAsync<TResult>(
        SharpClawActionKey actionKey,
        RuntimeEventActionInvocation invocation,
        Func<RuntimeEventActionInvocation, CancellationToken, ValueTask<TResult>> terminal,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        ArgumentNullException.ThrowIfNull(terminal);
        if (!RuntimeEventActionManifest.Contains(actionKey))
        {
            throw new ArgumentException(
                $"Action '{actionKey.Value}' is not a published Runtime event action.",
                nameof(actionKey));
        }

        var terminalState = 0;
        var terminalResult = new TaskCompletionSource<TResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var result = await _actionDispatcher.RunRequiredWithContextAsync<KernelActionEnvelope, object>(
                CreateHostExecutionContext(),
                Graph.GetStandardAction(actionKey),
                new KernelActionEnvelope(actionKey, invocation),
                async (envelope, ct) =>
                {
                    var effective = NormalizeEventInvocation(envelope.Action.Payload, actionKey);

                    if (Interlocked.CompareExchange(ref terminalState, 1, 0) != 0)
                    {
                        var repeated = await terminalResult.Task.WaitAsync(ct);
                        return (object?)repeated ?? throw new KernelActionExecutionException(
                            $"Event action '{actionKey.Value}' returned a null repeated result.");
                    }

                    try
                    {
                        var value = await terminal(effective, ct);
                        terminalResult.TrySetResult(value);
                        return (object?)value ?? throw new KernelActionExecutionException(
                            $"Event action '{actionKey.Value}' returned a null result.");
                    }
                    catch (Exception exception)
                    {
                        terminalResult.TrySetException(exception);
                        throw;
                    }
                },
                Graph.ActionSnapshot,
                cancellationToken);

            if (Volatile.Read(ref terminalState) == 0)
            {
                throw new KernelActionExecutionException(
                    $"Event action '{actionKey.Value}' completed without running its terminal.");
            }

            return NormalizeEventActionResult<TResult>(result, actionKey);
        }
        catch (KernelActionCancelledException exception)
        {
            await DispatchEventFailureAsync(actionKey, invocation, exception, isCancellation: true);
            ExceptionDispatchInfo.Capture(exception).Throw();
            throw;
        }
        catch (OperationCanceledException exception)
        {
            await DispatchEventFailureAsync(actionKey, invocation, exception, isCancellation: true);
            ExceptionDispatchInfo.Capture(exception).Throw();
            throw;
        }
        catch (Exception exception)
        {
            await DispatchEventFailureAsync(actionKey, invocation, exception, isCancellation: false);
            ExceptionDispatchInfo.Capture(exception).Throw();
            throw;
        }
    }

    private static RuntimeEventActionInvocation NormalizeEventInvocation(
        object? value,
        SharpClawActionKey actionKey)
    {
        var invocation = value switch
        {
            RuntimeEventActionInvocation typed => typed,
            JsonElement json => json.Deserialize<RuntimeEventActionInvocation>(EventActionJsonOptions),
            _ => null,
        };
        if (invocation is null)
        {
            throw new KernelActionExecutionException(
                $"Event action '{actionKey.Value}' returned an invalid invocation payload.");
        }

        return invocation with { Payload = NormalizeEventPayload(invocation.Payload) };
    }

    private static object? NormalizeEventPayload(object? value)
    {
        if (value is not JsonElement json)
            return value;

        if (json.ValueKind == JsonValueKind.Object &&
            json.TryGetProperty("Name", out _) &&
            json.TryGetProperty("SourceId", out _) &&
            json.TryGetProperty("Summary", out _))
        {
            return json.Deserialize<RuntimeEventPayload>(EventActionJsonOptions)
                ?? throw new KernelActionExecutionException(
                    "The event action payload could not be deserialized.");
        }

        if (json.ValueKind == JsonValueKind.Object &&
            json.TryGetProperty("RecordKey", out _) &&
            json.TryGetProperty("IsCancellation", out _))
        {
            return json.Deserialize<RuntimeEventOutboxTransition>(EventActionJsonOptions)
                ?? throw new KernelActionExecutionException(
                    "The event outbox transition could not be deserialized.");
        }

        return json.Clone();
    }

    private static TResult NormalizeEventActionResult<TResult>(
        object? value,
        SharpClawActionKey actionKey)
    {
        if (value is TResult typed)
            return typed;

        if (value is JsonElement json)
        {
            object? converted;
            if (typeof(TResult) == typeof(RuntimeEventActionInvocation))
            {
                converted = NormalizeEventInvocation(json, actionKey);
            }
            else if (typeof(TResult) == typeof(RuntimeEventPayload))
            {
                converted = json.Deserialize<RuntimeEventPayload>(EventActionJsonOptions);
            }
            else
            {
                converted = json.Deserialize<TResult>(EventActionJsonOptions);
            }

            if (converted is TResult result)
                return result;
        }

        throw new KernelActionExecutionException(
            $"Event action '{actionKey.Value}' returned an invalid result type.");
    }

    private async ValueTask DispatchEventFailureAsync(
        SharpClawActionKey failedAction,
        RuntimeEventActionInvocation invocation,
        Exception exception,
        bool isCancellation)
    {
        if (failedAction == new SharpClawActionKey("event.delivery.fail"))
            return;

        try
        {
            await _actionDispatcher.RunRequiredWithContextAsync<KernelActionEnvelope, object>(
                CreateHostExecutionContext(),
                Graph.GetStandardAction(new SharpClawActionKey("event.delivery.fail")),
                new KernelActionEnvelope(
                    new SharpClawActionKey("event.delivery.fail"),
                    invocation with
                    {
                        Phase = "failure",
                        Payload = new RuntimeEventFailure(
                            failedAction.Value,
                            exception is OperationCanceledException
                                ? "EVENT_CANCELLED"
                                : "EVENT_DELIVERY_FAILED",
                            isCancellation),
                    }),
                static (_, _) => ValueTask.FromResult<object>(true),
                Graph.ActionSnapshot,
                CancellationToken.None);
        }
        catch (Exception outcomeException)
        {
            throw new AggregateException(exception, outcomeException);
        }
    }

    internal KernelActionExecutionContext CreateCliExecutionContext(
        RequestPrincipal? caller = null,
        ExtensionFeatureSet? features = null) =>
        CreateHostExecutionContext(caller, features);

    internal async ValueTask<TResult> RunCliActionAsync<TResult>(
        KernelActionExecutionContext executionContext,
        SharpClawActionKey actionKey,
        RuntimeCliActionInvocation invocation,
        Func<CancellationToken, ValueTask<TResult>> terminal,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(executionContext);
        ArgumentNullException.ThrowIfNull(invocation);
        ArgumentNullException.ThrowIfNull(terminal);
        if (!RuntimeCliActionCatalog.Contains(actionKey))
        {
            throw new ArgumentException(
                $"Action '{actionKey.Value}' is not a published Runtime CLI action.",
                nameof(actionKey));
        }

        var descriptor = Graph.GetStandardAction(actionKey);
        var result = await _actionDispatcher.RunRequiredWithContextAsync<KernelActionEnvelope, object>(
            executionContext,
            descriptor,
            new KernelActionEnvelope(actionKey, invocation),
            async (envelope, ct) =>
            {
                if (envelope.Action.Payload is not RuntimeCliActionInvocation)
                {
                    throw new KernelActionExecutionException(
                        $"CLI action '{actionKey.Value}' returned an invalid invocation payload.");
                }

                return (object?)await terminal(ct) ??
                    throw new KernelActionExecutionException(
                        $"CLI action '{actionKey.Value}' returned a null result.");
            },
            Graph.ActionSnapshot,
            cancellationToken);

        if (result is not TResult typedResult)
        {
            throw new KernelActionExecutionException(
                $"CLI action '{actionKey.Value}' returned an invalid result type.");
        }

        return typedResult;
    }

    public ValueTask RunRuntimeLifecycleActionAsync(
        SharpClawActionKey actionKey,
        object? payload,
        Func<CancellationToken, ValueTask> terminal,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(terminal);
        if (!RuntimeLifecycleActionCatalog.Contains(actionKey))
        {
            throw new ArgumentException(
                $"Action '{actionKey.Value}' is not a Runtime lifecycle action.",
                nameof(actionKey));
        }

        return RunRuntimeLifecycleActionCoreAsync(
            actionKey,
            payload,
            CreateHostExecutionContext(),
            terminal,
            cancellationToken);
    }

    internal async ValueTask<TResult> RunRequestAsync<TRequest, TResult>(
        KernelActionExecutionContext executionContext,
        TRequest request,
        Func<TRequest, CancellationToken, ValueTask<TResult>> terminal,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(executionContext);
        ArgumentNullException.ThrowIfNull(terminal);
        var descriptor = Graph.GetStandardAction(
            new SharpClawActionKey("runtime.request.receive"));
        var terminalState = 0;
        var terminalResult = new TaskCompletionSource<TResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var result = await _actionDispatcher.RunRequiredWithContextAsync<KernelActionEnvelope, object>(
            executionContext,
            descriptor,
            new KernelActionEnvelope(descriptor.Key, request),
            async (envelope, ct) =>
            {
                if (envelope.Action.Payload is not TRequest effectiveRequest)
                {
                    throw new KernelActionExecutionException(
                        $"Runtime request action returned payload type '{envelope.Action.Payload?.GetType().FullName ?? "<null>"}'.");
                }

                if (Interlocked.CompareExchange(ref terminalState, 1, 0) != 0)
                    return (object?)await terminalResult.Task
                        ?? throw new KernelActionExecutionException(
                            "Runtime request terminal returned a null repeated result.");

                try
                {
                    var value = await terminal(effectiveRequest, ct);
                    if (value is null)
                    {
                        throw new KernelActionExecutionException(
                            "Runtime request terminal returned a null result.");
                    }
                    terminalResult.TrySetResult(value);
                    return value;
                }
                catch (Exception exception)
                {
                    terminalResult.TrySetException(exception);
                    throw;
                }
            },
            Graph.ActionSnapshot,
            cancellationToken);

        if (Volatile.Read(ref terminalState) == 0)
        {
            throw new KernelActionExecutionException(
                "Runtime request action completed without running its terminal.");
        }

        if (result is not TResult typedResult)
        {
            throw new KernelActionExecutionException(
                $"Runtime request action returned result type '{result?.GetType().FullName ?? "<null>"}'.");
        }

        return typedResult;
    }

    async ValueTask IRuntimePersistenceActionBoundary.RunPersistenceActionAsync(
        RuntimePersistenceActionInvocation invocation,
        Func<CancellationToken, ValueTask<int>> terminal,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        ArgumentNullException.ThrowIfNull(terminal);
        if (!RuntimePersistenceActionManifest.Contains(invocation.ActionKey))
        {
            throw new ArgumentException(
                $"Action '{invocation.ActionKey.Value}' is not a published Runtime persistence action.",
                nameof(invocation));
        }

        var terminalCompleted = false;
        await _actionDispatcher.RunRequiredWithContextAsync<KernelActionEnvelope, object>(
            CreateHostExecutionContext(),
            Graph.GetStandardAction(invocation.ActionKey),
            new KernelActionEnvelope(invocation.ActionKey, invocation),
            async (envelope, actionCancellationToken) =>
            {
                if (envelope.Action.Payload is not RuntimePersistenceActionInvocation)
                {
                    throw new KernelActionExecutionException(
                        $"Persistence action '{invocation.ActionKey.Value}' returned an invalid invocation payload.");
                }

                var result = await terminal(actionCancellationToken);
                terminalCompleted = true;
                return result;
            },
            Graph.ActionSnapshot,
            cancellationToken);

        if (!terminalCompleted)
        {
            throw new KernelActionExecutionException(
                $"Persistence action '{invocation.ActionKey.Value}' completed without running its save terminal.");
        }
    }

    async ValueTask<RuntimeTransactionActionResult>
        IRuntimeTransactionActionBoundary.RunTransactionActionAsync(
            RuntimeTransactionActionInvocation invocation,
            Func<CancellationToken, ValueTask<RuntimeTransactionActionResult>> terminal,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        ArgumentNullException.ThrowIfNull(terminal);
        if (!RuntimeTransactionActionManifest.Contains(invocation.ActionKey))
        {
            throw new ArgumentException(
                $"Action '{invocation.ActionKey.Value}' is not a published Runtime transaction action.",
                nameof(invocation));
        }

        var terminalCompleted = false;
        var result = await _actionDispatcher.RunRequiredWithContextAsync<KernelActionEnvelope, object>(
            CreateHostExecutionContext(),
            Graph.GetStandardAction(invocation.ActionKey),
            new KernelActionEnvelope(invocation.ActionKey, invocation),
            async (envelope, actionCancellationToken) =>
            {
                if (envelope.Action.Payload is not RuntimeTransactionActionInvocation)
                {
                    throw new KernelActionExecutionException(
                        $"Transaction action '{invocation.ActionKey.Value}' returned an invalid invocation payload.");
                }

                var terminalResult = await terminal(actionCancellationToken);
                terminalCompleted = true;
                return terminalResult;
            },
            Graph.ActionSnapshot,
            cancellationToken);

        if (!terminalCompleted)
        {
            throw new KernelActionExecutionException(
                $"Transaction action '{invocation.ActionKey.Value}' completed without running its terminal.");
        }

        if (result is not RuntimeTransactionActionResult transactionResult)
        {
            throw new KernelActionExecutionException(
                $"Transaction action '{invocation.ActionKey.Value}' returned an invalid result type.");
        }

        return transactionResult;
    }

    internal async IAsyncEnumerable<TResult> RunRequestStreamAsync<TRequest, TResult>(
        KernelActionExecutionContext executionContext,
        TRequest request,
        Func<TRequest, CancellationToken, IAsyncEnumerable<TResult>> terminal,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(executionContext);
        ArgumentNullException.ThrowIfNull(terminal);

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        var channel = Channel.CreateBounded<TResult>(new BoundedChannelOptions(32)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
        var dispatchTask = DispatchRequestStreamAsync(
            executionContext,
            request,
            terminal,
            channel.Writer,
            linkedCancellation.Token);

        try
        {
            await foreach (var item in channel.Reader.ReadAllAsync(cancellationToken))
                yield return item;

            await dispatchTask;
        }
        finally
        {
            linkedCancellation.Cancel();
            try
            {
                await dispatchTask;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
        }
    }

    private async Task DispatchRequestStreamAsync<TRequest, TResult>(
        KernelActionExecutionContext executionContext,
        TRequest request,
        Func<TRequest, CancellationToken, IAsyncEnumerable<TResult>> terminal,
        ChannelWriter<TResult> writer,
        CancellationToken cancellationToken)
    {
        Exception? failure = null;
        var terminalCompleted = false;
        try
        {
            await _actionDispatcher.RunRequiredWithContextAsync<KernelActionEnvelope, object>(
                executionContext,
                Graph.GetStandardAction(new SharpClawActionKey("runtime.request.receive")),
                new KernelActionEnvelope(
                    new SharpClawActionKey("runtime.request.receive"),
                    request),
                async (envelope, ct) =>
                {
                    if (envelope.Action.Payload is not TRequest effectiveRequest)
                    {
                        throw new KernelActionExecutionException(
                            $"Runtime request action returned payload type '{envelope.Action.Payload?.GetType().FullName ?? "<null>"}'.");
                    }

                    await foreach (var item in terminal(effectiveRequest, ct).WithCancellation(ct))
                        await writer.WriteAsync(item, ct);

                    terminalCompleted = true;
                    return true;
                },
                Graph.ActionSnapshot,
                cancellationToken);

            if (!terminalCompleted)
            {
                throw new KernelActionExecutionException(
                    "Runtime request stream action completed without running its terminal.");
            }
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            writer.TryComplete(failure);
        }
    }

    /// <summary>
    /// Runs one repeat-safe security decision through the singleton dispatcher.
    /// The invocation carries operation metadata only and never carries secrets.
    /// Protected work must run after this method returns.
    /// </summary>
    public async ValueTask<bool> RunSecurityDecisionAsync(
        KernelActionExecutionContext executionContext,
        SharpClawActionKey actionKey,
        RuntimeSecurityActionInvocation invocation,
        Func<RuntimeSecurityActionInvocation, CancellationToken, ValueTask<bool>> baseDecision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(executionContext);
        ArgumentNullException.ThrowIfNull(invocation);
        ArgumentNullException.ThrowIfNull(baseDecision);
        if (!RuntimeSecurityActionManifest.Contains(actionKey))
        {
            throw new ArgumentException(
                $"Action '{actionKey.Value}' is not a published Runtime security action.",
                nameof(actionKey));
        }

        var descriptor = Graph.GetStandardAction(actionKey);
        var baseAllowed = false;
        var result = await _actionDispatcher.RunRequiredWithContextAsync<KernelActionEnvelope, object>(
            executionContext,
            descriptor,
            new KernelActionEnvelope(actionKey, invocation),
            async (envelope, ct) =>
            {
                if (envelope.Action.Payload is not RuntimeSecurityActionInvocation effectiveInvocation)
                {
                    throw new KernelActionExecutionException(
                        $"Security action '{actionKey.Value}' returned an invalid invocation payload.");
                }

                baseAllowed = await baseDecision(invocation, ct);
                return baseAllowed;
            },
            Graph.ActionSnapshot,
            cancellationToken);

        if (result is not bool actionAllowed)
        {
            throw new KernelActionExecutionException(
                $"Security action '{actionKey.Value}' returned an invalid result type.");
        }

        // An interceptor can restrict the host decision, but it cannot grant authority
        // when the host decision did not allow the request.
        return baseAllowed && actionAllowed;
    }

    public async ValueTask StartAsync(
        string hostVersion,
        RequestPrincipal? caller = null,
        ExtensionFeatureSet? features = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostVersion);
        if (_started)
            throw new InvalidOperationException("The Runtime kernel has already started.");

        var effectiveCaller = caller ?? RequestPrincipal.Anonymous;
        var effectiveFeatures = features ?? ExtensionFeatureSet.Empty;
        var executionContext = CreateHostExecutionContext(effectiveCaller, effectiveFeatures);
        await RunRuntimeLifecycleActionCoreAsync(
            RuntimeLifecycleActionCatalog.StartConfigure,
            hostVersion,
            executionContext,
            ct => StartParticipantsAsync(
                hostVersion,
                effectiveFeatures,
                ct),
            cancellationToken);
        _started = true;
    }

    public async ValueTask StopAsync(
        CancellationToken cancellationToken = default,
        Func<CancellationToken, ValueTask>? onPrepare = null,
        Func<CancellationToken, ValueTask>? onComplete = null)
    {
        if (!_started)
            return;

        var executionContext = CreateHostExecutionContext();
        var prepare = onPrepare ?? (static _ => ValueTask.CompletedTask);
        var completion = onComplete ?? (static _ => ValueTask.CompletedTask);
        var prepareInvoked = false;
        var participantStopInvoked = 0;
        var completionInvoked = false;
        ExceptionDispatchInfo? failure = null;

        async ValueTask PrepareHostAndParticipantsAsync(CancellationToken _)
        {
            prepareInvoked = true;
            ExceptionDispatchInfo? prepareFailure = null;
            try
            {
                await prepare(CancellationToken.None);
            }
            catch (Exception exception)
            {
                prepareFailure = ExceptionDispatchInfo.Capture(exception);
            }

            if (Interlocked.Exchange(ref participantStopInvoked, 1) == 0)
            {
                try
                {
                    await StopParticipantsAsync(CancellationToken.None);
                }
                catch (Exception exception)
                {
                    prepareFailure ??= ExceptionDispatchInfo.Capture(exception);
                }
            }

            prepareFailure?.Throw();
        }

        async ValueTask CompleteHostAsync(CancellationToken _)
        {
            completionInvoked = true;
            await completion(CancellationToken.None);
        }

        try
        {
            try
            {
                await RunRuntimeLifecycleActionCoreAsync(
                    RuntimeLifecycleActionCatalog.StopPrepare,
                    null,
                    executionContext,
                    PrepareHostAndParticipantsAsync,
                    cancellationToken);
            }
            catch (Exception exception)
            {
                failure = ExceptionDispatchInfo.Capture(exception);
            }
            finally
            {
                if (!prepareInvoked)
                {
                    try
                    {
                        await PrepareHostAndParticipantsAsync(CancellationToken.None);
                    }
                    catch (Exception exception)
                    {
                        failure ??= ExceptionDispatchInfo.Capture(exception);
                    }
                }

                _started = false;
                try
                {
                    await RunRuntimeLifecycleActionCoreAsync(
                        RuntimeLifecycleActionCatalog.StopComplete,
                        null,
                        executionContext,
                        CompleteHostAsync,
                        CancellationToken.None);
                }
                catch (Exception exception)
                {
                    failure ??= ExceptionDispatchInfo.Capture(exception);
                }
            }
        }
        finally
        {
            if (!completionInvoked)
            {
                try
                {
                    await completion(CancellationToken.None);
                }
                catch (Exception exception)
                {
                    failure ??= ExceptionDispatchInfo.Capture(exception);
                }
            }
        }

        failure?.Throw();
    }

    private async ValueTask RunRuntimeLifecycleActionCoreAsync(
        SharpClawActionKey actionKey,
        object? payload,
        KernelActionExecutionContext executionContext,
        Func<CancellationToken, ValueTask> terminal,
        CancellationToken cancellationToken)
    {
        var descriptor = Graph.GetStandardAction(actionKey);
        await _actionDispatcher.RunRequiredWithContextAsync<KernelActionEnvelope, object>(
            executionContext,
            descriptor,
            new KernelActionEnvelope(actionKey, payload),
            async (_, ct) =>
            {
                await terminal(ct);
                return true;
            },
            Graph.ActionSnapshot,
            cancellationToken);
    }

    private async ValueTask StartParticipantsAsync(
        string hostVersion,
        ExtensionFeatureSet features,
        CancellationToken cancellationToken)
    {
        try
        {
            foreach (var service in _lifecycleServices)
            {
                var context = new ServiceStartContext(
                    hostVersion,
                    Graph.ActionSnapshot.ContractHash,
                    features);
                await service.StartAsync(context, cancellationToken);
                _startedServices.Add(service);
            }
        }
        catch (Exception startException)
        {
            try
            {
                await StopParticipantsAsync(CancellationToken.None);
            }
            catch (Exception stopException)
            {
                throw new AggregateException(startException, stopException);
            }

            ExceptionDispatchInfo.Capture(startException).Throw();
            throw;
        }
    }

    private async ValueTask StopParticipantsAsync(CancellationToken cancellationToken)
    {
        ExceptionDispatchInfo? failure = null;
        for (var index = _startedServices.Count - 1; index >= 0; index--)
        {
            try
            {
                await _startedServices[index].StopAsync(cancellationToken);
            }
            catch (Exception exception)
            {
                failure ??= ExceptionDispatchInfo.Capture(exception);
            }
        }
        _startedServices.Clear();
        failure?.Throw();
    }

    private static KernelActionExecutionContext CreateHostExecutionContext(
        RequestPrincipal? caller = null,
        ExtensionFeatureSet? features = null) =>
        new(
            caller ?? RequestPrincipal.Anonymous,
            features ?? ExtensionFeatureSet.Empty,
            Guid.NewGuid(),
            Guid.NewGuid());

    private static KernelGraphCompileOptions AddRuntimeEventGrant(
        KernelGraphCompileOptions? options,
        KernelJobsBindings jobsBindings)
    {
        var actionRegistrationGrants = options?.ActionRegistrationCapabilityGrants is { } existingActionGrants
            ? existingActionGrants.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyDictionary<string, ActionInterceptionCapabilities>)
                    new Dictionary<string, ActionInterceptionCapabilities>(
                        pair.Value,
                        StringComparer.Ordinal),
                StringComparer.Ordinal)
            : new Dictionary<
                string,
                IReadOnlyDictionary<string, ActionInterceptionCapabilities>>(
                StringComparer.Ordinal);
        actionRegistrationGrants[KernelJobsBindings.SourceId] = jobsBindings.Grants;

        var eventRegistrationGrants = options?.EventRegistrationCapabilityGrants is { } existing
            ? existing.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyDictionary<string, EventInterceptionCapabilities>)
                    new Dictionary<string, EventInterceptionCapabilities>(
                        pair.Value,
                        StringComparer.Ordinal),
                StringComparer.Ordinal)
            : new Dictionary<
                string,
                IReadOnlyDictionary<string, EventInterceptionCapabilities>>(
                StringComparer.Ordinal);
        var runtimeEventGrant = eventRegistrationGrants.TryGetValue(
            RuntimeEventDefinitions.SourceId,
            out var configuredGrant)
            ? new Dictionary<string, EventInterceptionCapabilities>(
                configuredGrant,
                StringComparer.Ordinal)
            : new Dictionary<string, EventInterceptionCapabilities>(
                StringComparer.Ordinal);
        runtimeEventGrant[RuntimeEventDefinitions.CommittedKey.Value] =
            RuntimeEventDefinitions.Committed.Capabilities;
        eventRegistrationGrants[RuntimeEventDefinitions.SourceId] = runtimeEventGrant;

        return new KernelGraphCompileOptions
        {
            SupportedActionCapabilities = options?.SupportedActionCapabilities
                ?? new KernelGraphCompileOptions().SupportedActionCapabilities,
            SupportedEventCapabilities = options?.SupportedEventCapabilities
                ?? new KernelGraphCompileOptions().SupportedEventCapabilities,
            ActionCapabilityGrants = options?.ActionCapabilityGrants,
            ActionRegistrationCapabilityGrants = actionRegistrationGrants,
            EventCapabilityGrants = options?.EventCapabilityGrants,
            EventRegistrationCapabilityGrants = eventRegistrationGrants,
            SensitiveActionApprovals = (options?.SensitiveActionApprovals ?? [])
                .Concat(jobsBindings.Approvals)
                .ToArray(),
            ExternalSensitiveActionApprovals = options?.ExternalSensitiveActionApprovals ?? [],
            SensitiveEventApprovals = options?.SensitiveEventApprovals ?? [],
            ExternalSensitiveEventApprovals = options?.ExternalSensitiveEventApprovals ?? [],
            MaximumActionDepth = options?.MaximumActionDepth
                ?? new KernelGraphCompileOptions().MaximumActionDepth,
        };
    }

    private static KernelGraphCompileOptions MergeExternalBehaviorAuthority(
        KernelGraphCompileOptions? options,
        IEnumerable<IExternalBehaviorAuthority> authorities)
    {
        var defaults = new KernelGraphCompileOptions();
        var actionRegistrationGrants = CopyActionRegistrationGrants(options?.ActionRegistrationCapabilityGrants);
        var eventRegistrationGrants = CopyEventRegistrationGrants(options?.EventRegistrationCapabilityGrants);
        var actionApprovals = new HashSet<KernelExternalSensitiveActionApproval>(
            options?.ExternalSensitiveActionApprovals ?? []);
        var eventApprovals = new HashSet<KernelExternalSensitiveEventApproval>(
            options?.ExternalSensitiveEventApprovals ?? []);

        foreach (var external in authorities)
        {
            ValidateExternalAuthority(external);
            var contributionActionGrants = actionRegistrationGrants.TryGetValue(external.SourceId, out var existingActions)
                ? new Dictionary<string, ActionInterceptionCapabilities>(existingActions, StringComparer.Ordinal)
                : new Dictionary<string, ActionInterceptionCapabilities>(StringComparer.Ordinal);
            foreach (var grantGroup in external.Authorization.ActionGrants
                         .GroupBy(grant => grant.ActionKey.Value, StringComparer.Ordinal))
            {
                var grants = grantGroup.Distinct().ToArray();
                if (grants.Length != 1)
                {
                    throw new KernelGraphCompilationException(
                        $"External source '{external.SourceId}' has conflicting grants for action '{grantGroup.Key}'.");
                }

                var grant = grants[0];
                MergeActionGrant(external.SourceId, contributionActionGrants, grant);
                if (grant.SensitiveApproved)
                {
                    AddExternalActionApprovals(
                        external.SourceId,
                        grant,
                        external.Discovery.Actions,
                        external.Discovery.ActionDefinitions,
                        actionApprovals);
                }
            }
            actionRegistrationGrants[external.SourceId] = contributionActionGrants;

            var contributionEventGrants = eventRegistrationGrants.TryGetValue(external.SourceId, out var existingEvents)
                ? new Dictionary<string, EventInterceptionCapabilities>(existingEvents, StringComparer.Ordinal)
                : new Dictionary<string, EventInterceptionCapabilities>(StringComparer.Ordinal);
            foreach (var grantGroup in external.Authorization.EventGrants
                         .GroupBy(grant => grant.EventKey.Value, StringComparer.Ordinal))
            {
                var grants = grantGroup.Distinct().ToArray();
                if (grants.Length != 1)
                {
                    throw new KernelGraphCompilationException(
                        $"External source '{external.SourceId}' has conflicting grants for event '{grantGroup.Key}'.");
                }

                var grant = grants[0];
                MergeEventGrant(external.SourceId, contributionEventGrants, grant);
                if (grant.SensitiveApproved)
                {
                    AddExternalEventApprovals(
                        external.SourceId,
                        grant,
                        external.Discovery.Events,
                        eventApprovals);
                }
            }
            eventRegistrationGrants[external.SourceId] = contributionEventGrants;
        }

        return new KernelGraphCompileOptions
        {
            SupportedActionCapabilities = options?.SupportedActionCapabilities
                ?? defaults.SupportedActionCapabilities,
            SupportedEventCapabilities = options?.SupportedEventCapabilities
                ?? defaults.SupportedEventCapabilities,
            ActionCapabilityGrants = options?.ActionCapabilityGrants,
            ActionRegistrationCapabilityGrants = actionRegistrationGrants,
            EventCapabilityGrants = options?.EventCapabilityGrants,
            EventRegistrationCapabilityGrants = eventRegistrationGrants,
            SensitiveActionApprovals = options?.SensitiveActionApprovals ?? [],
            ExternalSensitiveActionApprovals = actionApprovals.ToArray(),
            SensitiveEventApprovals = options?.SensitiveEventApprovals ?? [],
            ExternalSensitiveEventApprovals = eventApprovals.ToArray(),
            MaximumActionDepth = options?.MaximumActionDepth ?? defaults.MaximumActionDepth,
        };
    }

    private static Dictionary<string, IReadOnlyDictionary<string, ActionInterceptionCapabilities>>
        CopyActionRegistrationGrants(
            IReadOnlyDictionary<string, IReadOnlyDictionary<string, ActionInterceptionCapabilities>>? source) =>
        source?.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyDictionary<string, ActionInterceptionCapabilities>)
                new Dictionary<string, ActionInterceptionCapabilities>(pair.Value, StringComparer.Ordinal),
            StringComparer.Ordinal)
        ?? new Dictionary<string, IReadOnlyDictionary<string, ActionInterceptionCapabilities>>(
            StringComparer.Ordinal);

    private static Dictionary<string, IReadOnlyDictionary<string, EventInterceptionCapabilities>>
        CopyEventRegistrationGrants(
            IReadOnlyDictionary<string, IReadOnlyDictionary<string, EventInterceptionCapabilities>>? source) =>
        source?.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyDictionary<string, EventInterceptionCapabilities>)
                new Dictionary<string, EventInterceptionCapabilities>(pair.Value, StringComparer.Ordinal),
            StringComparer.Ordinal)
        ?? new Dictionary<string, IReadOnlyDictionary<string, EventInterceptionCapabilities>>(
            StringComparer.Ordinal);

    private static void ValidateExternalAuthority(IExternalBehaviorAuthority external)
    {
        if (!string.Equals(external.SourceId, external.Authorization.SourceId, StringComparison.Ordinal)
            || !string.Equals(external.SourceId, external.Discovery.SourceId, StringComparison.Ordinal))
        {
            throw new KernelGraphCompilationException(
                $"External source '{external.SourceId}' has inconsistent discovery or authorization identity.");
        }
    }

    private static void MergeActionGrant(
        string SourceId,
        IDictionary<string, ActionInterceptionCapabilities> target,
        ActionCapabilityGrant grant)
    {
        if (target.TryGetValue(grant.ActionKey.Value, out var existing)
            && existing != grant.Capabilities)
        {
            throw new KernelGraphCompilationException(
                $"External contribution '{SourceId}' conflicts with the configured grant for action '{grant.ActionKey.Value}'.");
        }
        target[grant.ActionKey.Value] = grant.Capabilities;
    }

    private static void MergeEventGrant(
        string SourceId,
        IDictionary<string, EventInterceptionCapabilities> target,
        EventCapabilityGrant grant)
    {
        if (target.TryGetValue(grant.EventKey.Value, out var existing)
            && existing != grant.Capabilities)
        {
            throw new KernelGraphCompilationException(
                $"External contribution '{SourceId}' conflicts with the configured grant for event '{grant.EventKey.Value}'.");
        }
        target[grant.EventKey.Value] = grant.Capabilities;
    }

    private static void AddExternalActionApprovals(
        string SourceId,
        ActionCapabilityGrant grant,
        IReadOnlyList<SidecarActionSubscription> subscriptions,
        IReadOnlyList<SidecarActionDefinition> definitions,
        ISet<KernelExternalSensitiveActionApproval> approvals)
    {
        var matches = subscriptions
            .Where(subscription => subscription.VersionRange.Contains(grant.ActionVersion))
            .Where(subscription => subscription.TargetKind != SidecarHookTargetKind.Exact
                || subscription.ActionKey == grant.ActionKey)
            .ToArray();
        if (matches.Length > 0)
        {
            foreach (var subscription in matches)
            {
                approvals.Add(new KernelExternalSensitiveActionApproval(
                    SourceId,
                    grant.ActionKey,
                    grant.ActionVersion,
                    subscription.InputSchema,
                    subscription.ResultSchema));
            }
            return;
        }

        var definitionMatches = definitions.Where(definition =>
            definition.ActionKey == grant.ActionKey
            && definition.Version == grant.ActionVersion).ToArray();
        if (definitionMatches.Length != 1)
        {
            throw new KernelGraphCompilationException(
                $"External contribution '{SourceId}' has no discovered schema for sensitive action '{grant.ActionKey.Value}'.");
        }

        approvals.Add(new KernelExternalSensitiveActionApproval(
            SourceId,
            grant.ActionKey,
            grant.ActionVersion,
            definitionMatches[0].InputSchema,
            definitionMatches[0].ResultSchema));
    }

    private static void AddExternalEventApprovals(
        string SourceId,
        EventCapabilityGrant grant,
        IReadOnlyList<SidecarEventSubscription> subscriptions,
        ISet<KernelExternalSensitiveEventApproval> approvals)
    {
        var matches = subscriptions
            .Where(subscription => subscription.VersionRange.Contains(grant.EventVersion))
            .Where(subscription => subscription.TargetKind != SidecarHookTargetKind.Exact
                || subscription.EventKey == grant.EventKey)
            .ToArray();
        if (matches.Length == 0)
        {
            throw new KernelGraphCompilationException(
                $"External contribution '{SourceId}' has no discovered schema for sensitive event '{grant.EventKey.Value}'.");
        }

        foreach (var subscription in matches)
        {
            approvals.Add(new KernelExternalSensitiveEventApproval(
                SourceId,
                grant.EventKey,
                grant.EventVersion,
                subscription.PayloadSchema));
        }
    }

    private static IConversationResolver ResolveConversationResolver(KernelGraph graph) =>
        graph.Services.ConversationResolver ?? new StatelessConversationResolver();

    private static IConversationStore ResolveConversationStore(KernelGraph graph)
    {
        if (graph.Services.ConversationResolver is null)
            return new StatelessConversationStore();

        return graph.Services.ConversationStore
            ?? throw new KernelGraphCompilationException(
                "A configured conversation resolver requires one IConversationStore service.");
    }

    private static IChatProfileResolver ResolveProfileResolver(
        KernelGraph graph,
        IConfiguration configuration) =>
        graph.Services.ProfileResolver ?? new FixedChatProfileResolver(CreateProfile(configuration));

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
                "Duplicate provider keys were found: "
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
                $"Configured provider '{providerKey}' is not available in the active service graph.");
        }
    }

}
