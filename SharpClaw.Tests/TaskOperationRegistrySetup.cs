using NUnit.Framework;

using SharpClaw.Core.Tasks.Parsing;
using SharpClaw.Core.Tasks.Registry;
using SharpClaw.Contracts.Tasks;
using SharpClaw.Modules.AgentOrchestration;
using SharpClaw.Modules.Metrics;

namespace SharpClaw.Tests;

/// <summary>
/// Global one-time test setup that seeds <see cref="TaskOperationRegistry.Default"/>
/// with the descriptor providers contributed by the default modules and
/// registers the default-module parser extensions with
/// <see cref="TaskScriptParser"/>. The production host performs both during
/// startup; tests bypass the host entirely, so we replicate that here.
/// </summary>
[SetUpFixture]
public sealed class TaskOperationRegistrySetup
{
    [OneTimeSetUp]
    public void SeedRegistry()
    {
        ITaskOperationDescriptorProvider[] providers =
        [
            new AgentOrchestrationOperationDescriptorProvider(),
        ];

        foreach (var provider in providers)
        {
            foreach (var descriptor in provider.Descriptors)
                TaskOperationRegistry.Default.Register(descriptor);
        }

        TaskScriptParser.RegisterModule(TaskScriptingParserExtension.Instance);
        TaskScriptParser.RegisterModule(MetricsParserExtension.Instance);
    }
}
