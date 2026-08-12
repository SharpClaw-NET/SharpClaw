using System.Runtime.ExceptionServices;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.Modules;
using SharpClaw.Contracts.Providers;
using SharpClaw.Core.Kernel;
using SharpClaw.Runtime.INF.Persistence;
using SharpClaw.Shared.Instances;

namespace SharpClaw.Runtime.BLL.Kernel;

/// <summary>Composes one Runtime-owned adapter over the Core kernel.</summary>
public sealed class RuntimeKernelAdapter :
    IRuntimePersistenceActionBoundary,
    IRuntimeTransactionActionBoundary,
    IRuntimeModuleActionBoundary
{
    private readonly KernelModuleRegistry _moduleRegistry;
    private readonly KernelActionDispatcher _actionDispatcher;
    private bool _started;

    public RuntimeKernelAdapter(
        IConfiguration configuration,
        IServiceProvider hostServices,
        IConversationStore conversationStore,
        IEnumerable<ISharpClawModule> modules,
        SharpClawInstancePaths instancePaths,
        IRuntimeProviderClientFactory providerClientFactory,
        KernelGraphCompileOptions? graphCompileOptions = null,
        IKernelActionRepeatEvidenceAuthority? repeatEvidenceAuthority = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(hostServices);
        ArgumentNullException.ThrowIfNull(conversationStore);
        ArgumentNullException.ThrowIfNull(modules);
        ArgumentNullException.ThrowIfNull(instancePaths);
        ArgumentNullException.ThrowIfNull(providerClientFactory);

        _moduleRegistry = new KernelModuleRegistry();
        foreach (var module in modules.OrderBy(value => value.Identity.Id, StringComparer.Ordinal))
            _moduleRegistry.Add(module);

        Graph = _moduleRegistry.Compile(hostServices, graphCompileOptions);
        RuntimeProviderActionManifest.Validate(Graph);
        RuntimeToolActionManifest.Validate(Graph);
        RuntimePersistenceActionManifest.Validate(Graph);
        RuntimeTransactionActionManifest.Validate(Graph);
        RuntimeModuleActionManifest.Validate(Graph);
        _actionDispatcher = new KernelActionDispatcher(
            Graph,
            new KernelActionExecutionContext(
                RequestPrincipal.Anonymous,
                ExtensionFeatureSet.Empty,
                Guid.NewGuid(),
                Guid.NewGuid()),
            repeatEvidenceAuthority: repeatEvidenceAuthority);
        DispatchModuleCompositionActions();
        var graphPlugins = (Graph.GetService(typeof(IEnumerable<IProviderPlugin>)) as IEnumerable<IProviderPlugin>)
            ?.ToArray()
            ?? [];
        var hostPlugins = hostServices.GetServices<IProviderPlugin>().ToArray();
        var plugins = graphPlugins.Concat(hostPlugins).ToArray();
        ValidateConfiguredProviders(configuration, plugins);
        var providerClient = providerClientFactory.Create(configuration, plugins);
        var conversationResolver = ResolveConversationResolver(Graph, instancePaths);
        var profileResolver = ResolveProfileResolver(Graph, configuration);

        Kernel = DirectChatKernelFactory.CreateFromGraph(
            Graph,
            _actionDispatcher,
            new ProviderKernelTransport(providerClient),
            conversationResolver,
            profileResolver,
            conversationStore);
    }

    public KernelGraph Graph { get; }

    public DirectChatKernel Kernel { get; }

    public IActionDispatcher ActionDispatcher => _actionDispatcher;

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
                if (envelope.Payload is not RuntimeCliActionInvocation)
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

    public async ValueTask<TResult> RunModuleActionAsync<TResult>(
        SharpClawActionKey actionKey,
        object? payload,
        Func<object?, CancellationToken, ValueTask<TResult>> terminal,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(terminal);
        if (!RuntimeModuleActionManifest.Contains(actionKey))
        {
            throw new ArgumentException(
                $"Action '{actionKey.Value}' is not a published Runtime module action.",
                nameof(actionKey));
        }

        var executionContext = CreateHostExecutionContext();
        try
        {
            var terminalState = 0;
            var terminalResult = new TaskCompletionSource<TResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var result = await _actionDispatcher.RunRequiredWithContextAsync<KernelActionEnvelope, object>(
                executionContext,
                Graph.GetStandardAction(actionKey),
                new KernelActionEnvelope(actionKey, payload),
                async (envelope, actionCancellationToken) =>
                {
                    if (Interlocked.CompareExchange(ref terminalState, 1, 0) != 0)
                    {
                        var repeated = await terminalResult.Task;
                        return repeated is null
                            ? throw new KernelActionExecutionException(
                                $"Module action '{actionKey.Value}' returned a null repeated result.")
                            : repeated;
                    }

                    try
                    {
                        var value = await terminal(envelope.Payload, actionCancellationToken);
                        if (value is null)
                        {
                            throw new KernelActionExecutionException(
                                $"Module action '{actionKey.Value}' returned a null result.");
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
                    $"Module action '{actionKey.Value}' completed without running its terminal.");
            }

            if (result is not TResult typedResult)
            {
                throw new KernelActionExecutionException(
                    $"Module action '{actionKey.Value}' returned an invalid result type.");
            }

            return typedResult;
        }
        catch (KernelActionCancelledException exception)
        {
            await DispatchModuleLifecycleOutcomeAsync(
                new SharpClawActionKey("module.lifecycle.cancel"),
                payload,
                executionContext,
                exception);
            ExceptionDispatchInfo.Capture(exception).Throw();
            throw;
        }
        catch (OperationCanceledException exception)
        {
            await DispatchModuleLifecycleOutcomeAsync(
                new SharpClawActionKey("module.lifecycle.cancel"),
                payload,
                executionContext,
                exception);
            ExceptionDispatchInfo.Capture(exception).Throw();
            throw;
        }
        catch (Exception exception)
        {
            await DispatchModuleLifecycleOutcomeAsync(
                new SharpClawActionKey("module.lifecycle.fail"),
                payload,
                executionContext,
                exception);
            ExceptionDispatchInfo.Capture(exception).Throw();
            throw;
        }
    }

    private async ValueTask DispatchModuleLifecycleOutcomeAsync(
        SharpClawActionKey actionKey,
        object? payload,
        KernelActionExecutionContext executionContext,
        Exception originalException)
    {
        try
        {
            await _actionDispatcher.RunRequiredWithContextAsync<KernelActionEnvelope, object>(
                executionContext,
                Graph.GetStandardAction(actionKey),
                new KernelActionEnvelope(actionKey, payload),
                static (_, _) => ValueTask.FromResult<object>(true),
                Graph.ActionSnapshot,
                CancellationToken.None);
        }
        catch (Exception outcomeException)
        {
            throw new AggregateException(originalException, outcomeException);
        }
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
        var result = await _actionDispatcher.RunRequiredWithContextAsync<KernelActionEnvelope, object>(
            executionContext,
            descriptor,
            new KernelActionEnvelope(descriptor.Key, request),
            async (envelope, ct) =>
            {
                if (envelope.Payload is not TRequest effectiveRequest)
                {
                    throw new KernelActionExecutionException(
                        $"Runtime request action returned payload type '{envelope.Payload?.GetType().FullName ?? "<null>"}'.");
                }

                var terminalResult = await terminal(effectiveRequest, ct);
                return terminalResult!;
            },
            Graph.ActionSnapshot,
            cancellationToken);

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
                if (envelope.Payload is not RuntimePersistenceActionInvocation)
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
                if (envelope.Payload is not RuntimeTransactionActionInvocation)
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
                    if (envelope.Payload is not TRequest effectiveRequest)
                    {
                        throw new KernelActionExecutionException(
                            $"Runtime request action returned payload type '{envelope.Payload?.GetType().FullName ?? "<null>"}'.");
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
                if (envelope.Payload is not RuntimeSecurityActionInvocation effectiveInvocation)
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

        // A module can restrict the host decision, but it cannot grant authority
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
            ct => StartModulesThroughActionsAsync(
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
        var moduleStopInvoked = 0;
        var completionInvoked = false;
        ExceptionDispatchInfo? failure = null;

        async ValueTask PrepareHostAndModulesAsync(CancellationToken _)
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

            if (Interlocked.Exchange(ref moduleStopInvoked, 1) == 0)
            {
                try
                {
                    await StopModulesThroughActionsAsync(CancellationToken.None);
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
                    PrepareHostAndModulesAsync,
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
                        await PrepareHostAndModulesAsync(CancellationToken.None);
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

    private async ValueTask StartModulesThroughActionsAsync(
        string hostVersion,
        ExtensionFeatureSet features,
        CancellationToken cancellationToken)
    {
        foreach (var module in _moduleRegistry.Modules)
        {
            var context = new ModuleStartContext(
                module.Identity,
                hostVersion,
                Graph.ActionSnapshot.ContractHash,
                features);
            var started = await RunModuleActionAsync(
                new SharpClawActionKey("module.start"),
                context,
                async (payload, ct) =>
                {
                    if (payload is not ModuleStartContext effectiveContext)
                    {
                        throw new KernelActionExecutionException(
                            "The module.start action returned an invalid ModuleStartContext replacement.");
                    }

                    await module.StartAsync(effectiveContext, ct);
                    return true;
                },
                cancellationToken);
            if (!started)
            {
                throw new KernelActionExecutionException(
                    $"Module '{module.Identity.Id}' did not start successfully.");
            }
        }
    }

    private async ValueTask StopModulesThroughActionsAsync(CancellationToken cancellationToken)
    {
        for (var index = _moduleRegistry.Modules.Count - 1; index >= 0; index--)
        {
            var module = _moduleRegistry.Modules[index];
            var stopped = await RunModuleActionAsync(
                new SharpClawActionKey("module.stop"),
                module.Identity,
                async (_, ct) =>
                {
                    await module.StopAsync(ct);
                    return true;
                },
                cancellationToken);
            if (!stopped)
            {
                throw new KernelActionExecutionException(
                    $"Module '{module.Identity.Id}' did not stop successfully.");
            }
        }
    }

    private void DispatchModuleCompositionActions()
    {
        foreach (var module in _moduleRegistry.Modules)
        {
            var invocation = new RuntimeModuleActionInvocation(
                module.Identity.Id,
                "composition");
            foreach (var actionName in new[]
            {
                "module.discover",
                "module.validate",
                "module.configure",
            })
            {
                var allowed = RunModuleActionAsync(
                        new SharpClawActionKey(actionName),
                        invocation,
                        static (_, _) => new ValueTask<bool>(true))
                    .GetAwaiter()
                    .GetResult();
                if (!allowed)
                {
                    throw new KernelGraphCompilationException(
                        $"Module action '{actionName}' rejected module '{module.Identity.Id}'.");
                }
            }
        }

        var graphAllowed = RunModuleActionAsync(
                new SharpClawActionKey("module.graph.compile"),
                new RuntimeModuleActionInvocation("<graph>", "composition"),
                static (_, _) => new ValueTask<bool>(true))
            .GetAwaiter()
            .GetResult();
        if (!graphAllowed)
        {
            throw new KernelGraphCompilationException(
                "The module.graph.compile action rejected the compiled graph.");
        }
    }

    private static KernelActionExecutionContext CreateHostExecutionContext(
        RequestPrincipal? caller = null,
        ExtensionFeatureSet? features = null) =>
        new(
            caller ?? RequestPrincipal.Anonymous,
            features ?? ExtensionFeatureSet.Empty,
            Guid.NewGuid(),
            Guid.NewGuid());

    private static IConversationResolver ResolveConversationResolver(
        KernelGraph graph,
        SharpClawInstancePaths instancePaths) =>
        graph.Modules.ConversationResolver is { } resolverType
            ? (IConversationResolver)(graph.GetService(resolverType)
                ?? throw new KernelGraphCompilationException(
                    $"Conversation resolver '{resolverType.FullName}' is not registered."))
            : new SingleConversationResolver(ResolveDefaultConversationId(instancePaths));

    private static Guid ResolveDefaultConversationId(SharpClawInstancePaths instancePaths)
    {
        if (!Guid.TryParse(instancePaths.Manifest.InstanceId, out var conversationId)
            || conversationId == Guid.Empty)
        {
            throw new InvalidOperationException(
                $"The Runtime instance manifest '{instancePaths.ManifestPath}' has no valid instance identifier.");
        }

        return conversationId;
    }

    private static IChatProfileResolver ResolveProfileResolver(
        KernelGraph graph,
        IConfiguration configuration) =>
        graph.Modules.ProfileResolver is { } resolverType
            ? (IChatProfileResolver)(graph.GetService(resolverType)
                ?? throw new KernelGraphCompilationException(
                    $"Chat profile resolver '{resolverType.FullName}' is not registered."))
            : new FixedChatProfileResolver(CreateProfile(configuration));

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
                "Duplicate provider registrations were found: "
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
                $"Configured provider '{providerKey}' is not registered by an enabled in-process module.");
        }
    }

}
