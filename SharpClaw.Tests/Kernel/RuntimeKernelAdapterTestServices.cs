using System.Collections.Concurrent;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.Kernel;
using SharpClaw.Core.Kernel;
using SharpClaw.Runtime.BLL.Kernel;
using SharpClaw.Shared.Instances;

namespace SharpClaw.Tests.Kernel;


[SetUpFixture]
internal sealed class RuntimeKernelAdapterTestServices
{
    [OneTimeTearDown]
    public void DisposeProviders() => RuntimeKernelAdapterTestFactory.DisposeProviders();
}
