using System.Collections.Concurrent;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.Kernel;
using SharpClaw.Core.Kernel;
using SharpClaw.Runtime.BLL.Kernel;
using SharpClaw.Shared.Instances;

namespace SharpClaw.Tests.Kernel;

internal static class RuntimeKernelAdapterTestFactory
{
    private static readonly ConcurrentBag<ServiceProvider> Providers = [];

    public static RuntimeKernelAdapter Create(
        IConfiguration configuration,
        IEnumerable<ISharpClawModule> registrations,
        SharpClawInstancePaths instancePaths,
        IRuntimeProviderClientFactory providerClientFactory,
        KernelGraphCompileOptions? graphCompileOptions = null,
        IKernelActionRepeatEvidenceAuthority? repeatEvidenceAuthority = null,
        IKernelEventDeliverySink? eventDeliverySink = null,
        Action<IServiceCollection>? configureServices = null)
    {
        var discovered = registrations.ToArray();
        var jobs = new KernelJobsBindings();
        var services = TestServiceGraph.Collect(discovered);
        configureServices?.Invoke(services);
        var provider = services.BuildServiceProvider();
        Providers.Add(provider);

        return new RuntimeKernelAdapter(
            configuration,
            provider,
            jobs,
            discovered.OfType<IServiceLifecycle>(),
            instancePaths,
            providerClientFactory,
            graphCompileOptions,
            repeatEvidenceAuthority,
            eventDeliverySink);
    }

    public static void DisposeProviders()
    {
        while (Providers.TryTake(out var provider))
            provider.Dispose();
    }
}

[SetUpFixture]
internal sealed class RuntimeKernelAdapterTestServices
{
    [OneTimeTearDown]
    public void DisposeProviders() => RuntimeKernelAdapterTestFactory.DisposeProviders();
}
