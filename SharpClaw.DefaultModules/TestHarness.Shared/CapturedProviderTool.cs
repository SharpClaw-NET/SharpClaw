using System.Text.Json;

using SharpClaw.Contracts.Kernel;
using SharpClaw.Contracts.Providers;

namespace SharpClaw.DefaultPackages.TestHarness;


public sealed record CapturedProviderTool(
    string Name,
    string Description,
    string ParametersSchemaJson);
