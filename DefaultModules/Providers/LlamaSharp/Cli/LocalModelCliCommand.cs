using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Contracts.DTOs.LocalModels;
using SharpClaw.Contracts.Modules;
using SharpClaw.Contracts.Providers;
using SharpClaw.Modules.Providers.LlamaSharp.Services;

namespace SharpClaw.Modules.Providers.LlamaSharp.Cli;

internal static class LocalModelCliCommand
{
    public static async Task HandleAsync(
        string[] args, IServiceProvider sp, CancellationToken ct)
    {
        var ids = sp.GetRequiredService<ICliIdResolver>();
        var svc = sp.GetRequiredService<LocalModelService>();

        if (args.Length < 2)
        {
            PrintUsage();
            return;
        }

        var sub = args[1].ToLowerInvariant();
        switch (sub)
        {
            case "download":
                await HandleDownload(args, svc, ids, ct);
                break;
            case "list":
                ids.PrintJson(await svc.ListLocalModelsAsync(ct));
                break;
            case "load" when args.Length >= 3:
                await HandleLoad(args, svc, ids, ct);
                break;
            case "load":
                Console.Error.WriteLine("localmodel load <id> [--gpu-layers <n>] [--ctx <size>] [--mmproj <path>]");
                break;
            case "unload" when args.Length >= 3:
                await svc.UnloadModelAsync(ids.Resolve(args[2]), ct);
                Console.WriteLine("Unpinned.");
                break;
            case "unload":
                Console.Error.WriteLine("localmodel unload <id>");
                break;
            case "mmproj" when args.Length >= 4:
            {
                var modelId = ids.Resolve(args[2]);
                var raw = args[3];
                var path = string.Equals(raw, "none", StringComparison.OrdinalIgnoreCase) ? null : raw;
                await svc.SetMmprojPathAsync(modelId, path, ct);
                Console.WriteLine(path is null
                    ? $"Cleared mmproj path for model {modelId}."
                    : $"Set mmproj path for model {modelId}: {path}");
                break;
            }
            case "mmproj":
                Console.Error.WriteLine("localmodel mmproj <id> <path|none>");
                break;
            case "delete" when args.Length >= 3:
            {
                var modelId = ids.Resolve(args[2]);
                var ok = await svc.DeleteLocalModelAsync(modelId, ct);
                Console.WriteLine(ok ? "Deleted." : "Not found.");
                break;
            }
            case "delete":
                Console.Error.WriteLine("localmodel delete <id>");
                break;
            default:
                Console.Error.WriteLine($"Unknown command: localmodel {sub}");
                PrintUsage();
                break;
        }
    }

    private static async Task HandleDownload(
        string[] args, LocalModelService svc, ICliIdResolver ids, CancellationToken ct)
    {
        if (args.Length >= 4 && args[2].Equals("list", StringComparison.OrdinalIgnoreCase))
        {
            ids.PrintJson(await svc.ListAvailableFilesAsync(args[3], ct));
            return;
        }

        if (args.Length < 3)
        {
            Console.Error.WriteLine("localmodel download <url> [--name <alias>] [--quant <Q4_K_M>] [--gpu-layers <n>]");
            Console.Error.WriteLine("localmodel download list <url>");
            return;
        }

        var url = args[2];
        string? name = null;
        string? quant = null;
        int? gpuLayers = null;

        for (var i = 3; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--name" when i + 1 < args.Length: name = args[++i]; break;
                case "--quant" when i + 1 < args.Length: quant = args[++i]; break;
                case "--gpu-layers" when i + 1 < args.Length && int.TryParse(args[i + 1], out var gl):
                    gpuLayers = gl; i++; break;
            }
        }

        var progress = new Progress<double>(p =>
            Console.Write($"\rDownloading... {(int)(p * 100)}%"));
        Console.WriteLine();

        var result = await svc.DownloadAndRegisterAsync(
            new DownloadModelRequest(url, name, quant, gpuLayers, WellKnownProviderKeys.LlamaSharp),
            progress, ct);
        Console.WriteLine();
        ids.PrintJson(result);
    }

    private static async Task HandleLoad(
        string[] args, LocalModelService svc, ICliIdResolver ids, CancellationToken ct)
    {
        var modelId = ids.Resolve(args[2]);
        int? gpuLayers = null;
        uint? contextSize = null;
        string? mmprojPath = null;

        for (var i = 3; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--gpu-layers" when i + 1 < args.Length && int.TryParse(args[i + 1], out var gl):
                    gpuLayers = gl; i++; break;
                case "--ctx" when i + 1 < args.Length && uint.TryParse(args[i + 1], out var cs):
                    contextSize = cs; i++; break;
                case "--mmproj" when i + 1 < args.Length:
                    mmprojPath = args[++i]; break;
            }
        }

        Console.Write("Pinning model (loading into memory)...");
        await svc.LoadModelAsync(modelId, new LoadModelRequest(gpuLayers, contextSize, mmprojPath), ct);
        Console.WriteLine(" ready. Model will stay loaded until 'localmodel unload'.");
    }

    private static void PrintUsage()
    {
        Console.Error.WriteLine("Usage:");
        Console.Error.WriteLine("  localmodel download <url> [--name <alias>] [--quant <Q4_K_M>] [--gpu-layers <n>]");
        Console.Error.WriteLine("  localmodel download list <url>");
        Console.Error.WriteLine("  localmodel list");
        Console.Error.WriteLine("  localmodel load <id> [--gpu-layers <n>] [--ctx <size>] [--mmproj <path>]");
        Console.Error.WriteLine("  localmodel unload <id>");
        Console.Error.WriteLine("  localmodel mmproj <id> <path|none>");
        Console.Error.WriteLine("  localmodel delete <id>");
    }
}
