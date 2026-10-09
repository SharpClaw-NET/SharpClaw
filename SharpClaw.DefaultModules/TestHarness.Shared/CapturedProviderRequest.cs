using System.Text.Json;

using SharpClaw.Contracts.Kernel;
using SharpClaw.Contracts.Providers;

namespace SharpClaw.DefaultPackages.TestHarness;


public sealed record CapturedProviderRequest(
    int Sequence,
    string ProviderKey,
    string Surface,
    string Model,
    string? SystemPrompt,
    IReadOnlyList<CapturedProviderMessage> Messages,
    IReadOnlyList<CapturedProviderTool> Tools,
    IReadOnlyDictionary<string, string> ProviderParameters,
    CompletionParameters? CompletionParameters,
    bool ApiKeyWasProvided,
    string ApiKeyFingerprint,
    DateTimeOffset CapturedAt);
