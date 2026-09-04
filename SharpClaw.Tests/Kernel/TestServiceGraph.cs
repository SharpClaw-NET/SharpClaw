using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Core.Kernel;

namespace SharpClaw.Tests;

internal static class TestServiceGraph
{
    private static readonly ConcurrentBag<ServiceProvider> Providers = [];

    public static ServiceCollection Collect(IEnumerable<ISharpClawModule> registrations)
    {
        var services = new ServiceCollection();
        foreach (var registration in registrations)
        {
            var builder = new SharpClawModuleBuilder(registration.Identity);
            registration.ConfigureServices(builder);
            foreach (var descriptor in builder)
                ((ICollection<ServiceDescriptor>)services).Add(descriptor);
        }
        return services;
    }

    public static ServiceProvider Build(IEnumerable<ISharpClawModule> registrations)
    {
        var provider = Collect(registrations).BuildServiceProvider();
        Providers.Add(provider);
        return provider;
    }

    public static KernelGraph Compile(
        IEnumerable<ISharpClawModule> registrations,
        KernelGraphCompileOptions? options = null)
    {
        var provider = Build(registrations);
        return new KernelGraphBuilder().Compile(provider, options);
    }

    public static void DisposeProviders()
    {
        while (Providers.TryTake(out var provider))
            provider.Dispose();
    }
}

[SetUpFixture]
internal sealed class TestServiceGraphCleanup
{
    [OneTimeTearDown]
    public void DisposeProviders() => TestServiceGraph.DisposeProviders();
}
