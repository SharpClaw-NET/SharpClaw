using NUnit.Framework;

using SharpClaw.Application.Infrastructure.Tasks.Parsing;
using SharpClaw.Application.Infrastructure.Tasks.Registry;
using SharpClaw.Contracts.Tasks;
using SharpClaw.Modules.AgentOrchestration;
using SharpClaw.Modules.ComputerUse;
using SharpClaw.Modules.DatabaseAccess;
using SharpClaw.Modules.FilesystemTriggers;
using SharpClaw.Modules.Http;
using SharpClaw.Modules.Metrics;
using SharpClaw.Modules.NetworkTriggers;

namespace SharpClaw.Tests;

/// <summary>
/// Global one-time test setup that seeds <see cref="TaskStepRegistry.Default"/>
/// with the descriptor providers contributed by the default modules and
/// registers the default-module parser extensions with
/// <see cref="TaskScriptParser"/>. The production host performs both during
/// startup; tests bypass the host entirely, so we replicate that here.
/// </summary>
[SetUpFixture]
public sealed class TaskStepRegistrySetup
{
    [OneTimeSetUp]
    public void SeedRegistry()
    {
        ITaskStepDescriptorProvider[] providers =
        [
            new TaskScriptingStepDescriptorProvider(),
            new AgentOrchestrationStepDescriptorProvider(),
            new HttpStepDescriptorProvider(),
        ];

        foreach (var provider in providers)
        {
            foreach (var descriptor in provider.Descriptors)
                TaskStepRegistry.Default.Register(descriptor);
        }

        TaskScriptParser.RegisterModule(TaskScriptingParserExtension.Instance);
        TaskScriptParser.RegisterModule(ComputerUseParserExtension.Instance);
        TaskScriptParser.RegisterModule(FilesystemTriggersParserExtension.Instance);
        TaskScriptParser.RegisterModule(HttpParserExtension.Instance);
        TaskScriptParser.RegisterModule(MetricsParserExtension.Instance);
        TaskScriptParser.RegisterModule(NetworkTriggersParserExtension.Instance);
        TaskScriptParser.RegisterModule(DatabaseAccessParserExtension.Instance);
    }
}
