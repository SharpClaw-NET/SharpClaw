using Microsoft.Extensions.Configuration;
using SharpClaw.Contracts.Kernel;
using SharpClaw.Contracts.Providers;
using SharpClaw.Core.Kernel;
using SharpClaw.Runtime.BLL.Kernel;
using SharpClaw.Shared.Instances;

namespace SharpClaw.Runtime.Host;

/// <summary>Provider-owned model discovery, without an inference request or host model allowlist.</summary>
internal static class RuntimeProviderModelCatalog
{
    private sealed record ModelDiscoveryInvocation(string Operation, string ProviderKey);

    public static async ValueTask<SharpClawProviderModels> ReadAsync(
        KernelActionExecutionContext context, IConfiguration configuration, RuntimeKernelAdapter adapter,
        IRuntimeProviderClientFactory factory, CancellationToken cancellationToken)
    {
        var setup = RuntimeProviderSetup.Describe(configuration, adapter);
        if (setup.SetupRequired || string.IsNullOrWhiteSpace(setup.ProviderKey))
            throw new InvalidOperationException(RuntimeProviderSetup.RequiredErrorMessage);
        var invocation = new ModelDiscoveryInvocation("models.list", setup.ProviderKey);
        var plugins = (adapter.Graph.GetService(typeof(IEnumerable<IProviderPlugin>)) as IEnumerable<IProviderPlugin>)?.ToArray() ?? [];
        IProviderApiClient? client = null;
        IReadOnlyList<string>? models = null;
        await RunStageAsync("provider.resolve", _ => ValueTask.CompletedTask).ConfigureAwait(false);
        await RunStageAsync("provider.client.create", _ =>
        {
            client = factory.Create(configuration, plugins, invocation.ProviderKey);
            return ValueTask.CompletedTask;
        }).ConfigureAwait(false);
        await RunStageAsync("provider.request.prepare", _ => ValueTask.CompletedTask).ConfigureAwait(false);
        await RunStageAsync("provider.request.send", async token =>
        {
            if (client is null) throw new InvalidOperationException("Provider model discovery was not prepared.");
            models = await client.ListModelIdsAsync(token).ConfigureAwait(false);
        }).ConfigureAwait(false);
        await RunStageAsync("provider.response.deserialize", _ =>
        {
            if (models is null || models.Count > 10_000 || models.Any(model =>
                string.IsNullOrWhiteSpace(model) || model.Length > 512 || model.Any(char.IsControl)))
                throw new InvalidDataException("Provider returned an invalid model catalog.");
            return ValueTask.CompletedTask;
        }).ConfigureAwait(false);
        if (models is null) throw new InvalidOperationException("Provider model discovery did not complete.");
        return new(invocation.ProviderKey, models.Distinct(StringComparer.Ordinal).ToArray());

        async ValueTask RunStageAsync(string key, Func<CancellationToken, ValueTask> terminal)
        {
            var actionKey = new SharpClawActionKey(key);
            var invoked = 0;
            var passed = await adapter.CoreActionDispatcher.RunRequiredWithContextAsync<KernelActionEnvelope, object>(
                context, adapter.Graph.GetStandardAction(actionKey), new KernelActionEnvelope(actionKey, invocation),
                async (effective, token) =>
                {
                    if (effective.Action.Payload is not ModelDiscoveryInvocation plan || plan != invocation ||
                        Interlocked.Exchange(ref invoked, 1) != 0)
                        throw new InvalidOperationException("Model discovery cannot replace provider authority or repeat a terminal.");
                    await terminal(token).ConfigureAwait(false);
                    return true;
                }, adapter.Graph.ActionSnapshot, cancellationToken).ConfigureAwait(false);
            if (passed is not true || invoked != 1)
                throw new InvalidOperationException("Provider model discovery was denied or suppressed.");
        }
    }
}
