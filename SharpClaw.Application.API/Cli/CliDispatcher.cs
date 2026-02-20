using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Application.API.Handlers;
using SharpClaw.Application.Core.Clients;
using SharpClaw.Application.Services;
using SharpClaw.Application.Services.Auth;
using SharpClaw.Contracts.DTOs.AgentActions;
using SharpClaw.Contracts.DTOs.Agents;
using SharpClaw.Contracts.DTOs.Auth;
using SharpClaw.Contracts.DTOs.Chat;
using SharpClaw.Contracts.DTOs.Contexts;
using SharpClaw.Contracts.DTOs.Conversations;
using SharpClaw.Contracts.DTOs.Models;
using SharpClaw.Contracts.DTOs.Providers;
using SharpClaw.Contracts.DTOs.Transcription;
using SharpClaw.Contracts.Enums;
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
    private static Guid? _currentConversationId;
    private static bool IsLoggedIn => _currentUser is not null;

    [Conditional("DEBUG")]
    private static void DebugLog(string message) => Debug.WriteLine(message, "SharpClaw.CLI");

    private static readonly HashSet<string> PublicCommands =
        ["login", "register", "help", "--help", "-h"];

    /// <summary>
    /// Runs an interactive REPL alongside the API server.
    /// </summary>
    public static async Task RunInteractiveAsync(IServiceProvider services, CancellationToken ct)
    {
        Console.WriteLine();
        Console.WriteLine("Type 'help' for available commands, 'exit' to quit.");
        Console.WriteLine("Log in with: login <username> <password>");
        Console.WriteLine();

        while (!ct.IsCancellationRequested)
        {
            var prompt = IsLoggedIn ? $"sharpclaw ({_currentUser})> " : "sharpclaw> ";
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
                _currentConversationId = null;
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

        var command = args[0].ToLowerInvariant();

        IResult? result = command switch
        {
            "register" => await HandleRegisterCommand(args, sp),
            "login" => await HandleLoginCommand(args, sp),
            "provider" => await HandleProviderCommand(args, sp),
            "model" => await HandleModelCommand(args, sp),
            "agent" => await HandleAgentCommand(args, sp),
            "context" or "ctx" => await HandleContextCommand(args, sp),
            "conversation" or "conv" => await HandleConversationCommand(args, sp),
            "chat" => await HandleChatCommand(args, sp),
            "job" => await HandleJobCommand(args, sp),
            "role" => await HandleRoleCommand(args, sp),
            "audiodevice" or "ad" => await HandleAudioDeviceCommand(args, sp),
            "help" or "--help" or "-h" => PrintHelp(),
            _ => null
        };

        if (result is null)
            return false;

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
                "provider sync <providerId>");
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
                    "Types: OpenAI, Anthropic, OpenRouter, GoogleVertexAI, GoogleGemini,",
                    "       ZAI, VercelAIGateway, XAI, Groq, Cerebras, Mistral, GitHubCopilot, Custom"),
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

            "sync" when args.Length >= 3
                => await HandleProviderSync(CliIdMap.Resolve(args[2]), svc),
            "sync" => UsageResult("provider sync <id>"),

            "refresh-caps" when args.Length >= 3
                => await HandleRefreshCaps(CliIdMap.Resolve(args[2]), svc),
            "refresh-caps" => UsageResult("provider refresh-caps <id>"),

            _ => UsageResult($"Unknown sub-command: provider {sub}. Try 'help' for usage.")
        };
    }

    private static async Task<IResult?> HandleModelCommand(string[] args, IServiceProvider sp)
    {
        if (args.Length < 2)
        {
            PrintUsage(
                "model add <name> <providerId> [--cap <capabilities>]",
                "  Capabilities (comma-separated): Chat, Transcription,",
                "    ImageGeneration, Embedding, TextToSpeech",
                "model get <id>",
                "model list",
                "model update <id> <name> [--cap <capabilities>]",
                "model delete <id>");
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
            "add" => UsageResult("model add <name> <providerId> [--cap Chat,Transcription]"),

            "get" when args.Length >= 3
                => await ModelHandlers.GetById(CliIdMap.Resolve(args[2]), svc),
            "get" => UsageResult("model get <id>"),

            "list" => await ModelHandlers.List(svc),

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

            _ => UsageResult($"Unknown sub-command: model {sub}. Try 'help' for usage.")
        };
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
                "agent add <name> <modelId>  [system prompt]",
                "agent get <id>",
                "agent list",
                "agent update <id> <name> [modelId] [system prompt]",
                "agent role <id> <roleId>                  Assign a role (use 'role list')",
                "agent role <id> none                      Remove role",
                "agent delete <id>");
            return Results.Ok();
        }

        var sub = args[1].ToLowerInvariant();
        var svc = sp.GetRequiredService<AgentService>();

        return sub switch
        {
            "add" when args.Length >= 4
                => await AgentHandlers.Create(
                    new CreateAgentRequest(args[2], CliIdMap.Resolve(args[3]),
                        args.Length >= 5 ? string.Join(' ', args[4..]) : null),
                    svc),
            "add" => UsageResult("agent add <name> <modelId> [system prompt]"),

            "get" when args.Length >= 3
                => await AgentHandlers.GetById(CliIdMap.Resolve(args[2]), svc),
            "get" => UsageResult("agent get <id>"),

            "list" => await AgentHandlers.List(svc),

            "update" when args.Length >= 4
                => await AgentHandlers.Update(
                    CliIdMap.Resolve(args[2]),
                    new UpdateAgentRequest(
                        args[3],
                        args.Length >= 5 ? TryResolveId(args[4]) : null,
                        args.Length >= 6 ? string.Join(' ', args[5..]) : null),
                    svc),
            "update" => UsageResult("agent update <id> <name> [modelId] [system prompt]"),

            "role" when args.Length >= 4 && args[3].Equals("none", StringComparison.OrdinalIgnoreCase)
                => await HandleAgentRoleAssign(CliIdMap.Resolve(args[2]), Guid.Empty, svc),
            "role" when args.Length >= 4
                => await HandleAgentRoleAssign(CliIdMap.Resolve(args[2]), CliIdMap.Resolve(args[3]), svc),
            "role" => UsageResult("agent role <agentId> <roleId>  (use 'role list' to find IDs)"),

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
            var result = await svc.AssignRoleAsync(agentId, roleId, _currentUserId);
            return result is not null ? Results.Ok(result) : Results.NotFound();
        }
        catch (UnauthorizedAccessException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return Results.Unauthorized();
        }
    }

    private static async Task<IResult?> HandleContextCommand(string[] args, IServiceProvider sp)
    {
        if (args.Length < 2)
        {
            PrintUsage(
                "context new <agentId> [name]               Create a context",
                "context list [agentId]                     List contexts",
                "context get <id>                           Show context details",
                "context update <id> <name>                 Rename a context",
                "context delete <id>                        Delete a context");
            return Results.Ok();
        }

        var sub = args[1].ToLowerInvariant();
        var svc = sp.GetRequiredService<ContextService>();

        return sub switch
        {
            "new" when args.Length >= 3
                => await ContextHandlers.Create(
                    new CreateContextRequest(
                        CliIdMap.Resolve(args[2]),
                        args.Length >= 4 ? string.Join(' ', args[3..]) : null),
                    svc),
            "new" => UsageResult("context new <agentId> [name]"),

            "list"
                => await ContextHandlers.List(svc,
                    args.Length >= 3 ? CliIdMap.Resolve(args[2]) : null),

            "get" when args.Length >= 3
                => await ContextHandlers.GetById(CliIdMap.Resolve(args[2]), svc),
            "get" => UsageResult("context get <id>"),

            "update" when args.Length >= 4
                => await ContextHandlers.Update(
                    CliIdMap.Resolve(args[2]),
                    new UpdateContextRequest(string.Join(' ', args[3..])),
                    svc),
            "update" => UsageResult("context update <id> <name>"),

            "delete" when args.Length >= 3
                => await ContextHandlers.Delete(CliIdMap.Resolve(args[2]), svc),
            "delete" => UsageResult("context delete <id>"),

            _ => UsageResult($"Unknown sub-command: context {sub}. Try 'help' for usage.")
        };
    }

    private static async Task<IResult?> HandleChatCommand(string[] args, IServiceProvider sp)
    {
        if (args.Length < 2)
        {
            PrintUsage("chat <message>");
            return Results.Ok();
        }

        var convSvc = sp.GetRequiredService<ConversationService>();

        // Auto-select latest conversation if none is selected
        if (_currentConversationId is null)
        {
            var latest = await convSvc.GetLatestActiveAsync();
            if (latest is null)
            {
                Console.Error.WriteLine("Error: No conversation selected and no conversations exist.");
                Console.Error.WriteLine("Create one first: conversation new <agentId> [title]");
                return Results.Ok();
            }

            _currentConversationId = latest.Id;
            Console.WriteLine($"No conversation selected. Opening latest conversation: \"{latest.Title}\" (#{CliIdMap.GetOrAssign(latest.Id)})");
        }

        return await ChatHandlers.Send(
            _currentConversationId.Value,
            new ChatRequest(string.Join(' ', args[1..])),
            sp.GetRequiredService<ChatService>());
    }

    private static async Task<IResult?> HandleConversationCommand(string[] args, IServiceProvider sp)
    {
        if (args.Length < 2)
        {
            PrintUsage(
                "conversation new <agentId> [--context <ctxId>] [title]",
                "                                           Create a conversation",
                "conversation list [agentId]                List conversations",
                "conversation select <id>                   Select active conversation",
                "conversation get <id>                      Show conversation details",
                "conversation model <id> <modelId>          Change conversation model",
                "conversation attach <id> <contextId>       Attach to a context",
                "conversation detach <id>                   Detach from context",
                "conversation delete <id>                   Delete a conversation");
            return Results.Ok();
        }

        var sub = args[1].ToLowerInvariant();
        var svc = sp.GetRequiredService<ConversationService>();

        return sub switch
        {
            "new" when args.Length >= 3
                => await HandleConversationNew(args, svc),
            "new" => UsageResult("conversation new <agentId> [--context <ctxId>] [title]"),

            "list" => await HandleConversationList(args, svc),

            "select" when args.Length >= 3
                => HandleConversationSelect(args),
            "select" => UsageResult("conversation select <id>"),

            "get" when args.Length >= 3
                => await ConversationHandlers.GetById(CliIdMap.Resolve(args[2]), svc),
            "get" => UsageResult("conversation get <id>"),

            "model" when args.Length >= 4
                => await ConversationHandlers.Update(
                    CliIdMap.Resolve(args[2]),
                    new UpdateConversationRequest(ModelId: CliIdMap.Resolve(args[3])),
                    svc),
            "model" => UsageResult("conversation model <conversationId> <modelId>"),

            "attach" when args.Length >= 4
                => await ConversationHandlers.Update(
                    CliIdMap.Resolve(args[2]),
                    new UpdateConversationRequest(ContextId: CliIdMap.Resolve(args[3])),
                    svc),
            "attach" => UsageResult("conversation attach <conversationId> <contextId>"),

            "detach" when args.Length >= 3
                => await ConversationHandlers.Update(
                    CliIdMap.Resolve(args[2]),
                    new UpdateConversationRequest(ContextId: Guid.Empty),
                    svc),
            "detach" => UsageResult("conversation detach <conversationId>"),

            "delete" when args.Length >= 3
                => await HandleConversationDelete(args, svc),
            "delete" => UsageResult("conversation delete <id>"),

            _ => UsageResult($"Unknown sub-command: conversation {sub}. Try 'help' for usage.")
        };
    }

    private static async Task<IResult> HandleConversationNew(string[] args, ConversationService svc)
    {
        var agentId = CliIdMap.Resolve(args[2]);
        Guid? contextId = null;
        var titleParts = new List<string>();

        // Parse remaining args: [--context <ctxId>] [title...]
        for (var i = 3; i < args.Length; i++)
        {
            if (args[i] is "--context" or "-c" && i + 1 < args.Length)
            {
                contextId = CliIdMap.Resolve(args[++i]);
            }
            else
            {
                titleParts.Add(args[i]);
            }
        }

        var title = titleParts.Count > 0 ? string.Join(' ', titleParts) : null;

        var result = await ConversationHandlers.Create(
            new CreateConversationRequest(agentId, title, ContextId: contextId), svc);

        // Auto-select the newly created conversation
        if (result is IValueHttpResult { Value: ConversationResponse conv })
            _currentConversationId = conv.Id;

        return result;
    }

    private static async Task<IResult> HandleConversationList(string[] args, ConversationService svc)
    {
        Guid? agentId = args.Length >= 3 ? CliIdMap.Resolve(args[2]) : null;
        return await ConversationHandlers.List(svc, agentId);
    }

    private static IResult HandleConversationSelect(string[] args)
    {
        _currentConversationId = CliIdMap.Resolve(args[2]);
        Console.WriteLine($"Conversation #{args[2]} selected.");
        return Results.Ok();
    }

    private static async Task<IResult> HandleConversationDelete(string[] args, ConversationService svc)
    {
        var id = CliIdMap.Resolve(args[2]);
        var result = await ConversationHandlers.Delete(id, svc);

        // Clear selection if the deleted conversation was the active one
        if (_currentConversationId == id)
            _currentConversationId = null;

        return result;
    }

    private static async Task<IResult?> HandleRoleCommand(string[] args, IServiceProvider sp)
    {
        if (args.Length < 2)
        {
            PrintUsage("role list                                 List all roles");
            return Results.Ok();
        }

        var sub = args[1].ToLowerInvariant();

        if (sub == "list")
        {
            var db = sp.GetRequiredService<SharpClawDbContext>();
            var roles = await db.Roles.OrderBy(r => r.Name).ToListAsync();
            var result = roles.Select(r => new { r.Id, r.Name }).ToList();
            PrintJsonWithShortIds(result);
            return Results.Ok();
        }

        return UsageResult($"Unknown sub-command: role {sub}. Try 'role list'.");
    }

    private static async Task<IResult?> HandleJobCommand(string[] args, IServiceProvider sp)
    {
        if (args.Length < 2)
        {
            PrintUsage(
                "job submit [agentId] <actionType> [resourceId] [--model <id>] [--conv <id>] [--lang <code>]",
                "  Agent can be omitted when --conv is specified.",
                "job list <agentId>",
                "job status <jobId>",
                "job approve <jobId>",
                "job stop <jobId>                           Stop a transcription job (complete)",
                "job cancel <jobId>",
                "job listen <jobId>                         Stream live transcription segments",
                "",
                "Action types (global): ExecuteAsAdmin, CreateSubAgent, CreateContainer,",
                "  RegisterInfoStore, EditAnyTask",
                "Action types (resource): ExecuteAsSystemUser, AccessLocalInfoStore,",
                "  AccessExternalInfoStore, AccessWebsite, QuerySearchEngine,",
                "  AccessContainer, ManageAgent, EditTask, AccessSkill",
                "Transcription types: TranscribeFromAudioDevice,",
                "  TranscribeFromAudioStream (API only), TranscribeFromAudioFile (API only)",
                "",
                "Transcription: submit with TranscribeFromAudioDevice and audio device",
                "  as resourceId.",
                "  Optional flags: --model <id>, --conv <id>, --lang <code>");
            return Results.Ok();
        }

        var sub = args[1].ToLowerInvariant();
        var svc = sp.GetRequiredService<AgentJobService>();

        return sub switch
        {
            // job submit <agentId> <actionType> [resourceId] [flags...]
            "submit" when args.Length >= 4 && Enum.TryParse<AgentActionType>(args[3], true, out var at)
                => await HandleJobSubmit(args, CliIdMap.Resolve(args[2]), at, 4, svc),

            // job submit <actionType> [resourceId] [flags...]  (agent inferred from --conv)
            "submit" when args.Length >= 3 && Enum.TryParse<AgentActionType>(args[2], true, out var at2)
                => await HandleJobSubmit(args, Guid.Empty, at2, 3, svc),

            "submit" when args.Length < 3
                => UsageResult(
                    "job submit [agentId] <actionType> [resourceId] [--model <id>] [--conv <id>] [--lang <code>]",
                    "  Agent can be omitted when --conv is specified."),
            "submit"
                => UsageResult($"Unknown action type. Valid types: {string.Join(", ", Enum.GetNames<AgentActionType>())}"),

            "list" when args.Length >= 3
                => await AgentJobHandlers.List(CliIdMap.Resolve(args[2]), svc),
            "list" => UsageResult("job list <agentId>"),

            "status" when args.Length >= 3
                => await AgentJobHandlers.GetById(Guid.Empty, CliIdMap.Resolve(args[2]), svc),
            "status" => UsageResult("job status <jobId>"),

            "approve" when args.Length >= 3
                => await AgentJobHandlers.Approve(
                    Guid.Empty, CliIdMap.Resolve(args[2]),
                    new ApproveAgentJobRequest(ApproverUserId: _currentUserId),
                    svc),
            "approve" => UsageResult("job approve <jobId>"),

            "stop" when args.Length >= 3
                => await AgentJobHandlers.Stop(Guid.Empty, CliIdMap.Resolve(args[2]), svc),
            "stop" => UsageResult("job stop <jobId>"),

            "cancel" when args.Length >= 3
                => await AgentJobHandlers.Cancel(Guid.Empty, CliIdMap.Resolve(args[2]), svc),
            "cancel" => UsageResult("job cancel <jobId>"),

            "listen" when args.Length >= 3
                => await HandleJobListen(CliIdMap.Resolve(args[2]), svc),
            "listen" => UsageResult("job listen <jobId>"),

            _ => UsageResult($"Unknown sub-command: job {sub}. Try 'help' for usage.")
        };
    }

    private static async Task<IResult> HandleJobSubmit(
        string[] args, Guid agentId, AgentActionType actionType, int nextArg, AgentJobService svc)
    {
        // Resource ID is the next positional arg, unless it looks like a flag
        Guid? resourceId = args.Length > nextArg && !args[nextArg].StartsWith("--")
            ? CliIdMap.Resolve(args[nextArg])
            : null;
        var flagStart = resourceId is not null ? nextArg + 1 : nextArg;

        Guid? modelId = null;
        Guid? convId = null;
        string? language = null;

        for (var i = flagStart; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--model" or "-m" when i + 1 < args.Length:
                    modelId = CliIdMap.Resolve(args[++i]);
                    break;
                case "--conv" or "-c" when i + 1 < args.Length:
                    convId = CliIdMap.Resolve(args[++i]);
                    break;
                case "--lang" or "-l" when i + 1 < args.Length:
                    language = args[++i];
                    break;
            }
        }

        return await AgentJobHandlers.Submit(
            agentId,
            new SubmitAgentJobRequest(
                actionType,
                resourceId,
                CallerUserId: _currentUserId,
                TranscriptionModelId: modelId,
                ConversationId: convId,
                Language: language),
            svc);
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
            else if (job.ActionType is not AgentActionType.TranscribeFromAudioDevice
                     and not AgentActionType.TranscribeFromAudioStream
                     and not AgentActionType.TranscribeFromAudioFile)
            {
                Console.Error.WriteLine("Only transcription jobs support live listening.");
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

        Console.CancelKeyPress += OnCancelKey;

        Console.WriteLine("Listening for transcription segments... (Ctrl+C to stop)");
        Console.WriteLine();

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
            Console.CancelKeyPress -= OnCancelKey;
        }

        return Results.Ok();

        void OnCancelKey(object? sender, ConsoleCancelEventArgs e)
        {
            e.Cancel = true;
            cts.Cancel();
        }
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
    private static void PrintJsonWithShortIds(object value)
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

    private static async Task<IResult?> HandleAudioDeviceCommand(string[] args, IServiceProvider sp)
    {
        if (args.Length < 2)
        {
            PrintUsage(
                "audiodevice add <name> <deviceIdentifier> [description]",
                "  Use 'audiodevice devices' to find identifiers.",
                "  Use 'default' as identifier for system default input.",
                "audiodevice get <id>",
                "audiodevice list",
                "audiodevice update <id> [name] [deviceIdentifier]",
                "audiodevice delete <id>",
                "audiodevice devices                       List system audio inputs");
            return Results.Ok();
        }

        var sub = args[1].ToLowerInvariant();
        var svc = sp.GetRequiredService<TranscriptionService>();

        return sub switch
        {
            "add" when args.Length >= 4
                => await AudioDeviceHandlers.Create(
                    new CreateAudioDeviceRequest(
                        args[2],
                        args[3],
                        args.Length >= 5 ? string.Join(' ', args[4..]) : null),
                    svc),
            "add" => UsageResult("audiodevice add <name> <deviceIdentifier> [description]",
                "  Use 'audiodevice devices' to find identifiers.",
                "  Use 'default' as identifier for system default input."),

            "get" when args.Length >= 3
                => await AudioDeviceHandlers.GetById(CliIdMap.Resolve(args[2]), svc),
            "get" => UsageResult("audiodevice get <id>"),

            "list" => await AudioDeviceHandlers.List(svc),

            "update" when args.Length >= 4
                => await AudioDeviceHandlers.Update(
                    CliIdMap.Resolve(args[2]),
                    new UpdateAudioDeviceRequest(
                        args.Length >= 4 ? args[3] : null,
                        args.Length >= 5 ? args[4] : null),
                    svc),
            "update" => UsageResult("audiodevice update <id> [name] [deviceIdentifier]"),

            "delete" when args.Length >= 3
                => await AudioDeviceHandlers.Delete(CliIdMap.Resolve(args[2]), svc),
            "delete" => UsageResult("audiodevice delete <id>"),

            "devices" => ListSystemAudioDevices(sp),

            _ => UsageResult($"Unknown sub-command: audiodevice {sub}. Try 'help' for usage.")
        };
    }

    private static IResult ListSystemAudioDevices(IServiceProvider sp)
    {
        var capture = sp.GetRequiredService<IAudioCaptureProvider>();
        var devices = capture.ListDevices();

        if (devices.Count == 0)
        {
            Console.WriteLine("No audio input devices found.");
            return Results.Ok();
        }

        Console.WriteLine("System audio input devices:");
        foreach (var (id, name) in devices)
            Console.WriteLine($"  {name}  →  {id}");

        Console.WriteLine();
        Console.WriteLine("Use the identifier with 'audiodevice add <name> <identifier>'.");
        Console.WriteLine("Use 'default' to select the system default input device.");
        return Results.Ok();
    }

    private static IResult PrintHelp()
    {
        Console.WriteLine("""
            SharpClaw - Shell Agent

            IDs can be full GUIDs or short #numbers shown in command output.
            Short IDs are CLI-only and do not persist between restarts.

            Commands:
              register <username> <password>              Register and log in
              login <username> <password>                 Log in
              logout                                      Log out

              provider add <name> <type> [endpoint]       Add a provider
                Types: OpenAI, Anthropic, OpenRouter, GoogleVertexAI,
                       GoogleGemini, ZAI, VercelAIGateway, XAI, Groq,
                       Cerebras, Mistral, GitHubCopilot, Custom
              provider get <id>                           Show a provider
              provider list                               List providers
              provider update <id> <name> [endpoint]      Update a provider
              provider delete <id>                        Delete a provider
              provider set-key <id> <apiKey>              Set API key for a provider
              provider login <id>                         Authenticate via device code flow
              provider sync <id>                          Sync models from provider API
              provider refresh-caps <id>                  Re-infer model capabilities

              model add <name> <providerId> [--cap <caps>]  Add a model
                Capabilities (comma-separated): Chat, Transcription,
                  ImageGeneration, Embedding, TextToSpeech
              model get <id>                              Show a model
              model list                                  List models
              model update <id> <name> [--cap <caps>]     Update a model
              model delete <id>                           Delete a model

              agent add <name> <modelId> [system prompt]  Create an agent
              agent get <id>                              Show an agent
              agent list                                  List agents
              agent update <id> <name> [modelId] [prompt]
                                                          Update an agent
              agent role <id> <roleId>                    Assign a role to an agent
              agent role <id> none                        Remove agent role
              agent delete <id>                           Delete an agent

              context new <agentId> [name]                Create a context
              context list [agentId]                      List contexts
              context get <id>                            Show context details
              context update <id> <name>                  Rename a context
              context delete <id>                         Delete a context

              conversation new <agentId> [--context <id>] [title]
                                                          Start a conversation
              conversation list [agentId]                 List conversations
              conversation select <id>                    Select active conversation
              conversation get <id>                       Show conversation details
              conversation model <id> <modelId>           Change conversation model
              conversation attach <id> <contextId>        Attach to a context
              conversation detach <id>                    Detach from context
              conversation delete <id>                    Delete a conversation

              chat <message>                              Chat in active conversation

              role list                                    List all roles

              job submit [agentId] <type> [resourceId] [--model <id>] [--conv <id>] [--lang <code>]
                                                          Submit an agent action job
                Agent can be omitted when --conv is specified.
                Global types: ExecuteAsAdmin, CreateSubAgent,
                  CreateContainer, RegisterInfoStore, EditAnyTask
                Resource types: ExecuteAsSystemUser, AccessLocalInfoStore,
                  AccessExternalInfoStore, AccessWebsite, QuerySearchEngine,
                  AccessContainer, ManageAgent, EditTask, AccessSkill
                Transcription types: TranscribeFromAudioDevice,
                  TranscribeFromAudioStream (API only),
                  TranscribeFromAudioFile (API only)
              job list <agentId>                           List jobs for an agent
              job status <jobId>                           Check job status & logs
              job approve <jobId>                          Approve a pending job
              job stop <jobId>                             Stop a transcription job
              job cancel <jobId>                           Cancel a job
              job listen <jobId>                           Stream live transcription

              audiodevice add <name> <deviceId> [desc]    Register an audio device
              audiodevice get <id>                        Show an audio device
              audiodevice list                            List audio devices
              audiodevice update <id> [name] [deviceId]   Update an audio device
              audiodevice delete <id>                     Delete an audio device
              audiodevice devices                         List system audio inputs
              (alias: ad)

              Transcription: use TranscribeFromAudioDevice with audio device
                as resourceId.
                job submit TranscribeFromAudioDevice [deviceId] --conv <id> [--model <id>] [--lang <code>]
                job listen <jobId>
                job stop <jobId>

              exit / quit                                 Shut down
            """);
        return Results.Ok();
    }
}
