using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using SharpClaw.Application.API.Handlers;
using SharpClaw.Application.Core.Modules;
using SharpClaw.Application.Services;
using SharpClaw.Application.Services.Auth;
using SharpClaw.Contracts.DTOs.AgentActions;
using SharpClaw.Contracts.DTOs.Agents;
using SharpClaw.Contracts.DTOs.Auth;
using SharpClaw.Contracts.DTOs.Chat;
using SharpClaw.Contracts.DTOs.Contexts;
using SharpClaw.Contracts.DTOs.Channels;
using SharpClaw.Contracts.DTOs.Threads;
using SharpClaw.Contracts.DTOs.Tasks;
using SharpClaw.Contracts.DTOs.Models;
using SharpClaw.Contracts.DTOs.Providers;
using SharpClaw.Contracts.DTOs.DefaultResources;
using SharpClaw.Contracts.DTOs.LocalModels;
using SharpClaw.Contracts.DTOs.Roles;
using SharpClaw.Contracts.DTOs.Tools;
using SharpClaw.Contracts.DTOs.Users;
using SharpClaw.Utils.Security;
using SharpClaw.Contracts.Enums;
using SharpClaw.Contracts.Modules;
using SharpClaw.Infrastructure.Persistence;

namespace SharpClaw.Application.API.Cli;

public static class CliDispatcher
{
    private static readonly JsonSerializerOptions JsonPrint = new()
    {
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private static string? _currentUser;
    private static Guid? _currentUserId;
    private static Guid? _currentChannelId;
    private static Guid? _currentThreadId;
    private static bool _chatMode;
    private static bool IsLoggedIn => _currentUser is not null;

    [Conditional("DEBUG")]
    private static void DebugLog(string message) => Debug.WriteLine(message, "SharpClaw.CLI");

    private static readonly HashSet<string> PublicCommands =
        ["login", "register", "help", "--help", "-h"];

    /// <summary>
    /// <summary>
    /// Runs an interactive REPL alongside the API server.
    /// <para>
    /// When stdin is not a TTY (redirected, closed, or the host is a
    /// non-interactive service/container), the REPL is skipped and this
    /// method waits on the cancellation token instead. This prevents the
    /// bug where a launched-detached API process immediately reads EOF
    /// from its closed stdin, `Console.ReadLine` returns null, and the
    /// whole host shuts down a beat after startup — see bug #1 in
    /// docs/internal/local-inference-pipeline-debug-report.md.
    /// </para>
    /// <para>
    /// Returns whether the REPL actually ran. Callers (specifically
    /// <c>Program.cs</c>) use this to decide whether to suppress console
    /// logging for the duration of the REPL: in headless mode we want
    /// console logging to stay visible because stdout is the only
    /// feedback channel.
    /// </para>
    /// </summary>
    public static async Task<bool> RunInteractiveAsync(IServiceProvider services, CancellationToken ct)
    {
        if (Console.IsInputRedirected)
        {
            Log.Information(
                "stdin is redirected; skipping interactive REPL. " +
                "API will run until cancelled (Ctrl+C or host shutdown).");
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown path.
            }
            return false;
        }

        Console.WriteLine();
        Console.WriteLine("Type 'help' for available commands, 'exit' to quit.");
        Console.WriteLine("Log in with: login <username> <password>");
        Console.WriteLine();

        while (!ct.IsCancellationRequested)
        {
            var prompt = !IsLoggedIn
                ? "sharpclaw> "
                : _chatMode
                    ? $"sharpclaw ({_currentUser}) 💬> "
                    : $"sharpclaw ({_currentUser})> ";
            Console.Write(prompt);

            string? line;
            try
            {
                line = await Task.Run(Console.ReadLine, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (line is null)
                break;

            line = line.Trim();
            if (line.Length == 0)
                continue;

            if (line is "exit" or "quit")
                break;

            DebugLog($"Command: {line}");

            if (line is "logout")
            {
                _currentUser = null;
                _currentUserId = null;
                _currentChannelId = null;
                _currentThreadId = null;
                _chatMode = false;
                Console.WriteLine("Logged out.");
                DebugLog("Response: Logged out.");
                Console.WriteLine();
                continue;
            }

            var args = ParseArgs(line);

            if (!IsLoggedIn && !PublicCommands.Contains(args[0].ToLowerInvariant()))
            {
                Console.Error.WriteLine("Please log in first: login <username> <password>");
                DebugLog("Response: Not logged in, command rejected.");
                Console.WriteLine();
                continue;
            }

            // In chat mode, only !-prefixed escape commands break out;
            // everything else is sent as a chat message.
            if (_chatMode)
            {
                if (TryHandleChatModeEscape(line))
                {
                    Console.WriteLine();
                    continue;
                }

                args = ["chat", .. args];
            }

            try
            {
                if (!await TryHandleAsync(args, services))
                {
                    Console.Error.WriteLine($"Unknown command: {args[0]}. Type 'help' for usage.");
                    DebugLog($"Response: Unknown command '{args[0]}'.");
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
                DebugLog($"Error: {ex}");
            }

            Console.WriteLine();
        }

        return true;
    }

    private static string[] ParseArgs(string input)
    {
        var args = new List<string>();
        var span = input.AsSpan().Trim();
        var inQuotes = false;
        var start = 0;

        for (var i = 0; i <= span.Length; i++)
        {
            if (i == span.Length || (!inQuotes && span[i] == ' ') || (span[i] == '"'))
            {
                if (span[i..].Length > 0 && span[i] == '"')
                {
                    inQuotes = !inQuotes;
                    if (!inQuotes)
                    {
                        args.Add(span[(start + 1)..i].ToString());
                        start = i + 1;
                    }
                    else
                    {
                        start = i;
                    }
                    continue;
                }

                if (i > start)
                    args.Add(span[start..i].ToString());

                start = i + 1;
            }
        }

        return [.. args];
    }

    /// <summary>
    /// Tries to handle a CLI command by calling the same handler methods
    /// that back the minimal API endpoints. Returns true if a command was
    /// handled (app should exit).
    /// </summary>
    public static async Task<bool> TryHandleAsync(string[] args, IServiceProvider services)
    {
        if (args.Length == 0)
            return false;

        using var scope = services.CreateScope();
        var sp = scope.ServiceProvider;

        // Populate the scoped session with the CLI user identity.
        var session = sp.GetRequiredService<SessionService>();
        session.UserId = _currentUserId;

        var command = args[0].ToLowerInvariant();

        IResult? result = command switch
        {
            "register" => await HandleRegisterCommand(args, sp),
            "login" => await HandleLoginCommand(args, sp),
            "provider" => await HandleProviderCommand(args, sp),
            "model" => await HandleModelCommand(args, sp),
            "agent" => await HandleAgentCommand(args, sp),
            "context" or "ctx" => await HandleContextCommand(args, sp),
            "channel" or "chan" => await HandleChannelCommand(args, sp),
            "thread" => await HandleThreadCommand(args, sp),
            "chat" => await HandleChatCommand(args, sp),
            "job" => await HandleJobCommand(args, sp),
            "role" => await HandleRoleCommand(args, sp),
            "user" => await HandleUserCommand(args, sp),
            "resource" => await HandleResourceCommand(args, sp),
            "task" => await HandleTaskCommand(args, sp),
            "module" => await HandleModuleCommand(args, sp),
            "tools" => await HandleToolAwarenessSetCommand(args, sp),
            "bio" => await HandleBioCommand(args, sp),
            "env" => await HandleEnvCommand(args, sp),
            "db" => await HandleDbCommand(args, sp),
            "health" => await HandleHealthCommand(sp),
            "me" => await AuthHandlers.Me(
                sp.GetRequiredService<SessionService>(),
                sp.GetRequiredService<AuthService>()),
            "help" or "--help" or "-h" => PrintHelp(sp),
            _ => null
        };

        if (result is null)
        {
            // Try module-provided top-level CLI commands.
            var registry = sp.GetRequiredService<ModuleRegistry>();
            var moduleCmd = registry.TryResolveTopLevelCommand(command);
            if (moduleCmd is not null)
            {
                await moduleCmd.Handler(args, sp, CancellationToken.None);
                return true;
            }
            return false;
        }

        await PrintResultAsync(result);
        return true;
    }

    private static async Task<IResult?> HandleRegisterCommand(string[] args, IServiceProvider sp)
    {
        if (args.Length < 3)
        {
            PrintUsage("register <username> <password>");
            return Results.Ok();
        }

        var result = await AuthHandlers.Register(
            new RegisterRequest(args[1], args[2]),
            sp.GetRequiredService<AuthService>());

        if (result is IStatusCodeHttpResult { StatusCode: StatusCodes.Status200OK })
        {
            _currentUser = args[1];
            var db = sp.GetRequiredService<SharpClawDbContext>();
            _currentUserId = (await db.Users.FirstOrDefaultAsync(u => u.Username == args[1]))?.Id;
        }

        return result;
    }

    private static async Task<IResult?> HandleLoginCommand(string[] args, IServiceProvider sp)
    {
        if (args.Length < 3)
        {
            PrintUsage("login <username> <password>");
            return Results.Ok();
        }

        var result = await AuthHandlers.Login(
            new LoginRequest(args[1], args[2], args.Length >= 4 && args[3] == "--remember"),
            sp.GetRequiredService<AuthService>());

        if (result is IStatusCodeHttpResult { StatusCode: StatusCodes.Status200OK })
        {
            _currentUser = args[1];
            var db = sp.GetRequiredService<SharpClawDbContext>();
            _currentUserId = (await db.Users.FirstOrDefaultAsync(u => u.Username == args[1]))?.Id;
        }

        return result;
    }

    private static async Task<IResult?> HandleProviderCommand(string[] args, IServiceProvider sp)
    {
        if (args.Length < 2)
        {
            PrintUsage(
                "provider add <name> <type> [endpoint]",
                "provider get <providerId>",
                "provider list",
                "provider update <providerId> [name] [endpoint]",
                "provider delete <providerId>",
                "provider set-key <providerId> <apiKey>",
                "provider login <providerId>",
                "provider sync-models <providerId>",
                "provider cost <providerId> [--days <n>]",
                "provider cost-total [--days <n>] [--simple] [--all]");
            return Results.Ok();
        }

        var sub = args[1].ToLowerInvariant();
        var svc = sp.GetRequiredService<ProviderService>();

        return sub switch
        {
            "add" when args.Length >= 4 && Enum.TryParse<ProviderType>(args[3], true, out var pt)
                => await ProviderHandlers.Create(
                    new CreateProviderRequest(
                        args[2], pt,
                        pt == ProviderType.Custom && args.Length >= 5 ? args[4] : null),
                    svc),
            "add" when args.Length < 4
                => UsageResult("provider add <name> <type>",
                    "Types: OpenAI, Anthropic, OpenRouter, GoogleVertexAI,",
                    "       GoogleVertexAIOpenAi, GoogleGemini, GoogleGeminiOpenAi,",
                    "       ZAI, VercelAIGateway, XAI, Groq, Cerebras,",
                    "       Mistral, GitHubCopilot, Minimax, Custom"),
            "add" => UsageResult("Unknown provider type. Valid types: " +
                     string.Join(", ", Enum.GetNames<ProviderType>())),

            "get" when args.Length >= 3
                => await ProviderHandlers.GetById(CliIdMap.Resolve(args[2]), svc),
            "get" => UsageResult("provider get <id>"),

            "list" => await ProviderHandlers.List(svc),

            "update" when args.Length >= 4
                => await ProviderHandlers.Update(
                    CliIdMap.Resolve(args[2]),
                    new UpdateProviderRequest(
                        args.Length >= 4 ? args[3] : null,
                        args.Length >= 5 ? args[4] : null),
                    svc),
            "update" => UsageResult("provider update <id> <name> [endpoint]"),

            "delete" when args.Length >= 3
                => await ProviderHandlers.Delete(CliIdMap.Resolve(args[2]), svc),
            "delete" => UsageResult("provider delete <id>"),

            "set-key" when args.Length >= 4
                => await ProviderHandlers.SetApiKey(CliIdMap.Resolve(args[2]), new SetApiKeyRequest(args[3]), svc),
            "set-key" => UsageResult("provider set-key <id> <apiKey>"),

            "login" when args.Length >= 3
                => await HandleDeviceCodeLoginAsync(CliIdMap.Resolve(args[2]), svc, ct: default),
            "login" => UsageResult("provider login <id>"),

            "sync-models" when args.Length >= 3
                => await HandleProviderSync(CliIdMap.Resolve(args[2]), svc),
            "sync-models" => UsageResult("provider sync-models <id>"),

            "refresh-caps" when args.Length >= 3
                => await HandleRefreshCaps(CliIdMap.Resolve(args[2]), svc),
            "refresh-caps" => UsageResult("provider refresh-caps <id>"),

            "cost" when args.Length >= 3
                => await HandleProviderCost(CliIdMap.Resolve(args[2]), ParseDaysFlag(args, 3), sp),
            "cost" => UsageResult("provider cost <id> [--days <n>]"),

            "cost-total"
                => await HandleProviderCostTotal(args, sp),

            _ => UsageResult($"Unknown sub-command: provider {sub}. Try 'help' for usage.")
        };
    }

    private static async Task<IResult?> HandleModelCommand(string[] args, IServiceProvider sp)
    {
        if (args.Length < 2)
        {
            PrintUsage(
                "model add <name> <providerId> [--cap <capabilities>]",
                "  <name> must be the exact model ID from the provider API",
                "    (e.g. gpt-4o, claude-sonnet-4-20250514, gemini-2.5-flash).",
                "  Tip: use 'provider sync-models <id>' to auto-import models.",
                "  Capabilities (comma-separated): Chat, Transcription,",
                "    ImageGeneration, Embedding, TextToSpeech",
                "model get <id>",
                "model list [--provider <id>]           List models (optionally by provider)",
                "model update <id> <name> [--cap <capabilities>]",
                "model delete <id>",
                "",
                "Local models:",
                "model download <url> [--name <alias>] [--quant <Q4_K_M>] [--gpu-layers <n>] [--provider <LlamaSharp|Whisper>]",
                "model download list <url>",
                "model load <id> [--gpu-layers <n>] [--ctx <size>]  Pin model (keep loaded)",
                "model unload <id>                                  Unpin model",
                "model local list",
                "  Models auto-load on chat and auto-unload when idle.",
                "  Use load/unload to keep frequently-used models resident.");
            return Results.Ok();
        }

        var sub = args[1].ToLowerInvariant();
        var svc = sp.GetRequiredService<ModelService>();

        return sub switch
        {
            "add" when args.Length >= 4
                => await ModelHandlers.Create(
                    new CreateModelRequest(
                        args[2],
                        CliIdMap.Resolve(args[3]),
                        ParseCapabilities(args, 4)),
                    svc),
            "add" => UsageResult(
                "model add <name> <providerId> [--cap Chat,Transcription]",
                "  <name> must be the exact provider model ID (e.g. gpt-4o).",
                "  Tip: use 'provider sync-models <id>' to auto-import models."),

            "get" when args.Length >= 3
                => await ModelHandlers.GetById(CliIdMap.Resolve(args[2]), svc),
            "get" => UsageResult("model get <id>"),

            "list" => await HandleModelList(args, svc),

            "update" when args.Length >= 4
                => await ModelHandlers.Update(
                    CliIdMap.Resolve(args[2]),
                    new UpdateModelRequest(
                        args[3],
                        ParseCapabilitiesNullable(args, 4)),
                    svc),
            "update" => UsageResult("model update <id> <name> [--cap Chat,Transcription]"),

            "delete" when args.Length >= 3
                => await ModelHandlers.Delete(CliIdMap.Resolve(args[2]), svc),
            "delete" => UsageResult("model delete <id>"),

            // ── Local model commands ──────────────────────────────
            "download" => await HandleModelDownload(args, sp),
            "load" when args.Length >= 3 => await HandleModelLoad(args, sp),
            "load" => UsageResult("model load <id> [--gpu-layers <n>] [--ctx <size>] [--mmproj <path>]",
                "  Pins the model so it stays loaded between requests."),
            "unload" when args.Length >= 3 => await HandleModelUnload(args, sp),
            "unload" => UsageResult("model unload <id>",
                "  Unpins the model. Stops immediately if no active requests."),
            "mmproj" when args.Length >= 4 => await HandleModelSetMmproj(args, sp),
            "mmproj" => UsageResult("model mmproj <id> <path>",
                "  Sets the CLIP/mmproj file path for a registered LlamaSharp model.",
                "  Use 'none' as path to clear it."),
            "local" when args.Length >= 3 && args[2].ToLowerInvariant() == "list"
                => await HandleLocalModelList(sp),
            "local" => UsageResult("model local list"),

            _ => UsageResult($"Unknown sub-command: model {sub}. Try 'help' for usage.")
        };
    }

    private static async Task<IResult> HandleModelList(string[] args, ModelService svc)
    {
        Guid? providerId = null;
        for (var i = 2; i < args.Length; i++)
        {
            if (args[i] is "--provider" && i + 1 < args.Length)
            {
                providerId = CliIdMap.Resolve(args[++i]);
                break;
            }
        }
        return await ModelHandlers.List(svc, providerId);
    }

    // ── Local model CLI handlers ─────────────────────────────────

    private static async Task<IResult> HandleModelDownload(string[] args, IServiceProvider sp)
    {
        // model download list <url>
        if (args.Length >= 4 && args[2].ToLowerInvariant() == "list")
        {
            var localSvc = sp.GetRequiredService<LocalModelService>();
            var files = await localSvc.ListAvailableFilesAsync(args[3]);
            return Results.Ok(files);
        }

        // model download <url> [--name <alias>] [--quant <Q4_K_M>] [--gpu-layers <n>] [--provider <LlamaSharp|Whisper>]
        if (args.Length < 3)
            return UsageResult(
                "model download <url> [--name <alias>] [--quant <Q4_K_M>] [--gpu-layers <n>] [--provider <LlamaSharp|Whisper>]",
                "model download list <url>");

        var url = args[2];
        string? name = null;
        string? quant = null;
        int? gpuLayers = null;
        ProviderType? providerType = null;

        for (var i = 3; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--name" when i + 1 < args.Length:
                    name = args[++i]; break;
                case "--quant" when i + 1 < args.Length:
                    quant = args[++i]; break;
                case "--gpu-layers" when i + 1 < args.Length && int.TryParse(args[i + 1], out var gl):
                    gpuLayers = gl; i++; break;
                case "--provider" when i + 1 < args.Length:
                    var provStr = args[++i];
                    if (!Enum.TryParse<ProviderType>(provStr, ignoreCase: true, out var pt)
                        || pt is not (ProviderType.LlamaSharp or ProviderType.Whisper))
                        return Results.BadRequest(
                            $"Unknown provider '{provStr}'. Supported values: LlamaSharp, Whisper.");
                    providerType = pt;
                    break;
            }
        }

        var svc = sp.GetRequiredService<LocalModelService>();
        var progress = new Progress<double>(p =>
        {
            var pct = (int)(p * 100);
            Console.Write($"\rDownloading... {pct}%");
        });

        Console.WriteLine();

        if (providerType is null)
        {
            var both = await svc.DownloadAndRegisterBothAsync(
                new DownloadModelRequest(url, name, quant, gpuLayers), progress);
            if (both.LlamaSharp is { } l) Console.WriteLine($"LlamaSharp: {l.Name} ({l.Id})");
            else                          Console.WriteLine("LlamaSharp: skipped");
            if (both.Whisper    is { } w) Console.WriteLine($"Whisper:    {w.Name} ({w.Id})");
            else                          Console.WriteLine("Whisper:    skipped");
            return Results.Ok(both);
        }

        var result = await svc.DownloadAndRegisterAsync(
            new DownloadModelRequest(url, name, quant, gpuLayers, providerType), progress);
        return Results.Ok(result);
    }

    private static async Task<IResult> HandleModelLoad(string[] args, IServiceProvider sp)
    {
        var modelId = CliIdMap.Resolve(args[2]);
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

        var svc = sp.GetRequiredService<LocalModelService>();
        Console.Write("Pinning model (loading into memory)...");
        await svc.LoadModelAsync(
            modelId, new LoadModelRequest(gpuLayers, contextSize, mmprojPath));
        Console.WriteLine(" ready. Model will stay loaded until 'model unload'.");
        return Results.Ok(new { modelId, pinned = true });
    }

    private static async Task<IResult> HandleModelUnload(string[] args, IServiceProvider sp)
    {
        var modelId = CliIdMap.Resolve(args[2]);
        var svc = sp.GetRequiredService<LocalModelService>();
        await svc.UnloadModelAsync(modelId);
        return Results.Ok(new { modelId, status = "unpinned" });
    }

    private static async Task<IResult> HandleLocalModelList(IServiceProvider sp)
    {
        var svc = sp.GetRequiredService<LocalModelService>();
        return Results.Ok(await svc.ListLocalModelsAsync());
    }

    private static async Task<IResult> HandleModelSetMmproj(string[] args, IServiceProvider sp)
    {
        var modelId   = CliIdMap.Resolve(args[2]);
        var rawPath   = args[3];
        var mmproj    = string.Equals(rawPath, "none", StringComparison.OrdinalIgnoreCase)
            ? null
            : rawPath;

        var svc = sp.GetRequiredService<LocalModelService>();
        await svc.SetMmprojPathAsync(modelId, mmproj);

        Console.WriteLine(mmproj is null
            ? $"Cleared mmproj path for model {modelId}."
            : $"Set mmproj path for model {modelId}: {mmproj}");

        return Results.Ok(new { modelId, mmprojPath = mmproj });
    }

    private static ModelCapability ParseCapabilities(string[] args, int startIndex)
    {
        return ParseCapabilitiesNullable(args, startIndex) ?? ModelCapability.Chat;
    }

    private static ModelCapability? ParseCapabilitiesNullable(string[] args, int startIndex)
    {
        for (var i = startIndex; i < args.Length - 1; i++)
        {
            if (args[i] is "--cap" or "-c")
            {
                if (Enum.TryParse<ModelCapability>(args[i + 1], true, out var cap))
                    return cap;
            }
        }
        return null;
    }

    private static async Task<IResult?> HandleAgentCommand(string[] args, IServiceProvider sp)
    {
        if (args.Length < 2)
        {
            PrintUsage(
                "agent add <name> <modelId>  [system prompt] [--max-tokens <n>] [--params <json>] [--header <template>]",
                "agent get <id>",
                "agent list",
                "agent update <id> <name> [modelId] [system prompt] [--max-tokens <n>] [--params <json>] [--header <template>]",
                "agent role <id> <roleId>                  Assign a role (use 'role list')",
                "agent role <id> none                      Remove role",
                "agent sync-with-models                    Create default-<model> agents",
                "agent delete <id>");
            return Results.Ok();
        }

        var sub = args[1].ToLowerInvariant();
        var svc = sp.GetRequiredService<AgentService>();

        return sub switch
        {
            "add" when args.Length >= 4
                => await HandleAgentAdd(args, sp, svc),
            "add" => UsageResult("agent add <name> <modelId> [system prompt]"),

            "get" when args.Length >= 3
                => await AgentHandlers.GetById(CliIdMap.Resolve(args[2]), svc),
            "get" => UsageResult("agent get <id>"),

            "list" => await AgentHandlers.List(svc),

            "update" when args.Length >= 4
                => await HandleAgentUpdate(args, svc),
            "update" => UsageResult("agent update <id> <name> [modelId] [system prompt] [--max-tokens <n>]"),

            "role" when args.Length >= 4 && args[3].Equals("none", StringComparison.OrdinalIgnoreCase)
                => await HandleAgentRoleAssign(CliIdMap.Resolve(args[2]), Guid.Empty, svc),
            "role" when args.Length >= 4
                => await HandleAgentRoleAssign(CliIdMap.Resolve(args[2]), CliIdMap.Resolve(args[3]), svc),
            "role" => UsageResult("agent role <agentId> <roleId>  (use 'role list' to find IDs)"),

            "sync-with-models" => await HandleAgentSyncWithModels(svc),

            "delete" when args.Length >= 3
                => await AgentHandlers.Delete(CliIdMap.Resolve(args[2]), svc),
            "delete" => UsageResult("agent delete <id>"),

            _ => UsageResult($"Unknown sub-command: agent {sub}. Try 'help' for usage.")
        };
    }

    private static async Task<IResult> HandleAgentRoleAssign(
        Guid agentId, Guid roleId, AgentService svc)
    {
        try
        {
            var result = await svc.AssignRoleAsync(agentId, roleId);
            return result is not null ? Results.Ok(result) : Results.NotFound();
        }
        catch (UnauthorizedAccessException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return Results.Unauthorized();
        }
    }

    /// <summary>
    /// Handles <c>agent add</c>. If the supplied model ID is actually a
    /// local model file ID, auto-resolves it to the parent model ID so
    /// users can pass either.
    /// </summary>
    private static async Task<IResult> HandleAgentAdd(
        string[] args, IServiceProvider sp, AgentService svc)
    {
        var modelId = CliIdMap.Resolve(args[3]);

        // Check if the ID is a local model file rather than a model
        var db = sp.GetRequiredService<SharpClawDbContext>();
        var localFile = await db.LocalModelFiles.FirstOrDefaultAsync(f => f.Id == modelId);
        if (localFile is not null)
        {
            Console.WriteLine($"(Resolved local file #{CliIdMap.GetOrAssign(localFile.Id)} → model #{CliIdMap.GetOrAssign(localFile.ModelId)})");
            modelId = localFile.ModelId;
        }

        // Separate flags from positional args (system prompt)
        int? maxTokens = null;
        Dictionary<string, JsonElement>? providerParams = null;
        float? temperature = null;
        float? topP = null;
        int? topK = null;
        float? frequencyPenalty = null;
        float? presencePenalty = null;
        string[]? stop = null;
        int? seed = null;
        JsonElement? responseFormat = null;
        string? reasoningEffort = null;
        string? customChatHeader = null;
        Guid? toolAwarenessSetId = null;
        bool? disableToolSchemas = null;
        var promptParts = new List<string>();
        for (var i = 4; i < args.Length; i++)
        {
            if (args[i] is "--max-tokens" && i + 1 < args.Length && int.TryParse(args[i + 1], out var mt))
            {
                maxTokens = mt; i++;
            }
            else if (args[i] is "--params" && i + 1 < args.Length)
            {
                try { providerParams = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(args[i + 1]); }
                catch (JsonException ex) { Console.Error.WriteLine($"Invalid --params JSON: {ex.Message}"); return Results.BadRequest("Invalid --params JSON."); }
                i++;
            }
            else if (args[i] is "--temperature" or "--temp" && i + 1 < args.Length && float.TryParse(args[i + 1], System.Globalization.CultureInfo.InvariantCulture, out var temp))
            {
                temperature = temp; i++;
            }
            else if (args[i] is "--top-p" && i + 1 < args.Length && float.TryParse(args[i + 1], System.Globalization.CultureInfo.InvariantCulture, out var tp))
            {
                topP = tp; i++;
            }
            else if (args[i] is "--top-k" && i + 1 < args.Length && int.TryParse(args[i + 1], out var tk))
            {
                topK = tk; i++;
            }
            else if (args[i] is "--frequency-penalty" && i + 1 < args.Length && float.TryParse(args[i + 1], System.Globalization.CultureInfo.InvariantCulture, out var fp))
            {
                frequencyPenalty = fp; i++;
            }
            else if (args[i] is "--presence-penalty" && i + 1 < args.Length && float.TryParse(args[i + 1], System.Globalization.CultureInfo.InvariantCulture, out var pp))
            {
                presencePenalty = pp; i++;
            }
            else if (args[i] is "--stop" && i + 1 < args.Length)
            {
                stop = args[i + 1].Split(','); i++;
            }
            else if (args[i] is "--seed" && i + 1 < args.Length && int.TryParse(args[i + 1], out var sd))
            {
                seed = sd; i++;
            }
            else if (args[i] is "--response-format" && i + 1 < args.Length)
            {
                try { responseFormat = JsonDocument.Parse(args[i + 1]).RootElement.Clone(); }
                catch (JsonException ex) { Console.Error.WriteLine($"Invalid --response-format JSON: {ex.Message}"); return Results.BadRequest("Invalid --response-format JSON."); }
                i++;
            }
            else if (args[i] is "--reasoning-effort" && i + 1 < args.Length)
            {
                reasoningEffort = args[i + 1]; i++;
            }
            else if (args[i] is "--header" && i + 1 < args.Length)
            {
                customChatHeader = args[i + 1]; i++;
            }
            else if (args[i] is "--tools" && i + 1 < args.Length)
            {
                toolAwarenessSetId = CliIdMap.Resolve(args[i + 1]); i++;
            }
            else if (args[i] is "--no-tools")
            {
                disableToolSchemas = true;
            }
            else
            {
                promptParts.Add(args[i]);
            }
        }

        var prompt = promptParts.Count > 0 ? string.Join(' ', promptParts) : null;
        return await AgentHandlers.Create(
            new CreateAgentRequest(args[2], modelId, prompt, maxTokens,
                Temperature: temperature, TopP: topP, TopK: topK,
                FrequencyPenalty: frequencyPenalty, PresencePenalty: presencePenalty,
                Stop: stop, Seed: seed, ResponseFormat: responseFormat,
                ReasoningEffort: reasoningEffort, ProviderParameters: providerParams,
                CustomChatHeader: customChatHeader, ToolAwarenessSetId: toolAwarenessSetId,
                DisableToolSchemas: disableToolSchemas), svc);
    }

    private static async Task<IResult> HandleAgentUpdate(string[] args, AgentService svc)
    {
        var agentId = CliIdMap.Resolve(args[2]);
        var name = args[3];

        // Separate flags from positional args
        int? maxTokens = null;
        Dictionary<string, JsonElement>? providerParams = null;
        float? temperature = null;
        float? topP = null;
        int? topK = null;
        float? frequencyPenalty = null;
        float? presencePenalty = null;
        string[]? stop = null;
        int? seed = null;
        JsonElement? responseFormat = null;
        string? reasoningEffort = null;
        string? customChatHeader = null;
        Guid? toolAwarenessSetId = null;
        bool? disableToolSchemas = null;
        var positional = new List<string>();
        for (var i = 4; i < args.Length; i++)
        {
            if (args[i] is "--max-tokens" && i + 1 < args.Length && int.TryParse(args[i + 1], out var mt))
            {
                maxTokens = mt; i++;
            }
            else if (args[i] is "--params" && i + 1 < args.Length)
            {
                try { providerParams = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(args[i + 1]); }
                catch (JsonException ex) { Console.Error.WriteLine($"Invalid --params JSON: {ex.Message}"); return Results.BadRequest("Invalid --params JSON."); }
                i++;
            }
            else if (args[i] is "--temperature" or "--temp" && i + 1 < args.Length && float.TryParse(args[i + 1], System.Globalization.CultureInfo.InvariantCulture, out var temp))
            {
                temperature = temp; i++;
            }
            else if (args[i] is "--top-p" && i + 1 < args.Length && float.TryParse(args[i + 1], System.Globalization.CultureInfo.InvariantCulture, out var tp))
            {
                topP = tp; i++;
            }
            else if (args[i] is "--top-k" && i + 1 < args.Length && int.TryParse(args[i + 1], out var tk))
            {
                topK = tk; i++;
            }
            else if (args[i] is "--frequency-penalty" && i + 1 < args.Length && float.TryParse(args[i + 1], System.Globalization.CultureInfo.InvariantCulture, out var fp))
            {
                frequencyPenalty = fp; i++;
            }
            else if (args[i] is "--presence-penalty" && i + 1 < args.Length && float.TryParse(args[i + 1], System.Globalization.CultureInfo.InvariantCulture, out var pp))
            {
                presencePenalty = pp; i++;
            }
            else if (args[i] is "--stop" && i + 1 < args.Length)
            {
                stop = args[i + 1].Split(','); i++;
            }
            else if (args[i] is "--seed" && i + 1 < args.Length && int.TryParse(args[i + 1], out var sd))
            {
                seed = sd; i++;
            }
            else if (args[i] is "--response-format" && i + 1 < args.Length)
            {
                try { responseFormat = JsonDocument.Parse(args[i + 1]).RootElement.Clone(); }
                catch (JsonException ex) { Console.Error.WriteLine($"Invalid --response-format JSON: {ex.Message}"); return Results.BadRequest("Invalid --response-format JSON."); }
                i++;
            }
            else if (args[i] is "--reasoning-effort" && i + 1 < args.Length)
            {
                reasoningEffort = args[i + 1]; i++;
            }
            else if (args[i] is "--header" && i + 1 < args.Length)
            {
                customChatHeader = args[i + 1]; i++;
            }
            else if (args[i] is "--tools" && i + 1 < args.Length)
            {
                toolAwarenessSetId = CliIdMap.Resolve(args[i + 1]); i++;
            }
            else if (args[i] is "--no-tools")
            {
                disableToolSchemas = true;
            }
            else
            {
                positional.Add(args[i]);
            }
        }

        Guid? modelId = positional.Count >= 1 ? TryResolveId(positional[0]) : null;
        string? prompt = null;
        if (modelId is not null && positional.Count >= 2)
            prompt = string.Join(' ', positional.Skip(1));
        else if (modelId is null && positional.Count >= 1)
            prompt = string.Join(' ', positional);

        var request = new UpdateAgentRequest(name, modelId, prompt, maxTokens,
            Temperature: temperature, TopP: topP, TopK: topK,
            FrequencyPenalty: frequencyPenalty, PresencePenalty: presencePenalty,
            Stop: stop, Seed: seed, ResponseFormat: responseFormat,
            ReasoningEffort: reasoningEffort, ProviderParameters: providerParams,
            CustomChatHeader: customChatHeader, ToolAwarenessSetId: toolAwarenessSetId,
            DisableToolSchemas: disableToolSchemas);
        return await AgentHandlers.Update(agentId, request, svc);
    }

    private static async Task<IResult> HandleAgentSyncWithModels(AgentService svc)
    {
        var created = await svc.SyncWithModelsAsync();
        if (created.Count == 0)
        {
            Console.WriteLine("All chat-capable models already have a default agent.");
        }
        else
        {
            Console.WriteLine($"Created {created.Count} agent(s):");
            foreach (var a in created)
                Console.WriteLine($"  #{CliIdMap.GetOrAssign(a.Id)} {a.Name}  ({a.ProviderName}/{a.ModelName})");
        }

        return Results.Ok(created);
    }

    private static async Task<IResult?> HandleContextCommand(string[] args, IServiceProvider sp)
    {
        if (args.Length < 2)
        {
            PrintUsage(
                "context add <agentId> [name]               Create a context",
                "context list [agentId]                     List contexts",
                "context get <id>                           Show context details",
                "context update <id> <name>                 Rename a context",
                "context agents <id>                        List allowed agents",
                "context agents <id> add <agentId>          Allow an agent",
                "context agents <id> remove <agentId>       Remove an allowed agent",
                "context defaults <id>                      Show default resources",
                "context defaults <id> set <key> <resId>    Set a default resource",
                "context defaults <id> clear <key>          Clear a default resource",
                "context delete <id>                        Delete a context");
            return Results.Ok();
        }

        var sub = args[1].ToLowerInvariant();
        var svc = sp.GetRequiredService<ContextService>();

        return sub switch
        {
            "add" when args.Length >= 3
                => await ChannelContextHandlers.Create(
                    new CreateContextRequest(
                        CliIdMap.Resolve(args[2]),
                        args.Length >= 4 ? string.Join(' ', args[3..]) : null),
                    svc),
            "add" => UsageResult("context add <agentId> [name]"),

            "list"
                => await ChannelContextHandlers.List(svc,
                    args.Length >= 3 ? CliIdMap.Resolve(args[2]) : null),

            "get" when args.Length >= 3
                => await ChannelContextHandlers.GetById(CliIdMap.Resolve(args[2]), svc),
            "get" => UsageResult("context get <id>"),

            "update" when args.Length >= 4
                => await ChannelContextHandlers.Update(
                    CliIdMap.Resolve(args[2]),
                    new UpdateContextRequest(string.Join(' ', args[3..])),
                    svc),
            "update" => UsageResult("context update <id> <name>"),

            "agents" when args.Length >= 3
                => await HandleContextAgents(args, svc),
            "agents" => UsageResult("context agents <contextId> [add|remove <agentId>]"),

            "defaults" when args.Length >= 3
                => await HandleDefaults("context", CliIdMap.Resolve(args[2]), args[3..], sp),
            "defaults" => UsageResult("context defaults <id> [set <key> <resId> | clear <key>]"),

            "delete" when args.Length >= 3
                => await ChannelContextHandlers.Delete(CliIdMap.Resolve(args[2]), svc),
            "delete" => UsageResult("context delete <id>"),

            _ => UsageResult($"Unknown sub-command: context {sub}. Try 'help' for usage.")
        };
    }

    private static async Task<IResult> HandleContextAgents(string[] args, ContextService svc)
    {
        var contextId = CliIdMap.Resolve(args[2]);

        // context agents <id>  — list
        if (args.Length == 3)
        {
            var ctx = await svc.GetByIdAsync(contextId);
            if (ctx is null) return Results.NotFound();
            PrintJsonWithShortIds(new
            {
                ContextId = ctx.Id,
                DefaultAgent = ctx.Agent,
                AllowedAgents = ctx.AllowedAgents
            });
            return Results.Ok();
        }

        var action = args[3].ToLowerInvariant();

        if (action == "add" && args.Length >= 5)
        {
            var agentToAdd = CliIdMap.Resolve(args[4]);
            var ctx = await svc.GetByIdAsync(contextId);
            if (ctx is null) return Results.NotFound();

            var updated = ctx.AllowedAgents.Select(a => a.Id).ToList();
            if (!updated.Contains(agentToAdd))
                updated.Add(agentToAdd);

            return await ChannelContextHandlers.Update(
                contextId,
                new UpdateContextRequest(AllowedAgentIds: updated),
                svc);
        }

        if (action == "remove" && args.Length >= 5)
        {
            var agentToRemove = CliIdMap.Resolve(args[4]);
            var ctx = await svc.GetByIdAsync(contextId);
            if (ctx is null) return Results.NotFound();

            var updated = ctx.AllowedAgents.Select(a => a.Id).Where(id => id != agentToRemove).ToList();

            return await ChannelContextHandlers.Update(
                contextId,
                new UpdateContextRequest(AllowedAgentIds: updated),
                svc);
        }

        return UsageResult("context agents <contextId> [add|remove <agentId>]");
    }

    /// <summary>
    /// Shared handler for <c>channel defaults</c> and <c>context defaults</c>.
    /// <list type="bullet">
    ///   <item><c>defaults &lt;id&gt;</c> — show current defaults (effective for channels).</item>
    ///   <item><c>defaults &lt;id&gt; set &lt;key&gt; &lt;resourceId&gt;</c> — set one field.</item>
    ///   <item><c>defaults &lt;id&gt; clear &lt;key&gt;</c> — clear one field.</item>
    /// </list>
    /// Keys: safeshell, dangshell, container, website, search, localinfo,
    /// externalinfo, inputaudio, agent, task, skill, transcriptionmodel, editor.
    /// </summary>
    private static async Task<IResult> HandleDefaults(
        string scope, Guid entityId, string[] extra, IServiceProvider sp)
    {
        var svc = sp.GetRequiredService<DefaultResourceSetService>();

        // Show
        if (extra.Length == 0)
        {
            var result = scope == "channel"
                ? await svc.GetForChannelAsync(entityId)
                : await svc.GetForContextAsync(entityId);

            if (result is null) return Results.NotFound();
            PrintJsonWithShortIds(result);
            return Results.Ok();
        }

        var action = extra[0].ToLowerInvariant();

        if (action == "set" && extra.Length >= 3)
        {
            var key = extra[1].ToLowerInvariant();
            var value = CliIdMap.Resolve(extra[2]);

            // GET current, merge, PUT — API does a full replace.
            var current = scope == "channel"
                ? await svc.GetForChannelAsync(entityId)
                : await svc.GetForContextAsync(entityId);

            if (current is null) return Results.NotFound();

            var req = MergeDefaultResourceKey(current, key, value);
            if (req is null)
                return UsageResult($"Unknown key '{extra[1]}'. Valid keys: safeshell, dangshell, container, website, search, localinfo, externalinfo, inputaudio, displaydevice, agent, task, skill, transcriptionmodel, editor");

            var result = scope == "channel"
                ? await svc.SetForChannelAsync(entityId, req)
                : await svc.SetForContextAsync(entityId, req);

            if (result is null) return Results.NotFound();
            PrintJsonWithShortIds(result);
            return Results.Ok();
        }

        if (action == "clear" && extra.Length >= 2)
        {
            var key = extra[1].ToLowerInvariant();

            var current = scope == "channel"
                ? await svc.GetForChannelAsync(entityId)
                : await svc.GetForContextAsync(entityId);

            if (current is null) return Results.NotFound();

            var req = MergeDefaultResourceKey(current, key, null);
            if (req is null)
                return UsageResult($"Unknown key '{extra[1]}'.");

            var result = scope == "channel"
                ? await svc.SetForChannelAsync(entityId, req)
                : await svc.SetForContextAsync(entityId, req);

            if (result is null) return Results.NotFound();
            PrintJsonWithShortIds(result);
            return Results.Ok();
        }

        return UsageResult($"{scope} defaults <id> [set <key> <resId> | clear <key>]");
    }

    /// <summary>
    /// Merges a single key change into the existing defaults, returning
    /// a full <see cref="SetDefaultResourcesRequest"/> for the PUT.
    /// Returns <c>null</c> when the key is unrecognised.
    /// </summary>
    private static SetDefaultResourcesRequest? MergeDefaultResourceKey(
        DefaultResourcesResponse current, string key, Guid? value)
    {
        var d = current;
        return key switch
        {
            "safeshell" => new(d.DangerousShellResourceId, value, d.ContainerResourceId, d.WebsiteResourceId, d.SearchEngineResourceId, d.InternalDatabaseResourceId, d.ExternalDatabaseResourceId, d.InputAudioResourceId, d.DisplayDeviceResourceId, d.AgentResourceId, d.TaskResourceId, d.SkillResourceId, d.TranscriptionModelId, d.EditorSessionResourceId),
            "dangshell" or "dangerousshell" => new(value, d.SafeShellResourceId, d.ContainerResourceId, d.WebsiteResourceId, d.SearchEngineResourceId, d.InternalDatabaseResourceId, d.ExternalDatabaseResourceId, d.InputAudioResourceId, d.DisplayDeviceResourceId, d.AgentResourceId, d.TaskResourceId, d.SkillResourceId, d.TranscriptionModelId, d.EditorSessionResourceId),
            "container" => new(d.DangerousShellResourceId, d.SafeShellResourceId, value, d.WebsiteResourceId, d.SearchEngineResourceId, d.InternalDatabaseResourceId, d.ExternalDatabaseResourceId, d.InputAudioResourceId, d.DisplayDeviceResourceId, d.AgentResourceId, d.TaskResourceId, d.SkillResourceId, d.TranscriptionModelId, d.EditorSessionResourceId),
            "website" => new(d.DangerousShellResourceId, d.SafeShellResourceId, d.ContainerResourceId, value, d.SearchEngineResourceId, d.InternalDatabaseResourceId, d.ExternalDatabaseResourceId, d.InputAudioResourceId, d.DisplayDeviceResourceId, d.AgentResourceId, d.TaskResourceId, d.SkillResourceId, d.TranscriptionModelId, d.EditorSessionResourceId),
            "search" or "searchengine" => new(d.DangerousShellResourceId, d.SafeShellResourceId, d.ContainerResourceId, d.WebsiteResourceId, value, d.InternalDatabaseResourceId, d.ExternalDatabaseResourceId, d.InputAudioResourceId, d.DisplayDeviceResourceId, d.AgentResourceId, d.TaskResourceId, d.SkillResourceId, d.TranscriptionModelId, d.EditorSessionResourceId),
            "internaldb" => new(d.DangerousShellResourceId, d.SafeShellResourceId, d.ContainerResourceId, d.WebsiteResourceId, d.SearchEngineResourceId, value, d.ExternalDatabaseResourceId, d.InputAudioResourceId, d.DisplayDeviceResourceId, d.AgentResourceId, d.TaskResourceId, d.SkillResourceId, d.TranscriptionModelId, d.EditorSessionResourceId),
            "externaldb" => new(d.DangerousShellResourceId, d.SafeShellResourceId, d.ContainerResourceId, d.WebsiteResourceId, d.SearchEngineResourceId, d.InternalDatabaseResourceId, value, d.InputAudioResourceId, d.DisplayDeviceResourceId, d.AgentResourceId, d.TaskResourceId, d.SkillResourceId, d.TranscriptionModelId, d.EditorSessionResourceId),
            "inputaudio" or "audio" => new(d.DangerousShellResourceId, d.SafeShellResourceId, d.ContainerResourceId, d.WebsiteResourceId, d.SearchEngineResourceId, d.InternalDatabaseResourceId, d.ExternalDatabaseResourceId, value, d.DisplayDeviceResourceId, d.AgentResourceId, d.TaskResourceId, d.SkillResourceId, d.TranscriptionModelId, d.EditorSessionResourceId),
            "displaydevice" or "display" => new(d.DangerousShellResourceId, d.SafeShellResourceId, d.ContainerResourceId, d.WebsiteResourceId, d.SearchEngineResourceId, d.InternalDatabaseResourceId, d.ExternalDatabaseResourceId, d.InputAudioResourceId, value, d.AgentResourceId, d.TaskResourceId, d.SkillResourceId, d.TranscriptionModelId, d.EditorSessionResourceId),
            "agent" => new(d.DangerousShellResourceId, d.SafeShellResourceId, d.ContainerResourceId, d.WebsiteResourceId, d.SearchEngineResourceId, d.InternalDatabaseResourceId, d.ExternalDatabaseResourceId, d.InputAudioResourceId, d.DisplayDeviceResourceId, value, d.TaskResourceId, d.SkillResourceId, d.TranscriptionModelId, d.EditorSessionResourceId),
            "task" => new(d.DangerousShellResourceId, d.SafeShellResourceId, d.ContainerResourceId, d.WebsiteResourceId, d.SearchEngineResourceId, d.InternalDatabaseResourceId, d.ExternalDatabaseResourceId, d.InputAudioResourceId, d.DisplayDeviceResourceId, d.AgentResourceId, value, d.SkillResourceId, d.TranscriptionModelId, d.EditorSessionResourceId),
            "skill" => new(d.DangerousShellResourceId, d.SafeShellResourceId, d.ContainerResourceId, d.WebsiteResourceId, d.SearchEngineResourceId, d.InternalDatabaseResourceId, d.ExternalDatabaseResourceId, d.InputAudioResourceId, d.DisplayDeviceResourceId, d.AgentResourceId, d.TaskResourceId, value, d.TranscriptionModelId, d.EditorSessionResourceId),
            "transcriptionmodel" or "model" => new(d.DangerousShellResourceId, d.SafeShellResourceId, d.ContainerResourceId, d.WebsiteResourceId, d.SearchEngineResourceId, d.InternalDatabaseResourceId, d.ExternalDatabaseResourceId, d.InputAudioResourceId, d.DisplayDeviceResourceId, d.AgentResourceId, d.TaskResourceId, d.SkillResourceId, value, d.EditorSessionResourceId),
            "editorsession" or "editor" => new(d.DangerousShellResourceId, d.SafeShellResourceId, d.ContainerResourceId, d.WebsiteResourceId, d.SearchEngineResourceId, d.InternalDatabaseResourceId, d.ExternalDatabaseResourceId, d.InputAudioResourceId, d.DisplayDeviceResourceId, d.AgentResourceId, d.TaskResourceId, d.SkillResourceId, d.TranscriptionModelId, value),
            _ => null,
        };
    }

    /// <summary>
    /// In chat mode only <c>!chat toggle</c>, <c>!chat exit</c>,
    /// <c>!toggle</c>, and <c>!exit</c> are recognised as escape
    /// commands that switch back to normal CLI mode.
    /// Returns <c>true</c> if the input was an escape command (already handled).
    /// </summary>
    private static bool TryHandleChatModeEscape(string line)
    {
        var trimmed = line.TrimStart();
        if (trimmed.Length == 0 || trimmed[0] != '!')
            return false;

        var command = trimmed[1..].Trim().ToLowerInvariant();
        if (command is "chat toggle" or "chat exit" or "toggle" or "exit")
        {
            _chatMode = false;
            Console.WriteLine("Chat mode OFF \u2014 normal command mode.");
            return true;
        }

        return false;
    }

    private static async Task<IResult?> HandleChatCommand(string[] args, IServiceProvider sp)
    {
        // ── chat toggle ──────────────────────────────────────────
        if (args.Length >= 2 && args[1].Equals("toggle", StringComparison.OrdinalIgnoreCase))
        {
            _chatMode = !_chatMode;
            Console.WriteLine(_chatMode
                ? "Chat mode ON — all input is sent as chat. Type !exit or !chat toggle to return to normal mode."
                : "Chat mode OFF — normal command mode.");
            return Results.Ok();
        }

        if (args.Length < 2)
        {
            PrintUsage(
                "chat [--agent <id>] [--thread <id>] <message>",
                "  chat <threadId> <message>               Send to a thread (infers channel)",
                "  chat toggle                             Toggle chat mode on/off",
                "  --agent overrides the channel's default agent.",
                "  --thread sends the message in a thread (with history).",
                "  A thread ID can be used anywhere a channel ID is expected.");
            return Results.Ok();
        }

        var channelSvc = sp.GetRequiredService<ChannelService>();
        var threadSvc = sp.GetRequiredService<ThreadService>();

        // Auto-select latest channel if none is selected
        if (_currentChannelId is null)
        {
            var latest = await channelSvc.GetLatestActiveAsync();
            if (latest is null)
            {
                Console.Error.WriteLine("Error: No channel selected and no channels exist.");
                Console.Error.WriteLine("Create one first: channel add <agentId> [title]");
                return Results.Ok();
            }

            _currentChannelId = latest.Id;
            Console.WriteLine($"No channel selected. Opening latest channel: \"{latest.Title}\" ({CliIdMap.GetOrAssign(latest.Id)})");
        }

        // Parse --agent, --thread flags and collect message parts
        Guid? agentId = null;
        Guid? threadId = _currentThreadId;
        var messageParts = new List<string>();
        for (var i = 1; i < args.Length; i++)
        {
            if (args[i] is "--agent" or "-a" && i + 1 < args.Length)
                agentId = CliIdMap.Resolve(args[++i]);
            else if (args[i] is "--thread" or "-t" && i + 1 < args.Length)
                threadId = CliIdMap.Resolve(args[++i]);
            else
                messageParts.Add(args[i]);
        }

        // If the first positional arg looks like an ID (short or GUID) and no
        // --thread was supplied, check whether it is a thread ID. If so, infer
        // the channel from it and remove it from the message parts.
        if (threadId == _currentThreadId && messageParts.Count >= 2)
        {
            var maybeId = messageParts[0];
            var normalized = maybeId.StartsWith('#') ? maybeId[1..] : maybeId;
            if (int.TryParse(normalized, out _) || Guid.TryParse(maybeId, out _))
            {
                try
                {
                    var resolved = CliIdMap.Resolve(maybeId);
                    var asThread = await threadSvc.GetByIdAsync(resolved);
                    if (asThread is not null)
                    {
                        threadId = asThread.Id;
                        _currentChannelId = asThread.ChannelId;
                        messageParts.RemoveAt(0);
                    }
                    else
                    {
                        // Could be a channel ID — try to select it
                        var asChannel = await channelSvc.GetByIdAsync(resolved);
                        if (asChannel is not null)
                        {
                            _currentChannelId = asChannel.Id;
                            messageParts.RemoveAt(0);
                        }
                    }
                }
                catch { /* not a valid ID — leave it as message text */ }
            }
        }

        // If --thread was given with a thread ID, resolve the channel from it
        if (threadId is not null && threadId != _currentThreadId)
        {
            var threadInfo = await threadSvc.GetByIdAsync(threadId.Value);
            if (threadInfo is not null)
                _currentChannelId = threadInfo.ChannelId;
        }

        if (messageParts.Count == 0)
        {
            PrintUsage("chat [--agent <id>] [--thread <id>] <message>");
            return Results.Ok();
        }

        var chatService = sp.GetRequiredService<ChatService>();
        var request = new ChatRequest(string.Join(' ', messageParts), agentId, ChatClientType.CLI);
        var wroteText = false;

        async Task<bool> CliApprovalCallback(
            AgentJobResponse job, CancellationToken ct)
        {
            Console.Write("Approve? (y/n): ");
            var input = await Task.Run(Console.ReadLine, ct);
            return input?.Trim().Equals("y", StringComparison.OrdinalIgnoreCase) == true;
        }

        try
        {
            await foreach (var evt in chatService.SendMessageStreamAsync(
                _currentChannelId.Value, request, CliApprovalCallback, threadId))
            {
                switch (evt.Type)
                {
                    case ChatStreamEventType.TextDelta:
                        Console.Write(evt.Delta);
                        wroteText = true;
                        break;

                    case ChatStreamEventType.ToolCallStart:
                        if (wroteText) { Console.WriteLine(); wroteText = false; }
                        Console.WriteLine($"  [tool] #{CliIdMap.GetOrAssign(evt.Job!.Id)} {evt.Job.ActionKey ?? "unknown"} → {evt.Job.Status}");
                        break;

                    case ChatStreamEventType.ToolCallResult:
                        Console.WriteLine($"  [result] #{CliIdMap.GetOrAssign(evt.Result!.Id)} → {evt.Result.Status}");
                        break;

                    case ChatStreamEventType.ApprovalRequired:
                        if (wroteText) { Console.WriteLine(); wroteText = false; }
                        Console.Write($"  [approval] Job #{CliIdMap.GetOrAssign(evt.PendingJob!.Id)} ({evt.PendingJob.ActionKey ?? "unknown"}) requires approval. ");
                        break;

                    case ChatStreamEventType.ApprovalResult:
                        Console.WriteLine($"  [approval] → {evt.ApprovalOutcome!.Status}");
                        break;

                    case ChatStreamEventType.Error:
                        if (wroteText) { Console.WriteLine(); wroteText = false; }
                        Console.Error.WriteLine($"  [error] {evt.Error}");
                        break;

                    case ChatStreamEventType.Done:
                        if (wroteText) Console.WriteLine();
                        break;
                }
            }
        }
        catch (HttpRequestException ex)
        {
            if (wroteText) { Console.WriteLine(); wroteText = false; }
            Console.Error.WriteLine($"Error: {ex.Message}");
        }

        return Results.Ok();
    }

    private static async Task<IResult?> HandleChannelCommand(string[] args, IServiceProvider sp)
    {
        if (args.Length < 2)
        {
            PrintUsage(
                "channel add [--agent <id>] [--context <id>] [--header <template>] [title]",
                "  Either --agent or --context is required.",
                "channel list [agentId]                     List channels",
                "channel select <id>                        Select active channel",
                "channel get <id>                           Show channel details",
                "channel cost <id>                          Show token usage by agent",
                "channel attach <id> <contextId>            Attach to a context",
                "channel detach <id>                        Detach from context",
                "channel agents <id>                        List allowed agents",
                "channel agents <id> add <agentId>          Allow an agent",
                "channel agents <id> remove <agentId>       Remove an allowed agent",
                "channel defaults <id>                      Show default resources",
                "channel defaults <id> set <key> <resId>    Set a default resource",
                "channel defaults <id> clear <key>          Clear a default resource",
                "channel delete <id>                        Delete a channel");
            return Results.Ok();
        }

        var sub = args[1].ToLowerInvariant();
        var svc = sp.GetRequiredService<ChannelService>();

        return sub switch
        {
            "add" when args.Length >= 3
                => await HandleChannelAdd(args, svc),
            "add" => UsageResult("channel add [--agent <id>] [--context <id>] [title]"),

            "list" => await HandleChannelList(args, svc),

            "select" when args.Length >= 3
                => HandleChannelSelect(args),
            "select" => UsageResult("channel select <id>"),

            "get" when args.Length >= 3
                => await ChannelHandlers.GetById(CliIdMap.Resolve(args[2]), svc),
            "get" => UsageResult("channel get <id>"),

            "cost" when args.Length >= 3
                => await ChatHandlers.ChannelCost(CliIdMap.Resolve(args[2]), sp.GetRequiredService<ChatService>()),
            "cost" => UsageResult("channel cost <id>"),

            "attach" when args.Length >= 4
                => await ChannelHandlers.Update(
                    CliIdMap.Resolve(args[2]),
                    new UpdateChannelRequest(ContextId: CliIdMap.Resolve(args[3])),
                    svc),
            "attach" => UsageResult("channel attach <channelId> <contextId>"),

            "detach" when args.Length >= 3
                => await ChannelHandlers.Update(
                    CliIdMap.Resolve(args[2]),
                    new UpdateChannelRequest(ContextId: Guid.Empty),
                    svc),
            "detach" => UsageResult("channel detach <channelId>"),

            "agents" when args.Length >= 3
                => await HandleChannelAgents(args, svc),
            "agents" => UsageResult("channel agents <channelId> [add|remove <agentId>]"),

            "defaults" when args.Length >= 3
                => await HandleDefaults("channel", CliIdMap.Resolve(args[2]), args[3..], sp),
            "defaults" => UsageResult("channel defaults <id> [set <key> <resId> | clear <key>]"),

            "delete" when args.Length >= 3
                => await HandleChannelDelete(args, svc),
            "delete" => UsageResult("channel delete <id>"),

            _ => UsageResult($"Unknown sub-command: channel {sub}. Try 'help' for usage.")
        };
    }

    private static async Task<IResult> HandleChannelAdd(string[] args, ChannelService svc)
    {
        Guid? agentId = null;
        Guid? contextId = null;
        string? customChatHeader = null;
        Guid? toolAwarenessSetId = null;
        bool? disableToolSchemas = null;
        var titleParts = new List<string>();

        for (var i = 2; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--context" or "-c" when i + 1 < args.Length:
                    contextId = CliIdMap.Resolve(args[++i]);
                    break;
                case "--agent" or "-a" when i + 1 < args.Length:
                    agentId = CliIdMap.Resolve(args[++i]);
                    break;
                case "--header" when i + 1 < args.Length:
                    customChatHeader = args[++i];
                    break;
                case "--tools" when i + 1 < args.Length:
                    toolAwarenessSetId = CliIdMap.Resolve(args[++i]);
                    break;
                case "--no-tools":
                    disableToolSchemas = true;
                    break;
                default:
                    titleParts.Add(args[i]);
                    break;
            }
        }

        if (agentId is null && contextId is null)
        {
            Console.Error.WriteLine("Either --agent or --context is required.");
            return Results.Ok();
        }

        var title = titleParts.Count > 0 ? string.Join(' ', titleParts) : null;

        var result = await ChannelHandlers.Create(
            new CreateChannelRequest(agentId, title, ContextId: contextId, CustomChatHeader: customChatHeader, ToolAwarenessSetId: toolAwarenessSetId, DisableToolSchemas: disableToolSchemas), svc);

        // Auto-select the newly created channel
        if (result is IValueHttpResult { Value: ChannelResponse ch })
            _currentChannelId = ch.Id;

        return result;
    }

    private static async Task<IResult> HandleChannelList(string[] args, ChannelService svc)
    {
        Guid? agentId = args.Length >= 3 ? CliIdMap.Resolve(args[2]) : null;
        return await ChannelHandlers.List(svc, agentId);
    }

    private static IResult HandleChannelSelect(string[] args)
    {
        _currentChannelId = CliIdMap.Resolve(args[2]);
        _currentThreadId = null;
        Console.WriteLine($"Channel {args[2]} selected.");
        return Results.Ok();
    }

    private static async Task<IResult> HandleChannelDelete(string[] args, ChannelService svc)
    {
        var id = CliIdMap.Resolve(args[2]);
        var result = await ChannelHandlers.Delete(id, svc);

        // Clear selection if the deleted channel was the active one
        if (_currentChannelId == id)
        {
            _currentChannelId = null;
            _currentThreadId = null;
        }

        return result;
    }

    private static async Task<IResult> HandleChannelAgents(string[] args, ChannelService svc)
    {
        var channelId = CliIdMap.Resolve(args[2]);

        // channel agents <id>  — list
        if (args.Length == 3)
        {
            var ch = await svc.GetByIdAsync(channelId);
            if (ch is null) return Results.NotFound();
            PrintJsonWithShortIds(new
            {
                ChannelId = ch.Id,
                DefaultAgent = ch.Agent,
                AllowedAgents = ch.AllowedAgents
            });
            return Results.Ok();
        }

        var action = args[3].ToLowerInvariant();

        if (action == "add" && args.Length >= 5)
        {
            var agentToAdd = CliIdMap.Resolve(args[4]);
            var ch = await svc.GetByIdAsync(channelId);
            if (ch is null) return Results.NotFound();

            var updated = ch.AllowedAgents.Select(a => a.Id).ToList();
            if (!updated.Contains(agentToAdd))
                updated.Add(agentToAdd);

            return await ChannelHandlers.Update(
                channelId,
                new UpdateChannelRequest(AllowedAgentIds: updated),
                svc);
        }

        if (action == "remove" && args.Length >= 5)
        {
            var agentToRemove = CliIdMap.Resolve(args[4]);
            var ch = await svc.GetByIdAsync(channelId);
            if (ch is null) return Results.NotFound();

            var updated = ch.AllowedAgents.Select(a => a.Id).Where(id => id != agentToRemove).ToList();

            return await ChannelHandlers.Update(
                channelId,
                new UpdateChannelRequest(AllowedAgentIds: updated),
                svc);
        }

        return UsageResult("channel agents <channelId> [add|remove <agentId>]");
    }

    // ═══════════════════════════════════════════════════════════════
    // Thread
    // ═══════════════════════════════════════════════════════════════

    private static async Task<IResult?> HandleThreadCommand(string[] args, IServiceProvider sp)
    {
        if (args.Length < 2)
        {
            PrintUsage(
                "thread add [channelId] [name] [--max-messages <n>] [--max-chars <n>]",
                "  Create a thread (current channel if omitted)",
                "thread list [channelId]                    List threads in a channel",
                "thread get <id>                            Show thread details",
                "thread cost <id>                           Show token usage by agent",
                "thread update <id> [--name <name>] [--max-messages <n>] [--max-chars <n>]",
                "  Rename a thread or change history limits (0 to reset to default)",
                "thread select <id>                         Select active thread for chat",
                "thread deselect                            Deselect active thread",
                "thread delete <id>                         Delete a thread");
            return Results.Ok();
        }

        var sub = args[1].ToLowerInvariant();
        var svc = sp.GetRequiredService<ThreadService>();

        return sub switch
        {
            "add" => await HandleThreadAdd(args, svc),

            "list" => await HandleThreadList(args, svc),

            "get" when args.Length >= 3
                => await HandleThreadGet(CliIdMap.Resolve(args[2]), svc),
            "get" => UsageResult("thread get <id>"),

            "cost" when args.Length >= 3
                => await HandleThreadCost(CliIdMap.Resolve(args[2]), svc, sp.GetRequiredService<ChatService>()),
            "cost" => UsageResult("thread cost <id>"),

            "update" when args.Length >= 3
                => await HandleThreadUpdate(args, svc),
            "update" => UsageResult("thread update <id> [--name <name>] [--max-messages <n>] [--max-chars <n>]"),

            "select" when args.Length >= 3
                => HandleThreadSelect(args),
            "select" => UsageResult("thread select <id>"),

            "deselect" => HandleThreadDeselect(),

            "delete" when args.Length >= 3
                => await HandleThreadDelete(CliIdMap.Resolve(args[2]), svc),
            "delete" => UsageResult("thread delete <id>"),

            _ => UsageResult($"Unknown sub-command: thread {sub}. Try 'help' for usage.")
        };
    }

    private static async Task<IResult> HandleThreadAdd(string[] args, ThreadService svc)
    {
        // thread add [channelId] [name] [--max-messages <n>] [--max-chars <n>]
        Guid channelId;
        int? maxMessages = null;
        int? maxChars = null;
        var nameParts = new List<string>();

        // First pass: separate flags from positional args
        var positional = new List<string>();
        for (var i = 2; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--max-messages" when i + 1 < args.Length && int.TryParse(args[i + 1], out var mm):
                    maxMessages = mm; i++; break;
                case "--max-chars" when i + 1 < args.Length && int.TryParse(args[i + 1], out var mc):
                    maxChars = mc; i++; break;
                default:
                    positional.Add(args[i]); break;
            }
        }

        if (positional.Count >= 1)
        {
            var possibleId = TryResolveId(positional[0]);
            if (possibleId is not null)
            {
                channelId = possibleId.Value;
                nameParts.AddRange(positional.Skip(1));
            }
            else if (_currentChannelId is not null)
            {
                channelId = _currentChannelId.Value;
                nameParts.AddRange(positional);
            }
            else
            {
                Console.Error.WriteLine("No channel selected. Specify a channel ID or use 'channel select'.");
                return Results.Ok();
            }
        }
        else if (_currentChannelId is not null)
        {
            channelId = _currentChannelId.Value;
        }
        else
        {
            Console.Error.WriteLine("No channel selected. Specify a channel ID or use 'channel select'.");
            return Results.Ok();
        }

        var name = nameParts.Count > 0 ? string.Join(' ', nameParts) : null;

        var result = await ThreadHandlers.Create(
            channelId,
            new CreateThreadRequest(name, maxMessages, maxChars),
            svc);

        return result;
    }

    private static async Task<IResult> HandleThreadList(string[] args, ThreadService svc)
    {
        Guid channelId;
        if (args.Length >= 3)
            channelId = CliIdMap.Resolve(args[2]);
        else if (_currentChannelId is not null)
            channelId = _currentChannelId.Value;
        else
        {
            Console.Error.WriteLine("No channel selected. Specify a channel ID or use 'channel select'.");
            return Results.Ok();
        }

        return await ThreadHandlers.List(channelId, svc);
    }

    private static async Task<IResult> HandleThreadGet(Guid threadId, ThreadService svc)
        => await ThreadHandlers.GetById(Guid.Empty, threadId, svc);

    private static async Task<IResult> HandleThreadCost(
        Guid threadId, ThreadService threadSvc, ChatService chatSvc)
    {
        var thread = await threadSvc.GetByIdAsync(threadId);
        if (thread is null) return Results.NotFound();
        return await ChatHandlers.ThreadCost(thread.ChannelId, threadId, chatSvc);
    }

    private static async Task<IResult> HandleThreadUpdate(string[] args, ThreadService svc)
    {
        // thread update <id> [--name <name>] [--max-messages <n>] [--max-chars <n>]
        // Also supports legacy positional: thread update <id> <name>
        var threadId = CliIdMap.Resolve(args[2]);
        string? name = null;
        int? maxMessages = null;
        int? maxChars = null;
        var hasFlags = false;

        for (var i = 3; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--name" when i + 1 < args.Length:
                    name = args[++i]; hasFlags = true; break;
                case "--max-messages" when i + 1 < args.Length && int.TryParse(args[i + 1], out var mm):
                    maxMessages = mm; i++; hasFlags = true; break;
                case "--max-chars" when i + 1 < args.Length && int.TryParse(args[i + 1], out var mc):
                    maxChars = mc; i++; hasFlags = true; break;
            }
        }

        // Legacy positional: thread update <id> <name> (no flags used)
        if (!hasFlags && args.Length >= 4)
            name = string.Join(' ', args[3..]);

        var request = new UpdateThreadRequest(name, maxMessages, maxChars);
        var result = await ThreadHandlers.Update(Guid.Empty, threadId, request, svc);
        return result;
    }

    private static IResult HandleThreadSelect(string[] args)
    {
        _currentThreadId = CliIdMap.Resolve(args[2]);
        Console.WriteLine($"Thread {args[2]} selected. Chat messages will now include thread history.");
        return Results.Ok();
    }

    private static IResult HandleThreadDeselect()
    {
        _currentThreadId = null;
        Console.WriteLine("Thread deselected. Chat messages will be sent without history.");
        return Results.Ok();
    }

    private static async Task<IResult> HandleThreadDelete(Guid threadId, ThreadService svc)
    {
        var result = await ThreadHandlers.Delete(Guid.Empty, threadId, svc);

        if (_currentThreadId == threadId)
            _currentThreadId = null;

        return result;
    }

    private static async Task<IResult?> HandleRoleCommand(string[] args, IServiceProvider sp)
    {
        if (args.Length < 2)
        {
            PrintUsage(
                "role list                                 List all roles",
                "role get <roleId>                         Show a role",
                "role permissions <roleId>                 Show role permissions",
                "role permissions <roleId> set [flags...]  Set role permissions",
                "",
                "Permission flags:",
                "  --flag FlagKey[:clearance]              Grant a global flag with optional clearance",
                "  --create-sub-agents                     Grant CanCreateSubAgents",
                "  --create-containers                     Grant CanCreateContainers",
                "  --register-databases                    Grant CanRegisterDatabases",
                "  --localhost-browser                     Grant CanAccessLocalhostInBrowser",
                "  --localhost-cli                         Grant CanAccessLocalhostCli",
                "  --click-desktop                         Grant CanClickDesktop",
                "  --type-on-desktop                       Grant CanTypeOnDesktop",
                "  --read-cross-thread-history             Grant CanReadCrossThreadHistory",
                "  --edit-agent-header                     Grant CanEditAgentHeader",
                "  --edit-channel-header                   Grant CanEditChannelHeader",
                "  --create-document-sessions              Grant CanCreateDocumentSessions",
                "  --enumerate-windows                     Grant CanEnumerateWindows",
                "  --focus-window                          Grant CanFocusWindow",
                "  --close-window                          Grant CanCloseWindow",
                "  --resize-window                         Grant CanResizeWindow",
                "  --send-hotkey                           Grant CanSendHotkey",
                "  --read-clipboard                        Grant CanReadClipboard",
                "  --write-clipboard                       Grant CanWriteClipboard",
                "  --dangerous-shell <id>[:<clearance>]    Add DangerousShell grant",
                "  --safe-shell <id>[:<clearance>]         Add SafeShell grant",
                "  --container <id>[:<clearance>]          Add Container grant",
                "  --website <id>[:<clearance>]            Add Website grant",
                "  --search-engine <id>[:<clearance>]      Add SearchEngine grant",
                "  --internal-db <id>[:<clearance>]        Add InternalDatabase grant",
                "  --external-db <id>[:<clearance>]        Add ExternalDatabase grant",
                "  --input-audio <id>[:<clearance>]        Add InputAudio grant",
                "  --agent <id>[:<clearance>]              Add Agent grant",
                "  --task <id>[:<clearance>]               Add Task grant",
                "  --skill <id>[:<clearance>]              Add Skill grant",
                "",
                "  Use 'all' as resource id for wildcard grant.");
            return Results.Ok();
        }

        var sub = args[1].ToLowerInvariant();
        var svc = sp.GetRequiredService<RoleService>();

        return sub switch
        {
            "list" => await HandleRoleList(svc),

            "get" when args.Length >= 3
                => await RoleHandlers.GetById(CliIdMap.Resolve(args[2]), svc),
            "get" => UsageResult("role get <roleId>"),

            "permissions" or "perms" when args.Length >= 3
                => await HandleRolePermissions(args, svc),
            "permissions" or "perms"
                => UsageResult("role permissions <roleId> [set ...]"),

            _ => UsageResult($"Unknown sub-command: role {sub}. Try 'help' for usage.")
        };
    }

    private static async Task<IResult> HandleRoleList(RoleService svc)
    {
        var roles = await svc.ListAsync();
        PrintJsonWithShortIds(roles);
        return Results.Ok();
    }

    private static async Task<IResult> HandleRolePermissions(
        string[] args, RoleService svc)
    {
        var roleId = CliIdMap.Resolve(args[2]);

        // role permissions <id>  — show
        if (args.Length == 3 || !args[3].Equals("set", StringComparison.OrdinalIgnoreCase))
        {
            var perms = await svc.GetPermissionsAsync(roleId);
            if (perms is null) return Results.NotFound();
            PrintJsonWithShortIds(perms);
            return Results.Ok();
        }

        // role permissions <id> set [flags...]
        // Syntax: --flag FlagKey[:clearance]
        //         --grant ResourceType id[:clearance]
        if (_currentUserId is null)
            return UsageResult("You must be logged in to set permissions.");

        var globalFlags = new Dictionary<string, PermissionClearance>();
        var resourceGrants = new Dictionary<string, List<ResourceGrant>>();

        for (var i = 4; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--flag" when i + 1 < args.Length:
                {
                    var (flagKey, flagClearance) = ParseFlagArg(args[++i]);
                    globalFlags[flagKey] = flagClearance;
                    break;
                }

                case "--grant" when i + 2 < args.Length:
                {
                    var resourceType = args[++i];
                    var grant = ParseResourceGrant(args[++i]);
                    if (!resourceGrants.TryGetValue(resourceType, out var list))
                    {
                        list = [];
                        resourceGrants[resourceType] = list;
                    }
                    list.Add(grant);
                    break;
                }
            }
        }

        var request = new SetRolePermissionsRequest(
            GlobalFlags: globalFlags.Count > 0 ? globalFlags : null,
            ResourceGrants: resourceGrants.Count > 0
                ? resourceGrants.ToDictionary(
                    kvp => kvp.Key,
                    kvp => (IReadOnlyList<ResourceGrant>)kvp.Value)
                : null);

        try
        {
            var result = await svc.SetPermissionsAsync(
                roleId, request, _currentUserId.Value);
            if (result is null) return Results.NotFound();
            PrintJsonWithShortIds(result);
            return Results.Ok();
        }
        catch (UnauthorizedAccessException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return Results.Ok();
        }
    }

    /// <summary>
    /// Parses a global-flag argument: <c>FlagKey[:clearance]</c>.
    /// If no clearance suffix is given, defaults to <see cref="PermissionClearance.Independent"/>.
    /// </summary>
    private static (string FlagKey, PermissionClearance Clearance) ParseFlagArg(string arg)
    {
        var parts = arg.Split(':', 2);
        var clearance = parts.Length > 1
            && Enum.TryParse<PermissionClearance>(parts[1], true, out var cl)
                ? cl
                : PermissionClearance.Independent;

        return (parts[0], clearance);
    }

    /// <summary>
    /// Parses a resource grant argument: <c>id[:clearance]</c>.
    /// The special value <c>all</c> maps to <see cref="Contracts.WellKnownIds.AllResources"/>.
    /// </summary>
    private static ResourceGrant ParseResourceGrant(string arg)
    {
        var parts = arg.Split(':', 2);
        var resourceId = parts[0].Equals("all", StringComparison.OrdinalIgnoreCase)
            ? Contracts.WellKnownIds.AllResources
            : CliIdMap.Resolve(parts[0]);

        var clearance = parts.Length > 1
            && Enum.TryParse<PermissionClearance>(parts[1], true, out var cl)
                ? cl
                : PermissionClearance.Unset;

        return new ResourceGrant(resourceId, clearance);
    }

    private static async Task<IResult?> HandleJobCommand(string[] args, IServiceProvider sp)
    {
        if (args.Length < 2)
        {
            PrintUsage(
                "job submit <channelId> <actionKey> [resourceId] [--agent <id>] [--model <id>] [--lang <code>]",
                "  --agent overrides the channel's default agent.",
                "job list [channelId]                       List jobs (current channel if omitted)",
                "job status <jobId>",
                "job approve <jobId>",
                "job stop <jobId>                           Stop a transcription job (complete)",
                "job pause <jobId>                          Pause a long-running job",
                "job resume <jobId>                         Resume a paused job",
                "job cancel <jobId>",
                "job listen <jobId>                         Stream live transcription segments",
                "",
                "Action keys are module tool names (e.g. execute_as_safe_shell, manage_agent,",
                "  cu_click_desktop, transcribe_from_audio_device).",
                "",
                "Transcription: submit transcribe_from_audio_device <channelId> <deviceId>",
                "  Optional flags: --model <id>, --lang <code>,",
                "    --mode <sliding|step|window>, --window <seconds>, --step <seconds>");
            return Results.Ok();
        }

        var sub = args[1].ToLowerInvariant();
        var svc = sp.GetRequiredService<AgentJobService>();
        var chatSvc = sp.GetRequiredService<ChatService>();

        return sub switch
        {
            // job submit <channelId> <actionKey> [resourceId] [flags...]
            "submit" when args.Length >= 4
                => await HandleJobSubmit(args, CliIdMap.Resolve(args[2]), args[3], 4, svc, chatSvc),

            "submit" when args.Length < 4
                => UsageResult(
                    "job submit <channelId> <actionKey> [resourceId] [--agent <id>] [--model <id>] [--lang <code>]"),

            "list" when args.Length >= 3
                => await AgentJobHandlers.List(CliIdMap.Resolve(args[2]), svc),
            "list" when _currentChannelId.HasValue
                => await AgentJobHandlers.List(_currentChannelId.Value, svc),
            "list" => UsageResult("job list [channelId]  (no current channel selected — specify a channel ID or use 'channel select')"),

            "status" when args.Length >= 3
                => await AgentJobHandlers.GetById(Guid.Empty, CliIdMap.Resolve(args[2]), svc, chatSvc),
            "status" => UsageResult("job status <jobId>"),

            "approve" when args.Length >= 3
                => await AgentJobHandlers.Approve(
                    Guid.Empty, CliIdMap.Resolve(args[2]),
                    new ApproveAgentJobRequest(),
                    svc, chatSvc),
            "approve" => UsageResult("job approve <jobId>"),

            "stop" when args.Length >= 3
                => await AgentJobHandlers.Stop(Guid.Empty, CliIdMap.Resolve(args[2]), svc, chatSvc),
            "stop" => UsageResult("job stop <jobId>"),

            "cancel" when args.Length >= 3
                => await AgentJobHandlers.Cancel(Guid.Empty, CliIdMap.Resolve(args[2]), svc, chatSvc),
            "cancel" => UsageResult("job cancel <jobId>"),

            "pause" when args.Length >= 3
                => await AgentJobHandlers.Pause(Guid.Empty, CliIdMap.Resolve(args[2]), svc, chatSvc),
            "pause" => UsageResult("job pause <jobId>"),

            "resume" when args.Length >= 3
                => await AgentJobHandlers.Resume(Guid.Empty, CliIdMap.Resolve(args[2]), svc, chatSvc),
            "resume" => UsageResult("job resume <jobId>"),

            "listen" when args.Length >= 3
                => await HandleJobListen(CliIdMap.Resolve(args[2]), svc),
            "listen" => UsageResult("job listen <jobId>"),

            _ => UsageResult($"Unknown sub-command: job {sub}. Try 'help' for usage.")
        };
    }

    private static async Task<IResult> HandleJobSubmit(
        string[] args, Guid channelId, string actionKey, int nextArg, AgentJobService svc, ChatService chatSvc)
    {
        // Resource ID is the next positional arg, unless it looks like a flag
        Guid? resourceId = args.Length > nextArg && !args[nextArg].StartsWith("--")
            ? CliIdMap.Resolve(args[nextArg])
            : null;
        var flagStart = resourceId is not null ? nextArg + 1 : nextArg;

        Guid? modelId = null;
        Guid? agentId = null;
        string? language = null;
        TranscriptionMode? transcriptionMode = null;
        int? windowSeconds = null;
        int? stepSeconds = null;

        for (var i = flagStart; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--model" or "-m" when i + 1 < args.Length:
                    modelId = CliIdMap.Resolve(args[++i]);
                    break;
                case "--agent" or "-a" when i + 1 < args.Length:
                    agentId = CliIdMap.Resolve(args[++i]);
                    break;
                case "--lang" or "-l" when i + 1 < args.Length:
                    language = args[++i];
                    break;
                case "--mode" when i + 1 < args.Length:
                    var modeArg = args[++i];
                    if (string.Equals(modeArg, "sliding", StringComparison.OrdinalIgnoreCase))
                        transcriptionMode = TranscriptionMode.SlidingWindow;
                    else if (string.Equals(modeArg, "window", StringComparison.OrdinalIgnoreCase))
                        transcriptionMode = TranscriptionMode.StrictWindow;
                    else if (Enum.TryParse<TranscriptionMode>(modeArg, true, out var m))
                        transcriptionMode = m;
                    break;
                case "--window" when i + 1 < args.Length:
                    if (int.TryParse(args[++i], out var w))
                        windowSeconds = w;
                    break;
                case "--step" when i + 1 < args.Length:
                    if (int.TryParse(args[++i], out var s))
                        stepSeconds = s;
                    break;
            }
        }

        return await AgentJobHandlers.Submit(
            channelId,
            new SubmitAgentJobRequest(
                ActionKey: actionKey,
                ResourceId: resourceId,
                AgentId: agentId,
                TranscriptionModelId: modelId,
                Language: language,
                TranscriptionMode: transcriptionMode,
                WindowSeconds: windowSeconds,
                StepSeconds: stepSeconds),
            svc, chatSvc);
    }

    private static async Task<IResult> HandleJobListen(Guid jobId, AgentJobService svc)
    {
        var reader = svc.Subscribe(jobId);
        if (reader is null)
        {
            var job = await svc.GetAsync(jobId);
            if (job is null)
            {
                Console.Error.WriteLine("Job not found.");
            }
            else if (job.ActionKey is null || !job.ActionKey.StartsWith("transcribe_from_audio", StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine("Only transcription jobs support live listening.");
            }
            else if (job.Status is AgentJobStatus.Failed or AgentJobStatus.Cancelled or AgentJobStatus.Denied)
            {
                Console.Error.WriteLine($"Job is {job.Status}.");
                if (!string.IsNullOrWhiteSpace(job.ErrorLog))
                {
                    Console.Error.WriteLine();
                    Console.Error.WriteLine("Error:");
                    Console.Error.WriteLine(job.ErrorLog);
                }
                else if (job.Logs is { Count: > 0 })
                {
                    var lastLog = job.Logs[^1];
                    Console.Error.WriteLine($"  Last log: {lastLog.Message}");
                }
            }
            else if (job.Status is not AgentJobStatus.Executing)
            {
                Console.Error.WriteLine($"Job is {job.Status}. Only executing jobs can be listened to.");
            }
            else
            {
                Console.Error.WriteLine("No active transcription channel for this job.");
                Console.Error.WriteLine("The job may have been started in a previous session. Cancel and resubmit it.");
            }
            return Results.Ok();
        }

        using var cts = new CancellationTokenSource();

        // Suppress Ctrl+C from reaching the host's shutdown handler.
        // Detect it as a regular key press instead.
        var prevCtrlC = Console.TreatControlCAsInput;
        Console.TreatControlCAsInput = true;

        Console.WriteLine("Listening for transcription segments... (Ctrl+C to stop)");
        Console.WriteLine();

        // Poll for Ctrl+C key press on a background thread.
        var keyTask = Task.Run(() =>
        {
            while (!cts.IsCancellationRequested)
            {
                if (Console.KeyAvailable)
                {
                    var key = Console.ReadKey(intercept: true);
                    if (key is { Modifiers: ConsoleModifiers.Control, Key: ConsoleKey.C })
                    {
                        cts.Cancel();
                        return;
                    }
                }

                Thread.Sleep(50);
            }
        });

        try
        {
            await foreach (var segment in reader.ReadAllAsync(cts.Token))
            {
                var confidence = segment.Confidence.HasValue
                    ? $" [{segment.Confidence:P0}]"
                    : "";
                Console.WriteLine($"  [{segment.StartTime:F1}s - {segment.EndTime:F1}s]{confidence} {segment.Text}");
            }

            Console.WriteLine();
            Console.WriteLine("Transcription ended.");
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine();
            Console.WriteLine("Stopped listening.");
        }
        finally
        {
            await cts.CancelAsync();
            await keyTask;
            Console.TreatControlCAsInput = prevCtrlC;
        }

        return Results.Ok();
    }

    private static void PrintUsage(params string[] lines)
    {
        Console.Error.WriteLine("Usage:");
        foreach (var line in lines)
            Console.Error.WriteLine($"  {line}");
    }

    private static IResult UsageResult(params string[] lines)
    {
        foreach (var line in lines)
            Console.Error.WriteLine(line);
        return Results.Ok();
    }

    /// <summary>
    /// Tries to resolve an argument as a short ID or GUID. Returns null if it
    /// doesn't look like either (for optional ID arguments like agent update's modelId).
    /// </summary>
    private static Guid? TryResolveId(string arg)
    {
        try { return CliIdMap.Resolve(arg); }
        catch { return null; }
    }

    private static async Task PrintResultAsync(IResult result)
    {
        switch (result)
        {
            case IValueHttpResult { Value: not null } valueResult:
                var json = SerializeWithShortIds(valueResult.Value);
                Console.WriteLine(json);
                DebugLog($"Response: {json}");
                break;
            case IStatusCodeHttpResult { StatusCode: StatusCodes.Status200OK }:
                DebugLog("Response: 200 OK (no body).");
                break;
            case IStatusCodeHttpResult { StatusCode: StatusCodes.Status401Unauthorized }:
                Console.Error.WriteLine("Unauthorized.");
                DebugLog("Response: 401 Unauthorized.");
                break;
            case IStatusCodeHttpResult { StatusCode: StatusCodes.Status404NotFound }:
                Console.Error.WriteLine("Not found.");
                DebugLog("Response: 404 Not Found.");
                break;
            case IStatusCodeHttpResult { StatusCode: StatusCodes.Status204NoContent }:
                Console.WriteLine("Done.");
                DebugLog("Response: 204 No Content.");
                break;
            default:
            {
                await using var stream = new MemoryStream();
                var httpContext = new DefaultHttpContext { Response = { Body = stream } };
                await result.ExecuteAsync(httpContext);
                stream.Position = 0;
                using var reader = new StreamReader(stream);
                var body = await reader.ReadToEndAsync();
                Console.WriteLine(body);
                DebugLog($"Response ({httpContext.Response.StatusCode}): {body}");
                break;
            }
        }
    }

    /// <summary>
    /// Serializes a value to JSON with short-ID injection, returning the string.
    /// </summary>
    private static string SerializeWithShortIds(object value)
    {
        var doc = JsonSerializer.SerializeToNode(value, JsonPrint);
        if (doc is null) return "null";
        InjectShortIds(doc);
        return doc.ToJsonString(JsonPrint);
    }

    /// <summary>
    /// Serializes a value to JSON, injecting a <c>#</c> short-ID field before each
    /// <c>Id</c> GUID property so the user can reference entities by number.
    /// </summary>
    internal static void PrintJsonWithShortIds(object value)
    {
        var doc = JsonSerializer.SerializeToNode(value, JsonPrint);
        if (doc is null) return;

        InjectShortIds(doc);
        Console.WriteLine(doc.ToJsonString(JsonPrint));
    }

    private static void InjectShortIds(System.Text.Json.Nodes.JsonNode node)
    {
        if (node is System.Text.Json.Nodes.JsonObject obj)
        {
            if (obj.TryGetPropertyValue("Id", out var idNode)
                && idNode is not null
                && Guid.TryParse(idNode.ToString(), out var guid))
            {
                var shortId = CliIdMap.GetOrAssign(guid);
                // Insert # as the first property
                obj.Remove("#");
                var copy = new System.Text.Json.Nodes.JsonObject();
                copy["#"] = shortId;
                foreach (var kvp in obj.ToList())
                {
                    obj.Remove(kvp.Key);
                    copy[kvp.Key] = kvp.Value;
                }
                // Replace contents of obj with copy's contents
                foreach (var kvp in copy.ToList())
                {
                    copy.Remove(kvp.Key);
                    obj[kvp.Key] = kvp.Value;
                }
            }

            foreach (var prop in obj.ToList())
                if (prop.Value is not null)
                    InjectShortIds(prop.Value);
        }
        else if (node is System.Text.Json.Nodes.JsonArray arr)
        {
            foreach (var item in arr)
                if (item is not null)
                    InjectShortIds(item);
        }
    }

    private static async Task<IResult> HandleDeviceCodeLoginAsync(
        Guid providerId, ProviderService svc, CancellationToken ct)
    {
        var session = await svc.StartDeviceCodeFlowAsync(providerId, ct);

        Console.WriteLine();
        Console.WriteLine($"  Go to: {session.VerificationUri}");
        Console.WriteLine($"  Enter code: {session.UserCode}");
        Console.WriteLine();
        Console.WriteLine("  Waiting for authorization...");

        await svc.CompleteDeviceCodeFlowAsync(providerId, session, ct);

        Console.WriteLine("  Successfully authenticated!");
        return Results.Ok();
    }

    private static async Task<IResult> HandleProviderSync(Guid providerId, ProviderService svc)
    {
        var result = await ProviderHandlers.SyncModels(providerId, svc);
        var refreshed = await svc.RefreshCapabilitiesAsync(providerId);
        if (refreshed > 0)
            Console.WriteLine($"(Updated capabilities for {refreshed} model(s))");
        return result;
    }

    private static async Task<IResult> HandleRefreshCaps(Guid providerId, ProviderService svc)
    {
        var updated = await svc.RefreshCapabilitiesAsync(providerId);
        Console.WriteLine(updated > 0
            ? $"Updated capabilities for {updated} model(s)."
            : "All model capabilities are already up to date.");
        return Results.Ok();
    }

    private static async Task<IResult> HandleProviderCost(Guid providerId, int days, IServiceProvider sp)
    {
        var costSvc = sp.GetRequiredService<ProviderCostService>();
        return await ProviderHandlers.GetCost(providerId, costSvc, days);
    }

    private static async Task<IResult> HandleProviderCostTotal(string[] args, IServiceProvider sp)
    {
        var days = ParseDaysFlag(args, 2);
        var simple = args.Skip(2).Any(a => a is "--simple");
        var all = args.Skip(2).Any(a => a is "--all");

        var costSvc = sp.GetRequiredService<ProviderCostService>();

        if (simple)
        {
            var result = await costSvc.GetTotalCostAsync(days, includeAll: all);
            var formatted = ProviderHandlers.FormatSimpleCost(result);
            Console.WriteLine(formatted.Summary);
            return Results.Ok();
        }

        return await ProviderHandlers.GetCostTotal(costSvc, days, all: all);
    }

    private static int ParseDaysFlag(string[] args, int startIndex)
    {
        for (var i = startIndex; i < args.Length - 1; i++)
        {
            if (args[i] is "--days" && int.TryParse(args[i + 1], out var days) && days > 0)
                return days;
        }
        return 30;
    }

    // ═══════════════════════════════════════════════════════════════
    // Task definitions & instances
    // ═══════════════════════════════════════════════════════════════

    private static async Task<IResult?> HandleTaskCommand(string[] args, IServiceProvider sp)
    {
        if (args.Length < 2)
        {
            PrintUsage(
                "task create <sourceFilePath>             Create definition from .cs file",
                "task list                                List all task definitions",
                "task get <id>                            Show a task definition",
                "task update <id> <sourceFilePath>        Update definition source",
                "task activate <id>                       Activate a definition",
                "task deactivate <id>                     Deactivate a definition",
                "task delete <id>                         Delete a task definition",
                "task create-queued <taskId> [channelId] [--param key=value ...]",
                "                                         Create a queued instance without starting",
                "task start <taskId> [channelId] [--param key=value ...]",
                "                                         Start a new instance",
                "task run <taskId> [channelId] [--param key=value ...]",
                "                                         Alias for start",
                "task start-instance <instanceId>         Start an existing queued instance",
                "task instances <taskId>                  List instances of a definition",
                "task instance <instanceId>               Show instance details",
                "task outputs <instanceId> [--since <timestamp>]",
                "                                         Get persisted output history",
                "task cancel <instanceId>                 Cancel a running instance",
                "task stop <instanceId>                   Stop a running instance",
                "task pause <instanceId>                  Pause a running instance",
                "task resume <instanceId>                 Resume a paused instance",
                "task listen <instanceId>                 Stream live task output");
            return Results.Ok();
        }

        var sub = args[1].ToLowerInvariant();
        var svc = sp.GetRequiredService<TaskService>();
        var chatSvc = sp.GetRequiredService<ChatService>();

        return sub switch
        {
            "create" when args.Length >= 3 => await HandleTaskCreate(args[2], svc),
            "create" => UsageResult("task create <sourceFilePath>"),

            "list" => await TaskDefinitionHandlers.List(svc),

            "get" when args.Length >= 3
                => await TaskDefinitionHandlers.GetById(CliIdMap.Resolve(args[2]), svc),
            "get" => UsageResult("task get <id>"),

            "update" when args.Length >= 4
                => await HandleTaskUpdate(CliIdMap.Resolve(args[2]), args[3], svc),
            "update" => UsageResult("task update <id> <sourceFilePath>"),

            "activate" when args.Length >= 3
                => await TaskDefinitionHandlers.Update(
                    CliIdMap.Resolve(args[2]),
                    new UpdateTaskDefinitionRequest(IsActive: true), svc),
            "activate" => UsageResult("task activate <id>"),

            "deactivate" when args.Length >= 3
                => await TaskDefinitionHandlers.Update(
                    CliIdMap.Resolve(args[2]),
                    new UpdateTaskDefinitionRequest(IsActive: false), svc),
            "deactivate" => UsageResult("task deactivate <id>"),

            "delete" when args.Length >= 3
                => await TaskDefinitionHandlers.Delete(CliIdMap.Resolve(args[2]), svc),
            "delete" => UsageResult("task delete <id>"),

            "create-queued" when args.Length >= 3
                => await HandleTaskStart(args, svc, sp, startImmediately: false),
            "create-queued" => UsageResult("task create-queued <taskId> [channelId] [--param key=value ...]"),

            "start" or "run" when args.Length >= 3
                => await HandleTaskStart(args, svc, sp, startImmediately: true),
            "start" => UsageResult("task start <taskId> [channelId] [--param key=value ...]"),
            "run" => UsageResult("task run <taskId> [channelId] [--param key=value ...]"),

            "start-instance" when args.Length >= 3
                => await HandleTaskStartExistingInstance(CliIdMap.Resolve(args[2]), sp),
            "start-instance" => UsageResult("task start-instance <instanceId>"),

            "instances" when args.Length >= 3
                => await TaskInstanceHandlers.List(CliIdMap.Resolve(args[2]), svc),
            "instances" => UsageResult("task instances <taskId>"),

            "instance" when args.Length >= 3
                => await TaskInstanceHandlers.GetById(Guid.Empty, CliIdMap.Resolve(args[2]), svc, chatSvc),
            "instance" => UsageResult("task instance <instanceId>"),

            "outputs" when args.Length >= 3
                => await HandleTaskOutputs(args, svc),
            "outputs" => UsageResult("task outputs <instanceId> [--since <timestamp>]"),

            "cancel" when args.Length >= 3
                => await svc.CancelInstanceAsync(CliIdMap.Resolve(args[2]))
                    ? Results.Ok("Instance cancelled.")
                    : Results.NotFound(),
            "cancel" => UsageResult("task cancel <instanceId>"),

            "stop" when args.Length >= 3
                => await HandleTaskStop(CliIdMap.Resolve(args[2]), sp),
            "stop" => UsageResult("task stop <instanceId>"),

            "pause" when args.Length >= 3
                => await HandleTaskPause(CliIdMap.Resolve(args[2]), sp),
            "pause" => UsageResult("task pause <instanceId>"),

            "resume" when args.Length >= 3
                => await HandleTaskResume(CliIdMap.Resolve(args[2]), sp),
            "resume" => UsageResult("task resume <instanceId>"),

            "listen" when args.Length >= 3
                => await HandleTaskListen(CliIdMap.Resolve(args[2]), sp),
            "listen" => UsageResult("task listen <instanceId>"),

            _ => UsageResult($"Unknown sub-command: task {sub}. Try 'help' for usage.")
        };
    }

    private static async Task<IResult> HandleTaskCreate(string sourceFilePath, TaskService svc)
    {
        if (!File.Exists(sourceFilePath))
        {
            Console.Error.WriteLine($"File not found: {sourceFilePath}");
            return Results.Ok();
        }

        var sourceText = await File.ReadAllTextAsync(sourceFilePath);
        var validation = svc.ValidateDefinition(sourceText);
        if (!validation.IsValid)
        {
            foreach (var diagnostic in validation.Diagnostics)
            {
                var location = diagnostic.Line > 0
                    ? $"[Line {diagnostic.Line}:{diagnostic.Column}] "
                    : string.Empty;
                Console.Error.WriteLine($"{location}{diagnostic.Severity} {diagnostic.Code}: {diagnostic.Message}");
            }

            return Results.BadRequest(validation);
        }

        return await TaskDefinitionHandlers.Create(
            new CreateTaskDefinitionRequest(sourceText), svc);
    }

    private static async Task<IResult> HandleTaskUpdate(
        Guid taskId, string sourceFilePath, TaskService svc)
    {
        if (!File.Exists(sourceFilePath))
        {
            Console.Error.WriteLine($"File not found: {sourceFilePath}");
            return Results.Ok();
        }

        var sourceText = await File.ReadAllTextAsync(sourceFilePath);
        var validation = svc.ValidateDefinition(sourceText);
        if (!validation.IsValid)
        {
            foreach (var diagnostic in validation.Diagnostics)
            {
                var location = diagnostic.Line > 0
                    ? $"[Line {diagnostic.Line}:{diagnostic.Column}] "
                    : string.Empty;
                Console.Error.WriteLine($"{location}{diagnostic.Severity} {diagnostic.Code}: {diagnostic.Message}");
            }

            return Results.BadRequest(validation);
        }

        return await TaskDefinitionHandlers.Update(
            taskId, new UpdateTaskDefinitionRequest(SourceText: sourceText), svc);
    }

    private static async Task<IResult> HandleTaskStart(
        string[] args, TaskService svc, IServiceProvider sp, bool startImmediately)
    {
        var taskId = CliIdMap.Resolve(args[2]);
        Guid? channelId = args.Length > 3 && !args[3].StartsWith("--")
            ? CliIdMap.Resolve(args[3])
            : _currentChannelId;

        var flagStart = channelId is not null && args.Length > 3 && !args[3].StartsWith("--")
            ? 4 : 3;

        Dictionary<string, string>? paramValues = null;
        for (var i = flagStart; i < args.Length; i++)
        {
            if (args[i] is "--param" or "-p" && i + 1 < args.Length)
            {
                var kv = args[++i];
                var eq = kv.IndexOf('=');
                if (eq > 0)
                {
                    paramValues ??= [];
                    paramValues[kv[..eq]] = kv[(eq + 1)..];
                }
            }
        }

        var session = sp.GetRequiredService<SessionService>();
        var orchestrator = sp.GetRequiredService<TaskOrchestrator>();
        var request = new StartTaskInstanceRequest(taskId, channelId, paramValues, startImmediately);
        return await TaskInstanceHandlers.CreateInstance(taskId, request, svc, session, orchestrator);
    }

    private static async Task<IResult> HandleTaskStartExistingInstance(Guid instanceId, IServiceProvider sp)
    {
        var orchestrator = sp.GetRequiredService<TaskOrchestrator>();
        await orchestrator.StartAsync(instanceId);
        Console.WriteLine("Instance started.");
        return Results.Ok();
    }

    private static async Task<IResult> HandleTaskStop(Guid instanceId, IServiceProvider sp)
    {
        var orchestrator = sp.GetRequiredService<TaskOrchestrator>();
        await orchestrator.StopAsync(instanceId);
        Console.WriteLine("Instance stopped.");
        return Results.Ok();
    }

    private static async Task<IResult> HandleTaskPause(Guid instanceId, IServiceProvider sp)
    {
        var orchestrator = sp.GetRequiredService<TaskOrchestrator>();
        if (!await orchestrator.PauseAsync(instanceId))
        {
            Console.Error.WriteLine("Instance could not be paused.");
            return Results.NotFound();
        }

        Console.WriteLine("Instance paused.");
        return Results.Ok();
    }

    private static async Task<IResult> HandleTaskResume(Guid instanceId, IServiceProvider sp)
    {
        var orchestrator = sp.GetRequiredService<TaskOrchestrator>();
        if (!await orchestrator.ResumeAsync(instanceId))
        {
            Console.Error.WriteLine("Instance could not be resumed.");
            return Results.NotFound();
        }

        Console.WriteLine("Instance resumed.");
        return Results.Ok();
    }

    private static async Task<IResult> HandleTaskOutputs(string[] args, TaskService svc)
    {
        var instanceId = CliIdMap.Resolve(args[2]);
        DateTimeOffset? since = null;

        for (var i = 3; i < args.Length; i++)
        {
            if (args[i] is "--since" && i + 1 < args.Length)
            {
                if (DateTimeOffset.TryParse(args[++i], out var ts))
                    since = ts;
                else
                {
                    Console.Error.WriteLine($"Invalid timestamp: {args[i]}");
                    return Results.Ok();
                }
            }
        }

        return await TaskInstanceHandlers.GetOutputs(Guid.Empty, instanceId, svc, since);
    }

    private static async Task<IResult> HandleTaskListen(Guid instanceId, IServiceProvider sp)
    {
        var orchestrator = sp.GetRequiredService<TaskOrchestrator>();
        var reader = orchestrator.GetOutputReader(instanceId);
        if (reader is null)
        {
            Console.Error.WriteLine("No active output stream for this instance.");
            Console.Error.WriteLine("The instance may not be running or was started in a previous session.");
            return Results.Ok();
        }

        using var cts = new CancellationTokenSource();

        var prevCtrlC = Console.TreatControlCAsInput;
        Console.TreatControlCAsInput = true;

        Console.WriteLine("Listening for task output... (Ctrl+C to stop)");
        Console.WriteLine();

        var keyTask = Task.Run(() =>
        {
            while (!cts.IsCancellationRequested)
            {
                if (Console.KeyAvailable)
                {
                    var key = Console.ReadKey(intercept: true);
                    if (key is { Modifiers: ConsoleModifiers.Control, Key: ConsoleKey.C })
                    {
                        cts.Cancel();
                        return;
                    }
                }

                Thread.Sleep(50);
            }
        });

        try
        {
            await foreach (var evt in reader.ReadAllAsync(cts.Token))
            {
                switch (evt.Type)
                {
                    case TaskOutputEventType.Output:
                        Console.WriteLine($"  [output] {evt.Data}");
                        break;
                    case TaskOutputEventType.Log:
                        Console.WriteLine($"  [log] {evt.Data}");
                        break;
                    case TaskOutputEventType.StatusChange:
                        Console.WriteLine($"  [status] {evt.Data}");
                        break;
                    case TaskOutputEventType.Done:
                        Console.WriteLine();
                        Console.WriteLine("Task stream ended.");
                        break;
                }

                if (evt.Type == TaskOutputEventType.Done)
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine();
            Console.WriteLine("Stopped listening.");
        }
        finally
        {
            await cts.CancelAsync();
            await keyTask;
            Console.TreatControlCAsInput = prevCtrlC;
        }

        return Results.Ok();
    }

    // ═══════════════════════════════════════════════════════════════
    // Module (list, get, enable, disable)
    // ═══════════════════════════════════════════════════════════════

    private static async Task<IResult?> HandleModuleCommand(string[] args, IServiceProvider sp)
    {
        if (args.Length < 2)
        {
            PrintUsage(
                "module list                              List all modules",
                "module get <id>                          Show module details",
                "module enable <id>                       Enable a module at runtime",
                "module disable <id>                      Disable a module at runtime",
                "module scan                              Scan & load external modules",
                "module reload <id>                       Reload an external module",
                "module unload <id>                       Unload an external module");
            return Results.Ok();
        }

        var sub = args[1].ToLowerInvariant();
        var svc = sp.GetRequiredService<ModuleService>();

        return sub switch
        {
            "list" => await ModuleHandlers.List(svc),

            "get" when args.Length >= 3
                => await ModuleHandlers.Get(args[2], svc),
            "get" => UsageResult("module get <id>"),

            "enable" when args.Length >= 3
                => await ModuleHandlers.Enable(args[2], svc, sp.GetRequiredService<ModuleLoader>()),
            "enable" => UsageResult("module enable <id>"),

            "disable" when args.Length >= 3
                => await ModuleHandlers.Disable(args[2], svc),
            "disable" => UsageResult("module disable <id>"),

            "scan" => await ModuleHandlers.Scan(svc, sp.GetRequiredService<ModuleLoader>()),

            "reload" when args.Length >= 3
                => await ModuleHandlers.Reload(args[2], svc, sp.GetRequiredService<ModuleLoader>()),
            "reload" => UsageResult("module reload <id>"),

            "unload" when args.Length >= 3
                => await ModuleHandlers.Unload(args[2], svc),
            "unload" => UsageResult("module unload <id>"),

            _ => UsageResult($"Unknown sub-command: module {sub}. Try 'help' for usage.")
        };
    }

    // ═══════════════════════════════════════════════════════════════
    // Resource (unified: container, displaydevice, ...)
    // ═══════════════════════════════════════════════════════════════

    private static async Task<IResult?> HandleResourceCommand(string[] args, IServiceProvider sp)
    {
        if (args.Length < 2)
        {
            PrintUsage(
                "resource <type> <command> [args...]",
                "",
                "All resource types are module-provided.",
                "See 'Module Commands' in help for available types.",
                "",
                "Commands (all types):",
                "  add      Create a new resource",
                "  get      Show a resource by ID",
                "  list     List all resources of this type",
                "  update   Update a resource by ID",
                "  delete   Delete a resource by ID",
                "  sync     Import from system / local registry");
            return Results.Ok();
        }

        var type = args[1].ToLowerInvariant();
        return await TryModuleResourceCommandAsync(type, args, sp)
            ?? UsageResult($"Unknown resource type: {type}. Type 'help' for available types.");
    }

    // ═══════════════════════════════════════════════════════════════
    // User administration
    // ═══════════════════════════════════════════════════════════════

    private static async Task<IResult?> HandleUserCommand(string[] args, IServiceProvider sp)
    {
        if (args.Length < 2)
        {
            PrintUsage(
                "user list                                 List all users (admin only)",
                "user role <userId> <roleId|none>           Assign a role to a user (admin only)");
            return Results.Ok();
        }

        var sub = args[1].ToLowerInvariant();
        var auth = sp.GetRequiredService<AuthService>();
        var session = sp.GetRequiredService<SessionService>();

        return sub switch
        {
            "list" => await HandleUserList(auth, session),

            "role" when args.Length >= 4
                => await HandleUserRole(args, auth, session),
            "role" => UsageResult("user role <userId> <roleId|none>"),

            _ => UsageResult($"Unknown sub-command: user {sub}. Try 'help' for usage.")
        };
    }

    private static async Task<IResult> HandleUserList(AuthService auth, SessionService session)
    {
        if (session.UserId is not { } userId)
            return Results.Unauthorized();

        try
        {
            var users = await auth.ListUsersAsync(userId);
            PrintJsonWithShortIds(users);
            return Results.Ok();
        }
        catch (UnauthorizedAccessException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return Results.Ok();
        }
    }

    private static async Task<IResult> HandleUserRole(
        string[] args, AuthService auth, SessionService session)
    {
        if (session.UserId is not { } callerId)
            return Results.Unauthorized();

        var targetUserId = CliIdMap.Resolve(args[2]);
        var roleId = args[3].Equals("none", StringComparison.OrdinalIgnoreCase)
            ? Guid.Empty
            : CliIdMap.Resolve(args[3]);

        try
        {
            var result = await auth.SetUserRoleAsync(targetUserId, roleId, callerId);
            if (result is null)
            {
                Console.Error.WriteLine("User not found.");
                return Results.Ok();
            }
            PrintJsonWithShortIds(result);
            return Results.Ok();
        }
        catch (UnauthorizedAccessException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return Results.Ok();
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return Results.Ok();
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // Bio
    // ═══════════════════════════════════════════════════════════════

    private static async Task<IResult?> HandleBioCommand(string[] args, IServiceProvider sp)
    {
        if (args.Length < 2)
        {
            PrintUsage(
                "bio get                                   Show your bio",
                "bio set <text>                            Set your bio",
                "bio clear                                 Remove your bio");
            return Results.Ok();
        }

        var sub = args[1].ToLowerInvariant();
        var db = sp.GetRequiredService<SharpClawDbContext>();

        return sub switch
        {
            "get" => await HandleBioGet(db),
            "set" when args.Length >= 3 => await HandleBioSet(db, string.Join(' ', args[2..])),
            "set" => UsageResult("bio set <text>"),
            "clear" => await HandleBioSet(db, null),
            _ => UsageResult($"Unknown sub-command: bio {sub}. Try 'bio get', 'bio set', or 'bio clear'.")
        };
    }

    private static async Task<IResult> HandleBioGet(SharpClawDbContext db)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == _currentUserId);
        if (user is null) return Results.NotFound();

        Console.WriteLine(string.IsNullOrEmpty(user.Bio)
            ? "(no bio set)"
            : user.Bio);
        return Results.Ok();
    }

    private static async Task<IResult> HandleBioSet(SharpClawDbContext db, string? bio)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == _currentUserId);
        if (user is null) return Results.NotFound();

        user.Bio = bio;
        await db.SaveChangesAsync();

        Console.WriteLine(bio is not null ? $"Bio updated." : "Bio cleared.");
        return Results.Ok();
    }

    private static async Task<IResult?> HandleToolAwarenessSetCommand(string[] args, IServiceProvider sp)
    {
        if (args.Length < 2)
        {
            PrintUsage(
                "tools add <name> [json]                   Create a tool awareness set",
                "tools list                                List all tool awareness sets",
                "tools get <id>                            Show a tool awareness set",
                "tools update <id> [--name <n>] [json]     Update a tool awareness set",
                "tools delete <id>                         Delete a tool awareness set",
                "",
                "  json: '{\"tool_name\": true, ...}' — tools not listed default to enabled.");
            return Results.Ok();
        }

        var svc = sp.GetRequiredService<ToolAwarenessSetService>();
        var sub = args[1].ToLowerInvariant();

        return sub switch
        {
            "add" when args.Length >= 3 => await HandleToolAwarenessSetAdd(args, svc),
            "add" => UsageResult("tools add <name> [json]"),

            "list" => await ToolAwarenessSetHandlers.List(svc),

            "get" when args.Length >= 3
                => await ToolAwarenessSetHandlers.GetById(CliIdMap.Resolve(args[2]), svc),
            "get" => UsageResult("tools get <id>"),

            "update" when args.Length >= 3
                => await HandleToolAwarenessSetUpdate(args, svc),
            "update" => UsageResult("tools update <id> [--name <n>] [json]"),

            "delete" when args.Length >= 3
                => await ToolAwarenessSetHandlers.Delete(CliIdMap.Resolve(args[2]), svc),
            "delete" => UsageResult("tools delete <id>"),

            _ => UsageResult($"Unknown sub-command: tools {sub}. Try 'tools add', 'tools list', etc.")
        };
    }

    private static async Task<IResult> HandleToolAwarenessSetAdd(string[] args, ToolAwarenessSetService svc)
    {
        var name = args[2];
        Dictionary<string, bool>? tools = null;
        if (args.Length >= 4)
        {
            try { tools = JsonSerializer.Deserialize<Dictionary<string, bool>>(args[3]); }
            catch (JsonException ex)
            {
                Console.Error.WriteLine($"Invalid JSON: {ex.Message}");
                return Results.BadRequest("Invalid tools JSON.");
            }
        }

        return await ToolAwarenessSetHandlers.Create(new CreateToolAwarenessSetRequest(name, tools), svc);
    }

    private static async Task<IResult> HandleToolAwarenessSetUpdate(string[] args, ToolAwarenessSetService svc)
    {
        var id = CliIdMap.Resolve(args[2]);
        string? name = null;
        Dictionary<string, bool>? tools = null;

        for (var i = 3; i < args.Length; i++)
        {
            if (args[i] is "--name" && i + 1 < args.Length)
            {
                name = args[++i];
            }
            else
            {
                try { tools = JsonSerializer.Deserialize<Dictionary<string, bool>>(args[i]); }
                catch (JsonException ex)
                {
                    Console.Error.WriteLine($"Invalid JSON: {ex.Message}");
                    return Results.BadRequest("Invalid tools JSON.");
                }
            }
        }

        return await ToolAwarenessSetHandlers.Update(id, new UpdateToolAwarenessSetRequest(name, tools), svc);
    }

    /// <summary>
    /// Tries to dispatch a resource sub-command to a module-provided handler.
    /// Returns null if no module registered the resource type.
    /// </summary>
    private static async Task<IResult?> TryModuleResourceCommandAsync(
        string type, string[] args, IServiceProvider sp)
    {
        var registry = sp.GetRequiredService<ModuleRegistry>();
        var moduleCmd = registry.TryResolveResourceTypeCommand(type);
        if (moduleCmd is null) return null;

        await moduleCmd.Handler(args, sp, CancellationToken.None);
        return Results.Ok();
    }

    // ═══════════════════════════════════════════════════════════════
    // env
    // ═══════════════════════════════════════════════════════════════

    private static async Task<IResult?> HandleEnvCommand(string[] args, IServiceProvider sp)
    {
        if (args.Length < 2)
        {
            PrintUsage(
                "env get                                   Read core .env content",
                "env set                                   Write core .env (reads from stdin until blank line)",
                "env auth                                  Check env edit authorisation",
                "env status                                Show encryption status of .env file",
                "env unlock                                Decrypt .env file in-place (re-locks on next startup)");
            return Results.Ok();
        }

        var sub = args[1].ToLowerInvariant();
        var svc = sp.GetRequiredService<EnvFileService>();

        return sub switch
        {
            "get" => await EnvHandlers.Read(svc),
            "set" => await HandleEnvSet(svc),
            "auth" => await EnvHandlers.CheckAuth(svc),
            "status" => HandleEnvStatus(),
            "unlock" => await HandleEnvUnlockAsync(),
            _ => UsageResult($"Unknown sub-command: env {sub}. Try 'env get', 'env set', 'env auth', 'env status', or 'env unlock'.")
        };
    }

    private static async Task<IResult> HandleEnvSet(EnvFileService svc)
    {
        Console.WriteLine("Paste .env JSON content (blank line to finish):");
        var lines = new List<string>();
        while (true)
        {
            var line = Console.ReadLine();
            if (string.IsNullOrEmpty(line))
                break;
            lines.Add(line);
        }

        var content = string.Join(Environment.NewLine, lines);
        return await EnvHandlers.Write(new EnvWriteRequest(content), svc);
    }

    private static IResult HandleEnvStatus()
    {
        var path = Path.Combine(
            Path.GetDirectoryName(typeof(CliDispatcher).Assembly.Location)!,
            "Environment", ".env");

        if (!File.Exists(path))
        {
            Console.WriteLine("Core .env file not found.");
            return Results.Ok();
        }

        var encrypted = EncryptedEnvFile.IsEncryptedOnDisk(path);
        Console.WriteLine(encrypted ? "Core .env is encrypted (AES-GCM)." : "Core .env is plaintext.");
        return Results.Ok();
    }

    private static async Task<IResult> HandleEnvUnlockAsync()
    {
        var path = Path.Combine(
            Path.GetDirectoryName(typeof(CliDispatcher).Assembly.Location)!,
            "Environment", ".env");

        if (!File.Exists(path))
        {
            Console.WriteLine("Core .env file not found.");
            return Results.Ok();
        }

        if (!EncryptedEnvFile.IsEncryptedOnDisk(path))
        {
            Console.WriteLine("Core .env is already plaintext.");
            return Results.Ok();
        }

        var key = EncryptionKeyResolver.ResolveKey();
        if (key is null)
        {
            Console.Error.WriteLine("Cannot resolve encryption key.");
            return Results.BadRequest("No encryption key available.");
        }

        var json = await EncryptedEnvFile.ReadAsync(path, key);
        await EncryptedEnvFile.WriteAsync(path, json, key, encrypt: false);
        Console.WriteLine("Core .env decrypted in-place. It will be re-encrypted on the next app startup.");
        return Results.Ok();
    }

    // ═══════════════════════════════════════════════════════════════
    // db (database / migration management)
    // ═══════════════════════════════════════════════════════════════

    private static async Task<IResult?> HandleDbCommand(string[] args, IServiceProvider sp)
    {
        if (args.Length < 2)
        {
            PrintUsage(
                "db status                                 Show migration gate state + applied/pending migrations",
                "db migrate                                Drain requests and apply pending EF Core migrations");
            return Results.Ok();
        }

        var migrationSvc = sp.GetService<MigrationService>();
        if (migrationSvc is null)
        {
            Console.Error.WriteLine("Migration management is only available for relational database providers.");
            return Results.BadRequest("Not a relational provider.");
        }

        return args[1].ToLowerInvariant() switch
        {
            "status" => await HandleDbStatus(migrationSvc),
            "migrate" => await HandleDbMigrate(migrationSvc),
            _ => null
        };
    }

    private static async Task<IResult> HandleDbStatus(MigrationService svc)
    {
        var status = await svc.GetStatusAsync();
        Console.WriteLine($"Gate state : {status.State}");
        Console.WriteLine($"Applied    : {status.Applied.Count}");
        if (status.Applied.Count > 0)
            foreach (var m in status.Applied) Console.WriteLine($"  ✓ {m}");
        Console.WriteLine($"Pending    : {status.Pending.Count}");
        if (status.Pending.Count > 0)
            foreach (var m in status.Pending) Console.WriteLine($"  ⏳ {m}");
        return Results.Ok();
    }

    private static async Task<IResult> HandleDbMigrate(MigrationService svc)
    {
        Console.WriteLine("Draining in-flight requests and applying migrations...");
        var result = await svc.MigrateAsync();

        if (result.AlreadyInProgress)
        {
            Console.Error.WriteLine(result.Message);
            return Results.Conflict(new { message = result.Message });
        }

        Console.WriteLine(result.Message);
        foreach (var m in result.Migrations) Console.WriteLine($"  ✓ {m}");
        return Results.Ok();
    }

    private static IResult PrintHelp(IServiceProvider? sp = null)
    {
        Console.WriteLine("""
            SharpClaw - Shell Agent

            IDs: full GUIDs or short #numbers from output (session-only).
            Most entities support: add, get, list, update, delete.

            Auth:
              register <user> <pass>          login <user> <pass>          logout
              me                               Show current user profile & role

            System:
              health                           Show persistence health status

            Provider:  provider <sub> [args]    (add, get, list, update, delete)
              provider add <name> <type> [endpoint]
                Types: OpenAI, Anthropic, OpenRouter, GoogleVertexAI, GoogleGemini,
                  ZAI, VercelAIGateway, XAI, Groq, Cerebras, Mistral, GitHubCopilot, Custom
              provider set-key <id> <apiKey>   login <id>   sync-models <id>   refresh-caps <id>

            Model:     model <sub> [args]       (add, get, list, update, delete)
              Prefer 'provider sync-models <id>' to auto-import models.
              model add <name> <providerId> [--cap Chat,Transcription,...]
                <name> must be the exact provider model ID (e.g. gpt-4o).
              Local models:
              model download <url> [--name <alias>] [--quant <Q4_K_M>] [--gpu-layers <n>] [--provider <LlamaSharp|Whisper>]
              model download list <url>          List available GGUF files at a URL
                Omit --provider to register with all local providers (LlamaSharp + Whisper).
                Specify --provider to target one. --gpu-layers has no effect for Whisper.
              model load <id> [--gpu-layers <n>] [--ctx <size>]    Pin (keep loaded)
              model unload <id>                                    Unpin
              model local list                   List downloaded local models
              Models auto-load on chat and auto-unload when idle.
              Use load/unload to keep frequently-used models resident.

            Agent:     agent <sub> [args]       (add, get, list, update, delete)
              agent add <name> <modelId> [system prompt] [--tools <setId>]
              agent update <id> <name> [--tools <setId>]
              agent role <id> <roleId|none>

            Role:      role <sub> [args]
              role list                          role permissions <id>
              role permissions <id> set [--create-sub-agents] [--safe-shell all:Independent] ...

            Context:   context|ctx <sub> [args] (add, get, list, update, delete)
              context add <agentId> [name]
              context agents <id> [add|remove <agentId>]
              context defaults <id> [set <key> <resId> | clear <key>]

            Channel:   channel|chan <sub> [args] (add, get, list, select, delete)
              channel add [--agent <id>] [--context <id>] [--tools <setId>] [title]
              channel attach|detach <id> [contextId]
              channel agents <id> [add|remove <agentId>]
              channel defaults <id> [set <key> <resId> | clear <key>]
              Fields cascade from context when not set: agent, permissions,
                DisableChatHeader, AllowedAgents, DefaultResourceSet.
              Default-resource keys: safeshell, dangshell, container, website,
                search, localinfo, externalinfo, inputaudio, agent, task,
                skill, transcriptionmodel

            Chat:
              chat [--agent <id>] [--thread <id>] <message>
                Send a message in the active channel.
                Without --thread, no history is sent (one-shot).
                With --thread, conversation history is included.
              chat toggle                      Toggle chat mode (all input → chat)
                In chat mode: !exit or !chat toggle to return to normal mode.

            Thread:    thread <sub> [args]
              thread add [channelId] [name] [--max-messages <n>] [--max-chars <n>]
              thread list [channelId]                      List threads
              thread get <id>                              Show thread details
              thread update <id> [--name <n>] [--max-messages <n>] [--max-chars <n>]
              thread select <id>                           Select active thread for chat
              thread deselect                              Deselect active thread
              thread delete <id>                           Delete a thread
              Defaults: 50 messages, 100k chars. Set 0 to reset to default.

            Bio:       bio get | set <text> | clear

            Env:       env <sub>                 (get, set, auth, status, unlock)
              env get                            Read core .env content
              env set                            Write core .env (stdin input)
              env auth                           Check env edit authorisation
              env status                         Show .env encryption status
              env unlock                         Decrypt .env in-place (re-locks on startup)

            Database:  db <sub>                  (relational providers only)
              db status                          Show migration gate state + applied/pending
              db migrate                         Drain requests and apply pending migrations

            User:      user <sub> [args]        (admin only)
              user list                          List all registered users
              user role <userId> <roleId|none>   Assign or remove a user's role

            Job:       job <sub> [args]
              job submit <channelId> <actionKey> [resourceId] [--agent <id>]
                  [--model <id>] [--lang <code>]
              job list [channelId]   status <id>   approve <id>   cancel <id>
              job stop <id>          listen <id>   (transcription jobs)

            Task:      task <sub> [args]
              task create <sourceFilePath>       Create definition from .cs file
              task list                          List all task definitions
              task get <id>                      Show definition details
              task update <id> <sourceFilePath>  Update definition source
              task activate <id>                 Activate / deactivate <id>
              task delete <id>                   Delete a task definition
              task start <taskId> [channelId] [--param key=value ...]
              task instances <taskId>            List instances of a definition
              task instance <instanceId>         Show instance details
              task cancel <instanceId>           Cancel a running instance
              task stop <instanceId>             Stop a running instance
              task listen <instanceId>           Stream live task output

            Resource:  resource <type> <sub>    (add, get, list, update, delete, sync)
              All resource types are module-provided.
              See Module Commands below for available types.

            Tools:     tools <sub> [args]       (add, get, list, update, delete)
              tools add <name> [json]            Create a tool awareness set
              tools list                         List all sets
              tools get <id>                     Show set details
              tools update <id> [--name <n>] [json]  Update a set
              tools delete <id>                  Delete a set
              json: '{"tool_name": true, ...}' — omitted tools default to enabled.
              Assign to agents/channels via --tools <setId>.
              Override chain: channel → agent → null (all enabled).

            Module:    module <sub>              (list, get, enable, disable)
              module list                        List all bundled modules
              module get <id>                    Show module details
              module enable <id>                 Enable a module at runtime
              module disable <id>                Disable a module at runtime

              exit / quit
            """);

        // Append module-provided CLI commands dynamically.
        if (sp is not null)
        {
            var registry = sp.GetService<ModuleRegistry>();
            var commands = registry?.GetAllCliCommands() ?? [];
            if (commands.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine("            Module Commands:");
                foreach (var (moduleId, cmd) in commands)
                {
                    var prefix = cmd.Scope == ModuleCliScope.ResourceType ? "resource " : "";
                    Console.WriteLine(
                        $"              {prefix}{cmd.Name,-28} {cmd.Description}  [{moduleId}]");
                    foreach (var alias in cmd.Aliases)
                        Console.WriteLine(
                            $"              {prefix}{alias,-28} (alias)");
                }
            }
        }

        return Results.Ok();
    }

    private static async Task<IResult> HandleHealthCommand(IServiceProvider sp)
    {
        var healthCheck = sp.GetRequiredService<SharpClaw.Infrastructure.Persistence.JSON.JsonPersistenceHealthCheck>();
        var result = await healthCheck.CheckAsync();

        Console.WriteLine($"Status: {result.Status}");
        Console.WriteLine();
        foreach (var entry in result.Entries)
        {
            var icon = entry.Status switch
            {
                SharpClaw.Infrastructure.Persistence.JSON.HealthStatus.Healthy => "✓",
                SharpClaw.Infrastructure.Persistence.JSON.HealthStatus.Degraded => "⚠",
                _ => "✗"
            };
            Console.WriteLine($"  {icon} {entry.Name,-26} {entry.Description}");
        }

        return Results.Ok();
    }
}
