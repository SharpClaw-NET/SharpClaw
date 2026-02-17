using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SharpClaw.Application.API.Handlers;
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

            if (line is "logout")
            {
                _currentUser = null;
                _currentUserId = null;
                _currentConversationId = null;
                Console.WriteLine("Logged out.");
                Console.WriteLine();
                continue;
            }

            var args = ParseArgs(line);

            if (!IsLoggedIn && !PublicCommands.Contains(args[0].ToLowerInvariant()))
            {
                Console.Error.WriteLine("Please log in first: login <username> <password>");
                Console.WriteLine();
                continue;
            }

            try
            {
                if (!await TryHandleAsync(args, services))
                    Console.Error.WriteLine($"Unknown command: {args[0]}. Type 'help' for usage.");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
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
                => await ProviderHandlers.SyncModels(CliIdMap.Resolve(args[2]), svc),
            "sync" => UsageResult("provider sync <id>"),

            _ => UsageResult($"Unknown sub-command: provider {sub}. Try 'help' for usage.")
        };
    }

    private static async Task<IResult?> HandleModelCommand(string[] args, IServiceProvider sp)
    {
        if (args.Length < 2)
        {
            PrintUsage(
                "model add <name> <providerId>",
                "model get <id>",
                "model list",
                "model update <id> <name>",
                "model delete <id>");
            return Results.Ok();
        }

        var sub = args[1].ToLowerInvariant();
        var svc = sp.GetRequiredService<ModelService>();

        return sub switch
        {
            "add" when args.Length >= 4
                => await ModelHandlers.Create(
                    new CreateModelRequest(args[2], CliIdMap.Resolve(args[3])), svc),
            "add" => UsageResult("model add <name> <providerId>"),

            "get" when args.Length >= 3
                => await ModelHandlers.GetById(CliIdMap.Resolve(args[2]), svc),
            "get" => UsageResult("model get <id>"),

            "list" => await ModelHandlers.List(svc),

            "update" when args.Length >= 4
                => await ModelHandlers.Update(
                    CliIdMap.Resolve(args[2]), new UpdateModelRequest(args[3]), svc),
            "update" => UsageResult("model update <id> <name>"),

            "delete" when args.Length >= 3
                => await ModelHandlers.Delete(CliIdMap.Resolve(args[2]), svc),
            "delete" => UsageResult("model delete <id>"),

            _ => UsageResult($"Unknown sub-command: model {sub}. Try 'help' for usage.")
        };
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

            "delete" when args.Length >= 3
                => await AgentHandlers.Delete(CliIdMap.Resolve(args[2]), svc),
            "delete" => UsageResult("agent delete <id>"),

            _ => UsageResult($"Unknown sub-command: agent {sub}. Try 'help' for usage.")
        };
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
                "context grant <id> <actionType> <clearance>",
                "                                           Set a default permission",
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

            "grant" when args.Length >= 5
                && Enum.TryParse<AgentActionType>(args[3], true, out var actionType)
                && Enum.TryParse<PermissionClearance>(args[4], true, out var clearance)
                => await ContextHandlers.Grant(
                    CliIdMap.Resolve(args[2]),
                    new PermissionGrantRequest(actionType, clearance),
                    svc),
            "grant" when args.Length < 5
                => UsageResult("context grant <id> <actionType> <clearance>"),
            "grant"
                => UsageResult(
                    $"Valid action types: {string.Join(", ", Enum.GetNames<AgentActionType>())}",
                    $"Valid clearances: {string.Join(", ", Enum.GetNames<PermissionClearance>())}"),

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
                "conversation grant <id> <actionType> <clearance>",
                "                                           Override permission for this conversation",
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

            "grant" when args.Length >= 5
                && Enum.TryParse<AgentActionType>(args[3], true, out var actionType)
                && Enum.TryParse<PermissionClearance>(args[4], true, out var clearance)
                => await ConversationHandlers.Grant(
                    CliIdMap.Resolve(args[2]),
                    new ConversationPermissionGrantRequest(actionType, clearance),
                    svc),
            "grant" when args.Length < 5
                => UsageResult("conversation grant <id> <actionType> <clearance>"),
            "grant"
                => UsageResult(
                    $"Valid action types: {string.Join(", ", Enum.GetNames<AgentActionType>())}",
                    $"Valid clearances: {string.Join(", ", Enum.GetNames<PermissionClearance>())}"),

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

    private static async Task<IResult?> HandleJobCommand(string[] args, IServiceProvider sp)
    {
        if (args.Length < 2)
        {
            PrintUsage(
                "job submit <agentId> <actionType> [resourceId]",
                "job list <agentId>",
                "job status <jobId>",
                "job approve <jobId>",
                "job cancel <jobId>",
                "",
                "Action types (global): ExecuteAsAdmin, CreateSubAgent, CreateContainer,",
                "  RegisterInfoStore, EditAnyTask",
                "Action types (resource): ExecuteAsSystemUser, AccessLocalInfoStore,",
                "  AccessExternalInfoStore, AccessWebsite, QuerySearchEngine,",
                "  AccessContainer, ManageAgent, EditTask, AccessSkill");
            return Results.Ok();
        }

        var sub = args[1].ToLowerInvariant();
        var svc = sp.GetRequiredService<AgentJobService>();

        return sub switch
        {
            "submit" when args.Length >= 4 && Enum.TryParse<AgentActionType>(args[3], true, out var at)
                => await AgentJobHandlers.Submit(
                    CliIdMap.Resolve(args[2]),
                    new SubmitAgentJobRequest(
                        at,
                        args.Length >= 5 ? CliIdMap.Resolve(args[4]) : null,
                        CallerUserId: _currentUserId),
                    svc),
            "submit" when args.Length < 4
                => UsageResult("job submit <agentId> <actionType> [resourceId]"),
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

            "cancel" when args.Length >= 3
                => await AgentJobHandlers.Cancel(Guid.Empty, CliIdMap.Resolve(args[2]), svc),
            "cancel" => UsageResult("job cancel <jobId>"),

            _ => UsageResult($"Unknown sub-command: job {sub}. Try 'help' for usage.")
        };
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
                PrintJsonWithShortIds(valueResult.Value);
                break;
            case IStatusCodeHttpResult { StatusCode: StatusCodes.Status200OK }:
                break;
            case IStatusCodeHttpResult { StatusCode: StatusCodes.Status401Unauthorized }:
                Console.Error.WriteLine("Unauthorized.");
                break;
            case IStatusCodeHttpResult { StatusCode: StatusCodes.Status404NotFound }:
                Console.Error.WriteLine("Not found.");
                break;
            case IStatusCodeHttpResult { StatusCode: StatusCodes.Status204NoContent }:
                Console.WriteLine("Done.");
                break;
            default:
            {
                await using var stream = new MemoryStream();
                var httpContext = new DefaultHttpContext { Response = { Body = stream } };
                await result.ExecuteAsync(httpContext);
                stream.Position = 0;
                using var reader = new StreamReader(stream);
                Console.WriteLine(await reader.ReadToEndAsync());
                break;
            }
        }
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

              model add <name> <providerId>               Add a model
              model get <id>                              Show a model
              model list                                  List models
              model update <id> <name>                    Update a model
              model delete <id>                           Delete a model

              agent add <name> <modelId> [system prompt]  Create an agent
              agent get <id>                              Show an agent
              agent list                                  List agents
              agent update <id> <name> [modelId] [prompt]
                                                          Update an agent
              agent delete <id>                           Delete an agent

              context new <agentId> [name]                Create a context
              context list [agentId]                      List contexts
              context get <id>                            Show context details
              context update <id> <name>                  Rename a context
              context grant <id> <type> <clearance>       Set a default permission
              context delete <id>                         Delete a context

              conversation new <agentId> [--context <id>] [title]
                                                          Start a conversation
              conversation list [agentId]                 List conversations
              conversation select <id>                    Select active conversation
              conversation get <id>                       Show conversation details
              conversation model <id> <modelId>           Change conversation model
              conversation attach <id> <contextId>        Attach to a context
              conversation detach <id>                    Detach from context
              conversation grant <id> <type> <clearance>  Override a permission
              conversation delete <id>                    Delete a conversation

              chat <message>                              Chat in active conversation

              job submit <agentId> <type> [resourceId]     Submit an agent action job
                Global types: ExecuteAsAdmin, CreateSubAgent,
                  CreateContainer, RegisterInfoStore, EditAnyTask
                Resource types: ExecuteAsSystemUser, AccessLocalInfoStore,
                  AccessExternalInfoStore, AccessWebsite, QuerySearchEngine,
                  AccessContainer, ManageAgent, EditTask, AccessSkill
              job list <agentId>                           List jobs for an agent
              job status <jobId>                           Check job status & logs
              job approve <jobId>                          Approve a pending job
              job cancel <jobId>                           Cancel a job

              exit / quit                                 Shut down
            """);
        return Results.Ok();
    }
}
