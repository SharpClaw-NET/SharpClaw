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
using SharpClaw.Contracts.DTOs.DefaultResources;
using SharpClaw.Contracts.DTOs.Roles;
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
    private static bool _chatMode;
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
            "bio" => await HandleBioCommand(args, sp),
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
                "provider sync-models <providerId>");
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

            "sync-models" when args.Length >= 3
                => await HandleProviderSync(CliIdMap.Resolve(args[2]), svc),
            "sync-models" => UsageResult("provider sync-models <id>"),

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
                DefaultAgentId = ctx.AgentId,
                AllowedAgentIds = ctx.AllowedAgentIds
            });
            return Results.Ok();
        }

        var action = args[3].ToLowerInvariant();

        if (action == "add" && args.Length >= 5)
        {
            var agentToAdd = CliIdMap.Resolve(args[4]);
            var ctx = await svc.GetByIdAsync(contextId);
            if (ctx is null) return Results.NotFound();

            var updated = ctx.AllowedAgentIds.ToList();
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

            var updated = ctx.AllowedAgentIds.Where(id => id != agentToRemove).ToList();

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
    /// externalinfo, audiodevice, agent, task, skill, transcriptionmodel.
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
                return UsageResult($"Unknown key '{extra[1]}'. Valid keys: safeshell, dangshell, container, website, search, localinfo, externalinfo, audiodevice, agent, task, skill, transcriptionmodel");

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
            "safeshell" => new(d.DangerousShellResourceId, value, d.ContainerResourceId, d.WebsiteResourceId, d.SearchEngineResourceId, d.LocalInfoStoreResourceId, d.ExternalInfoStoreResourceId, d.AudioDeviceResourceId, d.AgentResourceId, d.TaskResourceId, d.SkillResourceId, d.TranscriptionModelId),
            "dangshell" or "dangerousshell" => new(value, d.SafeShellResourceId, d.ContainerResourceId, d.WebsiteResourceId, d.SearchEngineResourceId, d.LocalInfoStoreResourceId, d.ExternalInfoStoreResourceId, d.AudioDeviceResourceId, d.AgentResourceId, d.TaskResourceId, d.SkillResourceId, d.TranscriptionModelId),
            "container" => new(d.DangerousShellResourceId, d.SafeShellResourceId, value, d.WebsiteResourceId, d.SearchEngineResourceId, d.LocalInfoStoreResourceId, d.ExternalInfoStoreResourceId, d.AudioDeviceResourceId, d.AgentResourceId, d.TaskResourceId, d.SkillResourceId, d.TranscriptionModelId),
            "website" => new(d.DangerousShellResourceId, d.SafeShellResourceId, d.ContainerResourceId, value, d.SearchEngineResourceId, d.LocalInfoStoreResourceId, d.ExternalInfoStoreResourceId, d.AudioDeviceResourceId, d.AgentResourceId, d.TaskResourceId, d.SkillResourceId, d.TranscriptionModelId),
            "search" or "searchengine" => new(d.DangerousShellResourceId, d.SafeShellResourceId, d.ContainerResourceId, d.WebsiteResourceId, value, d.LocalInfoStoreResourceId, d.ExternalInfoStoreResourceId, d.AudioDeviceResourceId, d.AgentResourceId, d.TaskResourceId, d.SkillResourceId, d.TranscriptionModelId),
            "localinfo" or "localinfostore" => new(d.DangerousShellResourceId, d.SafeShellResourceId, d.ContainerResourceId, d.WebsiteResourceId, d.SearchEngineResourceId, value, d.ExternalInfoStoreResourceId, d.AudioDeviceResourceId, d.AgentResourceId, d.TaskResourceId, d.SkillResourceId, d.TranscriptionModelId),
            "externalinfo" or "externalinfostore" => new(d.DangerousShellResourceId, d.SafeShellResourceId, d.ContainerResourceId, d.WebsiteResourceId, d.SearchEngineResourceId, d.LocalInfoStoreResourceId, value, d.AudioDeviceResourceId, d.AgentResourceId, d.TaskResourceId, d.SkillResourceId, d.TranscriptionModelId),
            "audiodevice" or "audio" => new(d.DangerousShellResourceId, d.SafeShellResourceId, d.ContainerResourceId, d.WebsiteResourceId, d.SearchEngineResourceId, d.LocalInfoStoreResourceId, d.ExternalInfoStoreResourceId, value, d.AgentResourceId, d.TaskResourceId, d.SkillResourceId, d.TranscriptionModelId),
            "agent" => new(d.DangerousShellResourceId, d.SafeShellResourceId, d.ContainerResourceId, d.WebsiteResourceId, d.SearchEngineResourceId, d.LocalInfoStoreResourceId, d.ExternalInfoStoreResourceId, d.AudioDeviceResourceId, value, d.TaskResourceId, d.SkillResourceId, d.TranscriptionModelId),
            "task" => new(d.DangerousShellResourceId, d.SafeShellResourceId, d.ContainerResourceId, d.WebsiteResourceId, d.SearchEngineResourceId, d.LocalInfoStoreResourceId, d.ExternalInfoStoreResourceId, d.AudioDeviceResourceId, d.AgentResourceId, value, d.SkillResourceId, d.TranscriptionModelId),
            "skill" => new(d.DangerousShellResourceId, d.SafeShellResourceId, d.ContainerResourceId, d.WebsiteResourceId, d.SearchEngineResourceId, d.LocalInfoStoreResourceId, d.ExternalInfoStoreResourceId, d.AudioDeviceResourceId, d.AgentResourceId, d.TaskResourceId, value, d.TranscriptionModelId),
            "transcriptionmodel" or "model" => new(d.DangerousShellResourceId, d.SafeShellResourceId, d.ContainerResourceId, d.WebsiteResourceId, d.SearchEngineResourceId, d.LocalInfoStoreResourceId, d.ExternalInfoStoreResourceId, d.AudioDeviceResourceId, d.AgentResourceId, d.TaskResourceId, d.SkillResourceId, value),
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
                "chat [--agent <id>] <message>",
                "  chat toggle                             Toggle chat mode on/off",
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
        var request = new ChatRequest(string.Join(' ', messageParts), agentId, ChatClientType.CLI);
        var wroteText = false;

        async Task<bool> CliApprovalCallback(
            AgentJobResponse job, CancellationToken ct)
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
                "channel new [agentId] [--context <ctxId>] [title]",
                "  Either agentId or --context is required.",
                "channel list [agentId]                     List channels",
                "channel select <id>                        Select active channel",
                "channel get <id>                           Show channel details",
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
            "new" when args.Length >= 3
                => await HandleChannelNew(args, svc),
            "new" => UsageResult("channel new [agentId] [--context <ctxId>] [title]"),

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

            "defaults" when args.Length >= 3
                => await HandleDefaults("channel", CliIdMap.Resolve(args[2]), args[3..], sp),
            "defaults" => UsageResult("channel defaults <id> [set <key> <resId> | clear <key>]"),

            "delete" when args.Length >= 3
                => await HandleChannelDelete(args, svc),
            "delete" => UsageResult("channel delete <id>"),

            _ => UsageResult($"Unknown sub-command: channel {sub}. Try 'help' for usage.")
        };
    }

    private static async Task<IResult> HandleChannelNew(string[] args, ChannelService svc)
    {
        Guid? agentId = null;
        Guid? contextId = null;
        var titleParts = new List<string>();

        // Parse args: [agentId] [--context <ctxId>] [title...]
        // The first positional arg is an agent ID unless it looks like a flag.
        var nextArg = 2;
        if (args.Length > nextArg && !args[nextArg].StartsWith("--"))
        {
            try
            {
                agentId = CliIdMap.Resolve(args[nextArg]);
                nextArg++;
            }
            catch
            {
                // Not a resolvable ID — treat as title.
            }
        }

        for (var i = nextArg; i < args.Length; i++)
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

        if (agentId is null && contextId is null)
        {
            Console.Error.WriteLine("Either an agent ID or --context is required.");
            return Results.Ok();
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
            PrintUsage(
                "role list                                 List all roles",
                "role permissions <roleId>                 Show role permissions",
                "role permissions <roleId> set [flags...]  Set role permissions",
                "",
                "Permission flags:",
                "  --clearance <level>                     Default clearance (Unset, Independent, etc.)",
                "  --create-sub-agents                     Grant CanCreateSubAgents",
                "  --create-containers                     Grant CanCreateContainers",
                "  --register-info-stores                  Grant CanRegisterInfoStores",
                "  --localhost-browser                     Grant CanAccessLocalhostInBrowser",
                "  --localhost-cli                         Grant CanAccessLocalhostCli",
                "  --dangerous-shell <id>[:<clearance>]    Add DangerousShell grant",
                "  --safe-shell <id>[:<clearance>]         Add SafeShell grant",
                "  --container <id>[:<clearance>]          Add Container grant",
                "  --website <id>[:<clearance>]            Add Website grant",
                "  --search-engine <id>[:<clearance>]      Add SearchEngine grant",
                "  --local-info <id>[:<clearance>]         Add LocalInfoStore grant",
                "  --external-info <id>[:<clearance>]      Add ExternalInfoStore grant",
                "  --audio-device <id>[:<clearance>]       Add AudioDevice grant",
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
        if (_currentUserId is null)
            return UsageResult("You must be logged in to set permissions.");

        var clearance = PermissionClearance.Unset;
        var createSubAgents = false;
        var createContainers = false;
        var registerInfoStores = false;
        var localhostBrowser = false;
        var localhostCli = false;

        var dangerousShell = new List<ResourceGrant>();
        var safeShell = new List<ResourceGrant>();
        var container = new List<ResourceGrant>();
        var website = new List<ResourceGrant>();
        var searchEngine = new List<ResourceGrant>();
        var localInfo = new List<ResourceGrant>();
        var externalInfo = new List<ResourceGrant>();
        var audioDevice = new List<ResourceGrant>();
        var agent = new List<ResourceGrant>();
        var task = new List<ResourceGrant>();
        var skill = new List<ResourceGrant>();

        for (var i = 4; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--clearance" when i + 1 < args.Length:
                    if (Enum.TryParse<PermissionClearance>(args[++i], true, out var cl))
                        clearance = cl;
                    break;
                case "--create-sub-agents": createSubAgents = true; break;
                case "--create-containers": createContainers = true; break;
                case "--register-info-stores": registerInfoStores = true; break;
                case "--localhost-browser": localhostBrowser = true; break;
                case "--localhost-cli": localhostCli = true; break;
                case "--dangerous-shell" when i + 1 < args.Length:
                    dangerousShell.Add(ParseResourceGrant(args[++i])); break;
                case "--safe-shell" when i + 1 < args.Length:
                    safeShell.Add(ParseResourceGrant(args[++i])); break;
                case "--container" when i + 1 < args.Length:
                    container.Add(ParseResourceGrant(args[++i])); break;
                case "--website" when i + 1 < args.Length:
                    website.Add(ParseResourceGrant(args[++i])); break;
                case "--search-engine" when i + 1 < args.Length:
                    searchEngine.Add(ParseResourceGrant(args[++i])); break;
                case "--local-info" when i + 1 < args.Length:
                    localInfo.Add(ParseResourceGrant(args[++i])); break;
                case "--external-info" when i + 1 < args.Length:
                    externalInfo.Add(ParseResourceGrant(args[++i])); break;
                case "--audio-device" when i + 1 < args.Length:
                    audioDevice.Add(ParseResourceGrant(args[++i])); break;
                case "--agent" when i + 1 < args.Length:
                    agent.Add(ParseResourceGrant(args[++i])); break;
                case "--task" when i + 1 < args.Length:
                    task.Add(ParseResourceGrant(args[++i])); break;
                case "--skill" when i + 1 < args.Length:
                    skill.Add(ParseResourceGrant(args[++i])); break;
            }
        }

        var request = new SetRolePermissionsRequest(
            DefaultClearance: clearance,
            CanCreateSubAgents: createSubAgents,
            CanCreateContainers: createContainers,
            CanRegisterInfoStores: registerInfoStores,
            CanAccessLocalhostInBrowser: localhostBrowser,
            CanAccessLocalhostCli: localhostCli,
            DangerousShellAccesses: dangerousShell.Count > 0 ? dangerousShell : null,
            SafeShellAccesses: safeShell.Count > 0 ? safeShell : null,
            ContainerAccesses: container.Count > 0 ? container : null,
            WebsiteAccesses: website.Count > 0 ? website : null,
            SearchEngineAccesses: searchEngine.Count > 0 ? searchEngine : null,
            LocalInfoStoreAccesses: localInfo.Count > 0 ? localInfo : null,
            ExternalInfoStoreAccesses: externalInfo.Count > 0 ? externalInfo : null,
            AudioDeviceAccesses: audioDevice.Count > 0 ? audioDevice : null,
            AgentAccesses: agent.Count > 0 ? agent : null,
            TaskAccesses: task.Count > 0 ? task : null,
            SkillAccesses: skill.Count > 0 ? skill : null);

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
                "  RegisterInfoStore, AccessLocalhostInBrowser, AccessLocalhostCli",
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

    private static IResult PrintHelp()
    {
        Console.WriteLine("""
            SharpClaw - Shell Agent

            IDs: full GUIDs or short #numbers from output (session-only).
            Most entities support: add, get, list, update, delete.

            Auth:
              register <user> <pass>          login <user> <pass>          logout

            Provider:  provider <sub> [args]    (add, get, list, update, delete)
              provider add <name> <type> [endpoint]
                Types: OpenAI, Anthropic, OpenRouter, GoogleVertexAI, GoogleGemini,
                  ZAI, VercelAIGateway, XAI, Groq, Cerebras, Mistral, GitHubCopilot, Custom
              provider set-key <id> <apiKey>   login <id>   sync-models <id>   refresh-caps <id>

            Model:     model <sub> [args]       (add, get, list, update, delete)
              model add <name> <providerId> [--cap Chat,Transcription,...]

            Agent:     agent <sub> [args]       (add, get, list, update, delete)
              agent add <name> <modelId> [system prompt]
              agent role <id> <roleId|none>

            Role:      role <sub> [args]
              role list                          role permissions <id>
              role permissions <id> set [--create-sub-agents] [--safe-shell all:Independent] ...

            Context:   context|ctx <sub> [args] (new, get, list, update, delete)
              context new <agentId> [name]
              context agents <id> [add|remove <agentId>]
              context defaults <id> [set <key> <resId> | clear <key>]

            Channel:   channel|chan <sub> [args] (new, get, list, select, delete)
              channel new [agentId] [--context <id>] [title]
              channel attach|detach <id> [contextId]
              channel agents <id> [add|remove <agentId>]
              channel defaults <id> [set <key> <resId> | clear <key>]
              Fields cascade from context when not set: agent, permissions,
                DisableChatHeader, AllowedAgents, DefaultResourceSet.
              Default-resource keys: safeshell, dangshell, container, website,
                search, localinfo, externalinfo, audiodevice, agent, task,
                skill, transcriptionmodel

            Chat:
              chat [--agent <id>] <message>    Send a message in the active channel
              chat toggle                      Toggle chat mode (all input → chat)
                In chat mode: !exit or !chat toggle to return to normal mode.

            Bio:       bio get | set <text> | clear

            Job:       job <sub> [args]
              job submit <actionType> [resourceId] [--conv <id>] [--agent <id>]
                  [--model <id>] [--lang <code>]
              job list [channelId]   status <id>   approve <id>   cancel <id>
              job stop <id>          listen <id>   (transcription jobs)

            Resource:  resource <type> <sub>    (add, get, list, update, delete, sync)
              Types: container, audiodevice
              resource container add mk8shell <name> <path>
              resource audiodevice add <name> [identifier]

              exit / quit
            """);
        return Results.Ok();
    }
}
