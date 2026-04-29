using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;

using SharpClaw.Contracts.Modules;
using SharpClaw.Modules.Providers.Whisper.Clients;
using SharpClaw.Modules.Providers.Whisper.LocalInference;
using SharpClaw.Modules.Transcription.Clients;

namespace SharpClaw.Modules.Providers.Whisper;

/// <summary>
/// Default module: provides the local Whisper STT backend
/// (<see cref="LocalTranscriptionClient"/> + <see cref="WhisperModelManager"/>)
/// as an <see cref="ITranscriptionApiClient"/> implementation.
///
/// The Transcription module discovers this implementation via
/// <c>IEnumerable&lt;ITranscriptionApiClient&gt;</c>, so when this module is
/// disabled the local backend simply disappears from the factory's catalog
/// and Transcription falls back to cloud STT (OpenAI / Groq).
/// </summary>
public sealed class WhisperProvidersModule : ISharpClawModule
{
    public string Id => "sharpclaw_providers_whisper";
    public string DisplayName => "Whisper (local STT)";
    public string ToolPrefix => "pw";

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<WhisperModelManager>();
        services.AddSingleton<ITranscriptionApiClient, LocalTranscriptionClient>();
    }

    public IReadOnlyList<ModuleContractExport> ExportedContracts =>
    [
        new("transcription_stt_local",
            typeof(ITranscriptionApiClient),
            "Local Whisper.net speech-to-text backend"),
    ];

    public IReadOnlyList<ModuleToolDefinition> GetToolDefinitions() => [];

    public Task<string> ExecuteToolAsync(
        string toolName, JsonElement parameters, AgentJobContext job,
        IServiceProvider sp, CancellationToken ct)
        => throw new InvalidOperationException(
            $"Module '{Id}' does not register any tools.");
}
