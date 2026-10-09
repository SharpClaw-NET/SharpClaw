using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Core.Kernel;

namespace SharpClaw.Tests;


[SetUpFixture]
internal sealed class TestServiceGraphCleanup
{
    [OneTimeTearDown]
    public void DisposeProviders() => TestServiceGraph.DisposeProviders();
}
