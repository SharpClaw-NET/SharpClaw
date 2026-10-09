using System.Text.Json;

using SharpClaw.Contracts.Kernel;
using SharpClaw.Contracts.Providers;

namespace SharpClaw.DefaultPackages.TestHarness;


public sealed record CapturedProviderMessage(
    string Role,
    string? Content,
    string? ProviderMetadataJson,
    bool HasImage);
