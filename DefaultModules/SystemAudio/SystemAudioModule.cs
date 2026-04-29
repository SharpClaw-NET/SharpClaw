using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using SharpClaw.Contracts.Modules;
using SharpClaw.Contracts.Persistence;
using SharpClaw.Modules.SystemAudio.Capture;
using SharpClaw.Modules.SystemAudio.DTOs;
using SharpClaw.Modules.SystemAudio.Handlers;
using SharpClaw.Modules.SystemAudio.Models;
using SharpClaw.Modules.SystemAudio.Services;

namespace SharpClaw.Modules.SystemAudio;

/// <summary>
/// Default module: input audio device CRUD and WASAPI audio capture.
/// Owns the <see cref="InputAudioDB"/> resource type and exports the
/// <c>system_audio_capture</c> contract consumed by transcription and
/// other audio-aware modules.
/// </summary>
public sealed class SystemAudioModule : ISharpClawModule
{
    public string Id => "sharpclaw_systemaudio";
    public string DisplayName => "System Audio";
    public string ToolPrefix => "sa";

    // ═══════════════════════════════════════════════════════════════
    // DI Registration
    // ═══════════════════════════════════════════════════════════════

    public void ConfigureServices(IServiceCollection services)
    {
        // Audio capture (WASAPI, Windows only)
        services.AddSingleton<IAudioCaptureProvider, WasapiAudioCaptureProvider>();
        services.AddSingleton<SharedAudioCaptureManager>();

        // Input audio device CRUD + resolver
        services.AddScoped<InputAudioService>();
        services.AddScoped<IInputAudioDeviceResolver, InputAudioDeviceResolver>();
        services.AddScoped(sp => sp.GetRequiredService<IModuleDbContextFactory>()
            .CreateDbContext<SystemAudioDbContext>());
    }

    // ═══════════════════════════════════════════════════════════════
    // Contracts
    // ═══════════════════════════════════════════════════════════════

    public IReadOnlyList<ModuleContractExport> ExportedContracts =>
    [
        new("system_audio_capture",
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
            var db = sp.GetRequiredService<SystemAudioDbContext>();
            return await db.InputAudios.Select(a => a.Id).ToListAsync(ct);
        },
        LoadLookupItems: static async (sp, ct) =>
        {
            var db = sp.GetRequiredService<SystemAudioDbContext>();
            return await db.InputAudios.Select(a => new ValueTuple<Guid, string>(a.Id, a.Name)).ToListAsync(ct);
        }, DefaultResourceKey: "inputaudio"),
    ];

    // ═══════════════════════════════════════════════════════════════
    // Tool Definitions
    // ═══════════════════════════════════════════════════════════════

    public IReadOnlyList<ModuleToolDefinition> GetToolDefinitions() => [];

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
        var svc = sp.GetRequiredService<InputAudioService>();

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
        endpoints.MapInputAudioEndpoints();
    }

    // ═══════════════════════════════════════════════════════════════
    // Tool Execution
    // ═══════════════════════════════════════════════════════════════

    public Task<string> ExecuteToolAsync(
        string toolName, JsonElement parameters, AgentJobContext job,
        IServiceProvider sp, CancellationToken ct)
        => throw new InvalidOperationException(
            $"Module '{Id}' does not register any tools.");

    // ═══════════════════════════════════════════════════════════════
    // Lifecycle
    // ═══════════════════════════════════════════════════════════════

    public async Task SeedDataAsync(IServiceProvider services, CancellationToken ct)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SystemAudioDbContext>();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<SystemAudioModule>>();

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
}
