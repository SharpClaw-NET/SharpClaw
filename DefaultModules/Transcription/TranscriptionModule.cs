using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using SharpClaw.Contracts.Enums;
using SharpClaw.Contracts.Modules;
using SharpClaw.Contracts.Persistence;
using SharpClaw.Contracts.Tasks;
using SharpClaw.Modules.Transcription.Clients;
using SharpClaw.Modules.Transcription.Contracts;
using SharpClaw.Modules.Transcription.DTOs;
using SharpClaw.Modules.Transcription.Handlers;
using SharpClaw.Modules.Transcription.LocalInference;
using SharpClaw.Modules.Transcription.Models;
using SharpClaw.Modules.Transcription.Services;

namespace SharpClaw.Modules.Transcription;

/// <summary>
/// Default module: live audio transcription, input audio device management,
/// and STT provider integration. Windows only (WASAPI audio capture).
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
        // Transcription API clients
        services.AddSingleton<ITranscriptionApiClient, OpenAiTranscriptionApiClient>();
        services.AddSingleton<ITranscriptionApiClient, GroqTranscriptionApiClient>();
        services.AddSingleton<WhisperModelManager>();
        services.AddSingleton<ITranscriptionApiClient, LocalTranscriptionClient>();
        services.AddSingleton<TranscriptionApiClientFactory>();

        // Audio capture (WASAPI, Windows only)
        services.AddSingleton<IAudioCaptureProvider, WasapiAudioCaptureProvider>();
        services.AddSingleton<SharedAudioCaptureManager>();

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

        // Shared services this module may also need
    }

    // ═══════════════════════════════════════════════════════════════
    // Contracts
    // ═══════════════════════════════════════════════════════════════

    public IReadOnlyList<ModuleContractExport> ExportedContracts =>
    [
        new("transcription_stt",
            typeof(ITranscriptionApiClient),
            "Speech-to-text transcription via provider APIs"),
        new("transcription_audio_capture",
            typeof(IAudioCaptureProvider),
            "Audio capture from input devices"),
    ];

    // ═══════════════════════════════════════════════════════════════
    // Resource Type Descriptors
    // ═══════════════════════════════════════════════════════════════

    public IReadOnlyList<ModuleResourceTypeDescriptor> GetResourceTypeDescriptors() =>
    [
        new("TrAudio", "InputAudio", "AccessInputAudioAsync", static async (sp, ct) =>
        {
            var db = sp.GetRequiredService<TranscriptionDbContext>();
            return await db.InputAudios.Select(a => a.Id).ToListAsync(ct);
        },
        LoadLookupItems: static async (sp, ct) =>
        {
            var db = sp.GetRequiredService<TranscriptionDbContext>();
            return await db.InputAudios.Select(a => new ValueTuple<Guid, string>(a.Id, a.Name)).ToListAsync(ct);
        }, DefaultResourceKey: "inputaudio"),
    ];

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

    public IReadOnlyList<ModuleCliCommand> GetCliCommands() =>
    [
        new(
            Name: "inputaudio",
            Aliases: ["ia"],
            Scope: ModuleCliScope.ResourceType,
            Description: "Input audio device management",
            UsageLines:
            [
                "resource inputaudio add <name> [identifier] [description]",
                "resource inputaudio get <id>                   Show an input audio",
                "resource inputaudio list                       List all input audios",
                "resource inputaudio update <id> [name] [id]    Update an input audio",
                "resource inputaudio delete <id>                Delete an input audio",
                "resource inputaudio sync                       Import system input audios",
            ],
            Handler: HandleResourceInputAudioCommandAsync),
    ];

    private static async Task HandleResourceInputAudioCommandAsync(
        string[] args, IServiceProvider sp, CancellationToken ct)
    {
        var ids = sp.GetRequiredService<ICliIdResolver>();
        var svc = sp.GetRequiredService<TranscriptionService>();

        if (args.Length < 3)
        {
            PrintInputAudioUsage();
            return;
        }

        var sub = args[2].ToLowerInvariant();
        switch (sub)
        {
            case "add" when args.Length >= 4:
            {
                var result = await svc.CreateDeviceAsync(
                    new CreateInputAudioRequest(
                        args[3],
                        args.Length >= 5 ? args[4] : null,
                        args.Length >= 6 ? string.Join(' ', args[5..]) : null));
                ids.PrintJson(result);
                break;
            }
            case "add":
                Console.Error.WriteLine("resource inputaudio add <name> [deviceIdentifier] [description]");
                break;

            case "get" when args.Length >= 4:
            {
                var result = await svc.GetDeviceByIdAsync(ids.Resolve(args[3]));
                if (result is not null)
                    ids.PrintJson(result);
                else
                    Console.Error.WriteLine("Not found.");
                break;
            }
            case "get":
                Console.Error.WriteLine("resource inputaudio get <id>");
                break;

            case "list":
            {
                var result = await svc.ListDevicesAsync();
                ids.PrintJson(result);
                break;
            }

            case "update" when args.Length >= 5:
            {
                var result = await svc.UpdateDeviceAsync(
                    ids.Resolve(args[3]),
                    new UpdateInputAudioRequest(
                        args.Length >= 5 ? args[4] : null,
                        args.Length >= 6 ? args[5] : null));
                if (result is not null)
                    ids.PrintJson(result);
                else
                    Console.Error.WriteLine("Not found.");
                break;
            }
            case "update":
                Console.Error.WriteLine("resource inputaudio update <id> [name] [deviceIdentifier]");
                break;

            case "delete" when args.Length >= 4:
            {
                var deleted = await svc.DeleteDeviceAsync(ids.Resolve(args[3]));
                Console.WriteLine(deleted ? "Done." : "Not found.");
                break;
            }
            case "delete":
                Console.Error.WriteLine("resource inputaudio delete <id>");
                break;

            case "sync":
            {
                var result = await svc.SyncDevicesAsync();
                ids.PrintJson(result);
                break;
            }

            default:
                Console.Error.WriteLine($"Unknown command: resource inputaudio {sub}");
                PrintInputAudioUsage();
                break;
        }
    }

    private static void PrintInputAudioUsage()
    {
        Console.Error.WriteLine("Usage:");
        Console.Error.WriteLine("  resource inputaudio add <name> [identifier] [description]");
        Console.Error.WriteLine("  resource inputaudio get <id>                   Show an input audio");
        Console.Error.WriteLine("  resource inputaudio list                       List all input audios");
        Console.Error.WriteLine("  resource inputaudio update <id> [name] [id]    Update an input audio");
        Console.Error.WriteLine("  resource inputaudio delete <id>                Delete an input audio");
        Console.Error.WriteLine("  resource inputaudio sync                       Import system input audios");
    }

    // ═══════════════════════════════════════════════════════════════
    // Endpoint Mapping
    // ═══════════════════════════════════════════════════════════════

    public void MapEndpoints(object app)
    {
        var endpoints = (Microsoft.AspNetCore.Routing.IEndpointRouteBuilder)app;
        endpoints.MapTranscriptionStreaming();
        endpoints.MapInputAudioEndpoints();
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

    public async Task SeedDataAsync(IServiceProvider services, CancellationToken ct)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TranscriptionDbContext>();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<TranscriptionModule>>();

        var exists = await db.InputAudios
            .AnyAsync(d => d.DeviceIdentifier == "default", ct);
        if (exists)
            return;

        logger.LogInformation("Seeding default input audio.");

        db.InputAudios.Add(new InputAudioDB
        {
            Name = "Default",
            DeviceIdentifier = "default",
            Description = "System default audio input device"
        });

        await db.SaveChangesAsync(ct);
    }

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
