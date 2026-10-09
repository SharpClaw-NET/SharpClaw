using System.Text.Json;

using SharpClaw.Contracts.Kernel;
using SharpClaw.Contracts.Providers;

namespace SharpClaw.DefaultPackages.TestHarness;


internal sealed class TestHarnessCapabilityResolver : IModelCapabilityResolver
{
    public HashSet<string> Resolve(string modelName) => new(StringComparer.OrdinalIgnoreCase)
    {
        "chat"
    };
}
