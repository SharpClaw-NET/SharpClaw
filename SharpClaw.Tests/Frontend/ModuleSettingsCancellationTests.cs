using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using SharpClaw.Contracts.Kernel;
using SharpClaw.Core.Kernel;
using SharpClaw.Services;
using SharpClaw.Shared.Instances;

namespace SharpClaw.Tests.Frontend;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812",
    Justification = "NUnit discovers and constructs this fixture through reflection.")]
[TestFixture]
[NonParallelizable]
internal sealed class ModuleSettingsCancellationTests
{
    [TestCase("catalog"), TestCase("read"), TestCase("save")]
    public async Task CallerCancellationReachesSettingsTransportWithoutPublishingSuccessAsync(string operationKind)
    {
        var sink = new ActionSink();
        var actions = ClientActionDispatcher.CreateProduction(new ClientActionContextSource(), sink);
        using var handler = new WaitingSettingsHandler();
        using var http = new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri("https://fixture.invalid/") };
        var api = new SharpClawApiClient(http, NullLogger<SharpClawApiClient>.Instance, actions, "local-runtime-key");
        await using var apiDisposal = api.ConfigureAwait(false);
        using var caller = new CancellationTokenSource();
        // The nested finally below directly joins this request before any owner
        // is disposed, including when cancellation callbacks report a fault.
#pragma warning disable CA2025
        var operation = StartSettingsOperationAsync(api, operationKind, caller.Token);
#pragma warning restore CA2025
        try
        {
            await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.CurrentContext.CancellationToken)
                .ConfigureAwait(false);
            operation.IsCompleted.Should().BeFalse();
            await caller.CancelAsync().ConfigureAwait(false);
            await handler.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.CurrentContext.CancellationToken)
                .ConfigureAwait(false);
            // This test owns the HTTP operation; its fake transport and action sink have no UI/JTF dependency.
#pragma warning disable VSTHRD003
            Func<Task> observe = async () => await operation.ConfigureAwait(false);
#pragma warning restore VSTHRD003
            await observe.Should().ThrowAsync<OperationCanceledException>().ConfigureAwait(false);
            handler.Calls.Should().Be(1);
            sink.Actions.Should().Equal("client.command.receive", "client.command.validate",
                "client.command.dispatch", "client.command.cancel");
        }
        finally
        {
            try { await caller.CancelAsync().ConfigureAwait(false); }
            finally
            {
                // Cancellation-callback failure cannot bypass the physical request join.
                try { await operation.ConfigureAwait(false); }
                catch (OperationCanceledException) when (caller.IsCancellationRequested) { }
            }
        }
    }

    private static Task StartSettingsOperationAsync(SharpClawApiClient api, string kind, CancellationToken token)
    {
        var page = new SharpClawModuleSettingsPage("test.fixture", "Fixture", "settings", "Settings",
            "/fixture/settings", "/fixture/settings");
        return kind switch
        {
            "catalog" => ModuleSettingsClient.ReadPagesAsync(api, token),
            "read" => ModuleSettingsClient.ReadDocumentAsync(api, page, token),
            "save" => ModuleSettingsClient.SaveAsync(api, page,
                new Dictionary<string, object?>(StringComparer.Ordinal) { ["message"] = "value" }, token),
            _ => throw new AssertionException("Unknown settings operation."),
        };
    }

    private sealed class ActionSink : ClientActionServiceSet.IClientActionContextSink
    {
        public ConcurrentQueue<string> Actions { get; } = new();
        public void Observe(ActionContext<KernelActionEnvelope> context) => Actions.Enqueue(context.ActionKey.Value);
    }

    private sealed class WaitingSettingsHandler : HttpMessageHandler
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            Entered.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                CancellationObserved.TrySetResult();
                throw;
            }
            throw new AssertionException("The controlled settings transport completed without cancellation.");
        }
    }
}
