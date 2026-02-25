using System.Diagnostics;
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
using SharpClaw.Contracts.DTOs.Channels;
using SharpClaw.Contracts.DTOs.Models;
using SharpClaw.Contracts.DTOs.Providers;
using SharpClaw.Contracts.DTOs.Containers;
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
    private static Guid? _currentChannelId;
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
                _currentChannelId = null;
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
            "chat" => await HandleChatCommand(args, sp),
            "job" => await HandleJobCommand(args, sp),
            "role" => await HandleRoleCommand(args, sp),
            "resource" => await HandleResourceCommand(args, sp),
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
                => await ChannelContextHandlers.Create(
                    new CreateContextRequest(
                        CliIdMap.Resolve(args[2]),
                        args.Length >= 4 ? string.Join(' ', args[3..]) : null),
                    svc),
            "new" => UsageResult("context new <agentId> [name]"),

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

            "delete" when args.Length >= 3
                => await ChannelContextHandlers.Delete(CliIdMap.Resolve(args[2]), svc),
            "delete" => UsageResult("context delete <id>"),

            _ => UsageResult($"Unknown sub-command: context {sub}. Try 'help' for usage.")
        };
    }

    private static async Task<IResult?> HandleChatCommand(string[] args, IServiceProvider sp)
    {
        if (args.Length < 2)
        {
            PrintUsage(
                "chat [--agent <id>] <message>",
                "  --agent overrides the channel's default agent.");
            return Results.Ok();
        }

        var channelSvc = sp.GetRequiredService<ChannelService>();

        // Auto-select latest channel if none is selected
        if (_currentChannelId is null)
        {
            var latest = await channelSvc.GetLatestActiveAsync();
            if (latest is null)
            {
                Console.Error.WriteLine("Error: No channel selected and no channels exist.");
                Console.Error.WriteLine("Create one first: channel new <agentId> [title]");
                return Results.Ok();
            }

            _currentChannelId = latest.Id;
            Console.WriteLine($"No channel selected. Opening latest channel: \"{latest.Title}\" ({CliIdMap.GetOrAssign(latest.Id)})");
        }

        // Parse --agent flag and collect message parts
        Guid? agentId = null;
        var messageParts = new List<string>();
        for (var i = 1; i < args.Length; i++)
        {
            if (args[i] is "--agent" or "-a" && i + 1 < args.Length)
                agentId = CliIdMap.Resolve(args[++i]);
            else
                messageParts.Add(args[i]);
        }

        if (messageParts.Count == 0)
        {
            PrintUsage("chat [--agent <id>] <message>");
            return Results.Ok();
        }

        var chatService = sp.GetRequiredService<ChatService>();
        var request = new ChatRequest(string.Join(' ', messageParts), agentId);
        var wroteText = false;

        async Task<bool> CliApprovalCallback(
            Contracts.DTOs.AgentActions.AgentJobResponse job, CancellationToken ct)
        {
            Console.Write("Approve? (y/n): ");
            var input = await Task.Run(Console.ReadLine, ct);
            return input?.Trim().Equals("y", StringComparison.OrdinalIgnoreCase) == true;
        }

        await foreach (var evt in chatService.SendMessageStreamAsync(
            _currentChannelId.Value, request, CliApprovalCallback))
        {
            switch (evt.Type)
            {
                case ChatStreamEventType.TextDelta:
                    Console.Write(evt.Delta);
                    wroteText = true;
                    break;

                case ChatStreamEventType.ToolCallStart:
                    if (wroteText) { Console.WriteLine(); wroteText = false; }
                    Console.WriteLine($"  [tool] #{CliIdMap.GetOrAssign(evt.Job!.Id)} {evt.Job.ActionType} → {evt.Job.Status}");
                    break;

                case ChatStreamEventType.ToolCallResult:
                    Console.WriteLine($"  [result] #{CliIdMap.GetOrAssign(evt.Result!.Id)} → {evt.Result.Status}");
                    break;

                case ChatStreamEventType.ApprovalRequired:
                    if (wroteText) { Console.WriteLine(); wroteText = false; }
                    Console.Write($"  [approval] Job #{CliIdMap.GetOrAssign(evt.PendingJob!.Id)} ({evt.PendingJob.ActionType}) requires approval. ");
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

        return Results.Ok();
    }

    private static async Task<IResult?> HandleChannelCommand(string[] args, IServiceProvider sp)
    {
        if (args.Length < 2)
        {
            PrintUsage(
                "channel new <agentId> [--context <ctxId>] [title]",
                "                                           Create a channel",
                "channel list [agentId]                     List channels",
                "channel select <id>                        Select active channel",
                "channel get <id>                           Show channel details",
                "channel attach <id> <contextId>            Attach to a context",
                "channel detach <id>                        Detach from context",
                "channel agents <id>                        List allowed agents",
                "channel agents <id> add <agentId>          Allow an agent",
                "channel agents <id> remove <agentId>       Remove an allowed agent",
                "channel delete <id>                        Delete a channel");
            return Results.Ok();
        }

        var sub = args[1].ToLowerInvariant();
        var svc = sp.GetRequiredService<ChannelService>();

        return sub switch
        {
            "new" when args.Length >= 3
                => await HandleChannelNew(args, svc),
            "new" => UsageResult("channel new <agentId> [--context <ctxId>] [title]"),

            "list" => await HandleChannelList(args, svc),

            "select" when args.Length >= 3
                => HandleChannelSelect(args),
            "select" => UsageResult("channel select <id>"),

            "get" when args.Length >= 3
                => await ChannelHandlers.GetById(CliIdMap.Resolve(args[2]), svc),
            "get" => UsageResult("channel get <id>"),

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

            "delete" when args.Length >= 3
                => await HandleChannelDelete(args, svc),
            "delete" => UsageResult("channel delete <id>"),

            _ => UsageResult($"Unknown sub-command: channel {sub}. Try 'help' for usage.")
        };
    }

    private static async Task<IResult> HandleChannelNew(string[] args, ChannelService svc)
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

        var result = await ChannelHandlers.Create(
            new CreateChannelRequest(agentId, title, ContextId: contextId), svc);

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
        Console.WriteLine($"Channel {args[2]} selected.");
        return Results.Ok();
    }

    private static async Task<IResult> HandleChannelDelete(string[] args, ChannelService svc)
    {
        var id = CliIdMap.Resolve(args[2]);
        var result = await ChannelHandlers.Delete(id, svc);

        // Clear selection if the deleted channel was the active one
        if (_currentChannelId == id)
            _currentChannelId = null;

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
                DefaultAgentId = ch.AgentId,
                AllowedAgentIds = ch.AllowedAgentIds
            });
            return Results.Ok();
        }

        var action = args[3].ToLowerInvariant();

        if (action == "add" && args.Length >= 5)
        {
            var agentToAdd = CliIdMap.Resolve(args[4]);
            var ch = await svc.GetByIdAsync(channelId);
            if (ch is null) return Results.NotFound();

            var updated = ch.AllowedAgentIds.ToList();
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

            var updated = ch.AllowedAgentIds.Where(id => id != agentToRemove).ToList();

            return await ChannelHandlers.Update(
                channelId,
                new UpdateChannelRequest(AllowedAgentIds: updated),
                svc);
        }

        return UsageResult("channel agents <channelId> [add|remove <agentId>]");
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
                "job submit <actionType> [resourceId] [--conv <id>] [--agent <id>] [--model <id>] [--lang <code>]",
                "  Uses current channel by default, or specify --conv to override.",
                "  --agent overrides the channel's default agent.",
                "job list [channelId]                       List jobs (current channel if omitted)",
                "job status <jobId>",
                "job approve <jobId>",
                "job stop <jobId>                           Stop a transcription job (complete)",
                "job cancel <jobId>",
                "job listen <jobId>                         Stream live transcription segments",
                "",
                "Action types (global): CreateSubAgent, CreateContainer,",
                "  RegisterInfoStore, EditAnyTask",
                "Action types (resource): UnsafeExecuteAsDangerousShell, ExecuteAsSafeShell,",
                "  AccessLocalInfoStore,",
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
            // job submit <actionType> [resourceId] [flags...]
            "submit" when args.Length >= 3 && Enum.TryParse<AgentActionType>(args[2], true, out var at)
                => await HandleJobSubmit(args, at, 3, svc),

            "submit" when args.Length < 3
                => UsageResult(
                    "job submit <actionType> [resourceId] [--conv <id>] [--agent <id>] [--model <id>] [--lang <code>]",
                    "  Uses current channel by default, or specify --conv to override.",
                    "  --agent overrides the channel's default agent."),
            "submit"
                => UsageResult($"Unknown action type. Valid types: {string.Join(", ", Enum.GetNames<AgentActionType>())}"),

            "list" when args.Length >= 3
                => await AgentJobHandlers.List(CliIdMap.Resolve(args[2]), svc),
            "list" when _currentChannelId.HasValue
                => await AgentJobHandlers.List(_currentChannelId.Value, svc),
            "list" => UsageResult("job list [channelId]  (no current channel selected — specify a channel ID or use 'channel select')"),

            "status" when args.Length >= 3
                => await AgentJobHandlers.GetById(Guid.Empty, CliIdMap.Resolve(args[2]), svc),
            "status" => UsageResult("job status <jobId>"),

            "approve" when args.Length >= 3
                => await AgentJobHandlers.Approve(
                    Guid.Empty, CliIdMap.Resolve(args[2]),
                    new ApproveAgentJobRequest(),
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
        string[] args, AgentActionType actionType, int nextArg, AgentJobService svc)
    {
        // Resource ID is the next positional arg, unless it looks like a flag
        Guid? resourceId = args.Length > nextArg && !args[nextArg].StartsWith("--")
            ? CliIdMap.Resolve(args[nextArg])
            : null;
        var flagStart = resourceId is not null ? nextArg + 1 : nextArg;

        Guid? modelId = null;
        Guid? convId = null;
        Guid? agentId = null;
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
                case "--agent" or "-a" when i + 1 < args.Length:
                    agentId = CliIdMap.Resolve(args[++i]);
                    break;
                case "--lang" or "-l" when i + 1 < args.Length:
                    language = args[++i];
                    break;
            }
        }

        var channelId = convId ?? _currentChannelId;
        if (!channelId.HasValue)
            return UsageResult("No channel specified. Use --conv <id> or select a channel with 'channel select'.");

        return await AgentJobHandlers.Submit(
            channelId.Value,
            new SubmitAgentJobRequest(
                actionType,
                resourceId,
                AgentId: agentId,
                TranscriptionModelId: modelId,
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

    // ═══════════════════════════════════════════════════════════════
    // Resource (unified: container, audiodevice, ...)
    // ═══════════════════════════════════════════════════════════════

    private static async Task<IResult?> HandleResourceCommand(string[] args, IServiceProvider sp)
    {
        if (args.Length < 2)
        {
            PrintUsage(
                "resource <type> <command> [args...]",
                "",
                "Types: container, audiodevice",
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
        return type switch
        {
            "container" => await HandleResourceContainerCommand(args, sp),
            "audiodevice" => await HandleResourceAudioDeviceCommand(args, sp),
            _ => UsageResult($"Unknown resource type: {type}. " +
                "Available: container, audiodevice")
        };
    }

    private static async Task<IResult?> HandleResourceContainerCommand(
        string[] args, IServiceProvider sp)
    {
        if (args.Length < 3)
        {
            PrintUsage(
                "resource container add mk8shell <name> <path>  Create an mk8shell sandbox",
                "resource container get <id>                    Show a container",
                "resource container list                        List all containers",
                "resource container update <id> [description]   Update a container",
                "resource container delete <id>                 Delete a container",
                "resource container sync                        Import from mk8.shell registry");
            return Results.Ok();
        }

        var sub = args[2].ToLowerInvariant();
        var svc = sp.GetRequiredService<ContainerService>();

        return sub switch
        {
            "add" when args.Length >= 6
                && args[3].Equals("mk8shell", StringComparison.OrdinalIgnoreCase)
                => await ResourceHandlers.CreateContainer(
                    new CreateContainerRequest(
                        ContainerType.Mk8Shell,
                        args[4],
                        args[5],
                        args.Length >= 7 ? string.Join(' ', args[6..]) : null),
                    svc),
            "add" => UsageResult(
                "resource container add mk8shell <name> <parentPath>",
                "  Example: resource container add mk8shell Banana D:/"),

            "get" when args.Length >= 4
                => await ResourceHandlers.GetContainer(CliIdMap.Resolve(args[3]), svc),
            "get" => UsageResult("resource container get <id>"),

            "list" => await ResourceHandlers.ListContainers(svc),

            "update" when args.Length >= 5
                => await ResourceHandlers.UpdateContainer(
                    CliIdMap.Resolve(args[3]),
                    new UpdateContainerRequest(
                        Description: string.Join(' ', args[4..])),
                    svc),
            "update" => UsageResult("resource container update <id> [description]"),

            "delete" when args.Length >= 4
                => await ResourceHandlers.DeleteContainer(CliIdMap.Resolve(args[3]), svc),
            "delete" => UsageResult("resource container delete <id>"),

            "sync" => await ResourceHandlers.SyncContainers(svc),

            _ => UsageResult($"Unknown command: resource container {sub}")
        };
    }

    private static async Task<IResult?> HandleResourceAudioDeviceCommand(
        string[] args, IServiceProvider sp)
    {
        if (args.Length < 3)
        {
            PrintUsage(
                "resource audiodevice add <name> [identifier] [description]",
                "resource audiodevice get <id>                  Show an audio device",
                "resource audiodevice list                      List all audio devices",
                "resource audiodevice update <id> [name] [id]   Update an audio device",
                "resource audiodevice delete <id>               Delete an audio device",
                "resource audiodevice sync                      Import system audio devices");
            return Results.Ok();
        }

        var sub = args[2].ToLowerInvariant();
        var svc = sp.GetRequiredService<TranscriptionService>();

        return sub switch
        {
            "add" when args.Length >= 4
                => await ResourceHandlers.CreateAudioDevice(
                    new CreateAudioDeviceRequest(
                        args[3],
                        args.Length >= 5 ? args[4] : null,
                        args.Length >= 6 ? string.Join(' ', args[5..]) : null),
                    svc),
            "add" => UsageResult("resource audiodevice add <name> [deviceIdentifier] [description]"),

            "get" when args.Length >= 4
                => await ResourceHandlers.GetAudioDevice(CliIdMap.Resolve(args[3]), svc),
            "get" => UsageResult("resource audiodevice get <id>"),

            "list" => await ResourceHandlers.ListAudioDevices(svc),

            "update" when args.Length >= 5
                => await ResourceHandlers.UpdateAudioDevice(
                    CliIdMap.Resolve(args[3]),
                    new UpdateAudioDeviceRequest(
                        args.Length >= 5 ? args[4] : null,
                        args.Length >= 6 ? args[5] : null),
                    svc),
            "update" => UsageResult("resource audiodevice update <id> [name] [deviceIdentifier]"),

            "delete" when args.Length >= 4
                => await ResourceHandlers.DeleteAudioDevice(CliIdMap.Resolve(args[3]), svc),
            "delete" => UsageResult("resource audiodevice delete <id>"),

            "sync" => await ResourceHandlers.SyncAudioDevices(svc),

            _ => UsageResult($"Unknown command: resource audiodevice {sub}")
        };
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

              channel new <agentId> [--context <id>] [title]
                                                          Start a channel
              channel list [agentId]                      List channels
              channel select <id>                         Select active channel
              channel get <id>                            Show channel details
              channel attach <id> <contextId>             Attach to a context
              channel detach <id>                         Detach from context
              channel agents <id>                         List allowed agents
              channel agents <id> add <agentId>           Allow an agent
              channel agents <id> remove <agentId>        Remove an allowed agent
              channel delete <id>                         Delete a channel

              chat [--agent <id>] <message>               Chat in active channel

              role list                                    List all roles

              job submit <type> [resourceId] [--conv <id>] [--agent <id>] [--model <id>] [--lang <code>]
                                                          Submit a job on the current channel
                Uses current channel by default, or --conv to override.
                --agent overrides the channel's default agent.
                Global types: ExecuteAsAdmin, CreateSubAgent,
                  CreateContainer, RegisterInfoStore, EditAnyTask
                Resource types: ExecuteAsSystemUser, AccessLocalInfoStore,
                  AccessExternalInfoStore, AccessWebsite, QuerySearchEngine,
                  AccessContainer, ManageAgent, EditTask, AccessSkill
                Transcription types: TranscribeFromAudioDevice,
                  TranscribeFromAudioStream (API only),
                  TranscribeFromAudioFile (API only)
              job list [channelId]                         List jobs (current channel if omitted)
              job status <jobId>                           Check job status & logs
              job approve <jobId>                          Approve a pending job
              job stop <jobId>                             Stop a transcription job
              job cancel <jobId>                           Cancel a job
              job listen <jobId>                           Stream live transcription

              resource <type> <command> [args...]
                Available types: container, audiodevice
                Commands (all types): add, get, list, update, delete, sync

              resource container add <type> <name> <path>  Create a container
              resource audiodevice add <name> [devId] [d]  Register an audio device

              exit / quit                                 Shut down
            """);
        return Results.Ok();
    }
}
