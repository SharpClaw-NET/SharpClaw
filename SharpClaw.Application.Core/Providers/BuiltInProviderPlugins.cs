using SharpClaw.Application.Core.Clients;
using SharpClaw.Application.Core.LocalInference;
using SharpClaw.Contracts.Providers;
using SharpClaw.Providers.Common;

namespace SharpClaw.Application.Core.Providers;

/// <summary>
/// Builds the residual provider plugins that still ship inside Core after
/// Phase 8a. Provider modules now own: OpenAI-compatible family, Anthropic,
/// Google native, and Ollama. This assembler now only registers the
/// LlamaSharp local-inference plugin. Phase 8b carves it out and deletes
/// this file.
/// </summary>
public static class BuiltInProviderPlugins
{
    public static IEnumerable<IProviderPlugin> Build(LocalInferenceProcessManager localInferenceManager)
    {
        var genericCaps = new HeuristicCapabilityResolver(ProviderCapabilityHeuristics.ForGeneric);

        return
        [
            new SimpleProviderPlugin(WellKnownProviderKeys.LlamaSharp, "LlamaSharp (local)", false,
                _ => new LocalInferenceApiClient(localInferenceManager), genericCaps),
        ];
    }
}
