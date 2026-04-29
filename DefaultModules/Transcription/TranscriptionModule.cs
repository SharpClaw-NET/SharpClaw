using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using SharpClaw.Contracts.Enums;
using SharpClaw.Contracts.Modules;
using SharpClaw.Contracts.Persistence;
using SharpClaw.Contracts.Tasks;
using SharpClaw.Modules.Transcription.Clients;
using SharpClaw.Modules.Transcription.Contracts;
using SharpClaw.Modules.Transcription.Handlers;
using SharpClaw.Modules.Transcription.Models;
using SharpClaw.Modules.Transcription.Services;

namespace SharpClaw.Modules.Transcription;

/// <summary>
/// Default module: live audio transcription and STT provider integration.
/// Consumes the <c>system_audio_capture</c> contract from the SystemAudio
/// module for input device CRUD and WASAPI capture. Windows only.
/// </summary>
public sealed class TranscriptionModule : ISharpClawModule, ITaskParserAware
{
    public string Id => "sharpclaw_transcription";
    public string DisplayName => "Transcription";
    public string ToolPrefix => "tr";

    // ═══════════════════════════════════════════════════════════════
    // DI Registration
    // ═══════════════════════════════════════════════════════════════

    public ITaskParserModuleExtension ParserExtension => TranscriptionParserExtension.Instance;

    public void ConfigureServices(IServiceCollection services)
    {
        // Cloud transcription API clients. The local Whisper backend lives in the
        // sharpclaw_providers_whisper module; when that module is enabled it
        // contributes another ITranscriptionApiClient via its own ConfigureServices,
        // and the factory picks it up through IEnumerable<ITranscriptionApiClient>.
        services.AddSingleton<ITranscriptionApiClient, OpenAiTranscriptionApiClient>();
        services.AddSingleton<ITranscriptionApiClient, GroqTranscriptionApiClient>();
        services.AddSingleton<TranscriptionApiClientFactory>();

        // Orchestrator + service
        services.AddSingleton<LiveTranscriptionOrchestrator>();
        services.AddSingleton<ILiveTranscriptionOrchestrator>(sp =>
            sp.GetRequiredService<LiveTranscriptionOrchestrator>());
        services.AddSingleton<ITranscriptionSegmentPublisher>(sp =>
            sp.GetRequiredService<LiveTranscriptionOrchestrator>());
        services.AddScoped<TranscriptionTaskBridge>();
        services.AddSingleton<TranscriptionJobSink>();
        services.AddScoped<TranscriptionService>();
        services.AddScoped(sp => sp.GetRequiredService<IModuleDbContextFactory>()
            .CreateDbContext<TranscriptionDbContext>());
        services.AddScoped<ITaskStepExecutorExtension, TranscriptionTaskStepExecutor>();
    }

    // ═══════════════════════════════════════════════════════════════
    // Contracts
    // ═══════════════════════════════════════════════════════════════

    public IReadOnlyList<ModuleContractExport> ExportedContracts =>
    [
        new("transcription_stt",
            typeof(ITranscriptionApiClient),
            "Speech-to-text transcription via provider APIs"),
    ];

    // ═══════════════════════════════════════════════════════════════
    // Resource Type Descriptors
    // ═══════════════════════════════════════════════════════════════

    public IReadOnlyList<ModuleResourceTypeDescriptor> GetResourceTypeDescriptors() => [];

    // ═══════════════════════════════════════════════════════════════
    // Tool Definitions
    // ═══════════════════════════════════════════════════════════════

    public IReadOnlyList<ModuleToolDefinition> GetToolDefinitions()
    {
        var permission = new ModuleToolPermission(
            IsPerResource: true, Check: null, DelegateTo: "AccessInputAudioAsync");

        return
        [
            new("transcribe_audio_device",
                "Start live transcription from an input audio device. Returns transcription segments.",
                TranscribeSchema(), permission,
                Aliases: ["transcribe_from_audio_device"]),
            new("transcribe_audio_stream",
                "Start live transcription from an audio stream. Returns transcription segments.",
                TranscribeSchema(), permission),
            new("transcribe_audio_file",
                "Transcribe an audio file. Returns transcription segments.",
                TranscribeSchema(), permission),
        ];
    }

    // ═══════════════════════════════════════════════════════════════
    // CLI Commands
    // ═══════════════════════════════════════════════════════════════

    public IReadOnlyList<ModuleCliCommand> GetCliCommands() => [];

    // ═══════════════════════════════════════════════════════════════
    // Endpoint Mapping
    // ═══════════════════════════════════════════════════════════════

    public void MapEndpoints(object app)
    {
        var endpoints = (Microsoft.AspNetCore.Routing.IEndpointRouteBuilder)app;
        endpoints.MapTranscriptionStreaming();
        endpoints.MapTranscriptionJobEndpoints();
    }

    // ═══════════════════════════════════════════════════════════════
    // Tool Execution
    // ═══════════════════════════════════════════════════════════════

    public async Task<string> ExecuteToolAsync(
        string toolName, JsonElement parameters, AgentJobContext job,
        IServiceProvider sp, CancellationToken ct)
    {
        var orchestrator = sp.GetRequiredService<ILiveTranscriptionOrchestrator>();

        if (toolName is "transcribe_audio_device" or "transcribe_from_audio_device"
                     or "transcribe_audio_stream" or "transcribe_audio_file")
        {
            var modelInfoProvider = sp.GetRequiredService<IModelInfoProvider>();
            var db = sp.GetRequiredService<TranscriptionDbContext>();

            var deviceId = job.ResourceId
                ?? throw new InvalidOperationException("transcribe_audio_device requires a ResourceId (audio device).");

            // Resolve model: explicit modelId parameter, else agent model.
            Guid modelId;
            if (parameters.TryGetProperty("modelId", out var modelIdProp)
                && modelIdProp.TryGetGuid(out var explicitModelId))
            {
                modelId = explicitModelId;
            }
            else
            {
                throw new InvalidOperationException(
                    $"Agent {job.AgentId} has no model configured; pass modelId explicitly.");
            }

            var modelInfo = await modelInfoProvider.GetModelProviderInfoAsync(modelId, ct)
                ?? throw new InvalidOperationException($"Model {modelId} not found.");

            if (!orchestrator.SupportsProvider(modelInfo.ProviderKey))
                throw new InvalidOperationException(
                    $"Provider ({modelInfo.ProviderKey}) does not support transcription.");

            // Read transcription parameters from the tool call.
            var language = parameters.TryGetProperty("language", out var langProp) ? langProp.GetString() : null;

            TranscriptionMode? mode = null;
            if (parameters.TryGetProperty("mode", out var modeProp)
                && modeProp.GetString() is { } modeStr
                && Enum.TryParse<TranscriptionMode>(modeStr, ignoreCase: true, out var parsedMode))
                mode = parsedMode;

            int? windowSeconds = null;
            if (parameters.TryGetProperty("windowSeconds", out var wsProp)
                && wsProp.TryGetInt32(out var ws))
                windowSeconds = ws;

            int? stepSeconds = null;
            if (parameters.TryGetProperty("stepSeconds", out var ssProp)
                && ssProp.TryGetInt32(out var ss))
                stepSeconds = ss;

            // Persist module-owned job params.
            var txJob = new TranscriptionJobDB
            {
                AgentJobId = job.JobId,
                ModelId = modelId,
                DeviceId = deviceId,
                Language = language,
                Mode = mode,
                WindowSeconds = windowSeconds,
                StepSeconds = stepSeconds,
            };
            db.TranscriptionJobs.Add(txJob);
            await db.SaveChangesAsync(ct);

            orchestrator.Start(
                job.JobId, modelId, deviceId,
                language, mode, windowSeconds, stepSeconds);

            // Transcription jobs remain in Executing status until stopped.
            return "Transcription started.";
        }

        throw new InvalidOperationException($"Unknown Transcription tool: {toolName}");
    }

    // ═══════════════════════════════════════════════════════════════
    // Lifecycle
    // ═══════════════════════════════════════════════════════════════

    public async Task ShutdownAsync()
    {
        if (_orchestrator is not null)
            await _orchestrator.StopAllAsync();
    }

    private ILiveTranscriptionOrchestrator? _orchestrator;

    public async Task InitializeAsync(IServiceProvider services, CancellationToken ct)
    {
        _orchestrator = services.GetService<ILiveTranscriptionOrchestrator>();

        // Reconcile transcription jobs left in Executing/Queued from a previous session.
        // No orchestrator loops survive a restart, so these are guaranteed stale.
        var sink = services.GetService<TranscriptionJobSink>();
        if (sink is not null)
            await sink.CancelStaleTranscriptionJobsAsync(ct);
    }

    // ═══════════════════════════════════════════════════════════════
    // JSON Schemas
    // ═══════════════════════════════════════════════════════════════

    private static JsonElement TranscribeSchema()
    {
        var json = """
        {
          "type": "object",
          "properties": {
            "resource_id": {
              "type": "string",
              "description": "ID of the input audio device to use for transcription."
            }
          },
          "required": ["resource_id"]
        }
        """;
        return JsonDocument.Parse(json).RootElement.Clone();
    }
}
