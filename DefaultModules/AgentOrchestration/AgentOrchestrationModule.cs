using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Microsoft.EntityFrameworkCore;

using SharpClaw.Contracts.Chat;
using SharpClaw.Contracts.DTOs.Tasks;
using SharpClaw.Contracts.Modules;
using SharpClaw.Contracts.Persistence;
using SharpClaw.Contracts.Services;
using SharpClaw.Contracts.Tasks;
using SharpClaw.Modules.AgentOrchestration.Services;

namespace SharpClaw.Modules.AgentOrchestration;

/// <summary>
/// Default module: agent lifecycle (create sub-agent, manage agent),
/// task editing, and skill access. All tools flow through the job pipeline.
/// </summary>
public sealed class AgentOrchestrationModule : ISharpClawModule, ITaskParserAware
{
    public const string ModuleIdValue = "sharpclaw_agent_orchestration";

    public string Id => ModuleIdValue;
    public string DisplayName => "Agent Orchestration";
    public string ToolPrefix => "ao";

    /// <summary>
    /// Parser extension contributed by the merged Task Scripting subsystem
    /// (folded into Agent Orchestration). Registers the <c>OnTimer</c>
    /// event-handler name and the scripting-language primitives.
    /// </summary>
    public ITaskParserModuleExtension ParserExtension => TaskScriptingParserExtension.Instance;

    // ═══════════════════════════════════════════════════════════════
    // DI Registration
    // ═══════════════════════════════════════════════════════════════

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddScoped(sp => sp.GetRequiredService<IModuleDbContextFactory>()
            .CreateDbContext<AgentOrchestrationDbContext>());
        services.TryAddScoped<AgentOrchestrationService>();
        services.AddScoped<ITaskStepExecutorExtension, AgentOrchestrationTaskStepExecutor>();

        // Module-owned chat contributor: surfaces task definitions as agent
        // tools when the agent has CanInvokeTasksAsTool. The flag key is read
        // directly from AgentOrchestrationPermissionKeys; the policy decision
        // and the data fetch both live in the module.
        services.AddScoped<IChatProcessingContributor, AgentOrchestrationChatContributor>();

        // Event-bus triggers (Event / TaskCompleted / TaskFailed) — moved here
        // from core by the trigger-extraction plan. The same instance is
        // exposed both as a trigger source and as a host event sink.
        services.AddSingleton<EventBusTriggerSource>();
        services.AddSingleton<ITaskTriggerSource>(sp => sp.GetRequiredService<EventBusTriggerSource>());
        services.AddSingleton<ISharpClawEventSink>(sp => sp.GetRequiredService<EventBusTriggerSource>());

        // ── Merged from sharpclaw_task_scripting ───────────────────
        // Task scripting primitives (declare/assign/conditional/loop/return/
        // event_handler/evaluate) and runtime control (delay/wait_until_stopped/log).
        services.AddScoped<ITaskStepExecutorExtension, TaskScriptingStepExecutor>();

        // Lifecycle triggers (Startup / Shutdown).
        services.AddSingleton<ITaskTriggerSource, LifecycleTriggerSource>();

        // Task-chain triggers (TaskCompleted / TaskFailed). Same instance is
        // exposed as a trigger source and as a host event sink so it observes
        // orchestrator completion events.
        services.AddSingleton<TaskChainTriggerSource>();
        services.AddSingleton<ITaskTriggerSource>(sp => sp.GetRequiredService<TaskChainTriggerSource>());
        services.AddSingleton<ISharpClawEventSink>(sp => sp.GetRequiredService<TaskChainTriggerSource>());

        // ── Scheduled jobs (relocated from core) ───────────────────
        // Module owns the scheduling logic; the host only exposes
        // ITaskInstanceLauncher to actually start a task instance.
        services.AddScoped<ScheduledJobService>();
        services.AddScoped<IScheduledJobService>(sp => sp.GetRequiredService<ScheduledJobService>());
        services.AddSingleton<ScheduledJobWorker>();
    }

    // ═══════════════════════════════════════════════════════════════
    // Contracts
    // ═══════════════════════════════════════════════════════════════

    public IReadOnlyList<ModuleContractExport> ExportedContracts => [];

    // ═══════════════════════════════════════════════════════════════
    // Resource Type Descriptors
    // ═══════════════════════════════════════════════════════════════

    public IReadOnlyList<ModuleResourceTypeDescriptor> GetResourceTypeDescriptors() =>
    [
        new("AoAgent", "ManageAgent", "ManageAgentAsync", static async (sp, ct) =>
        {
            var ids = sp.GetRequiredService<ICoreEntityIdProvider>();
            return await ids.GetAgentIdsAsync(ct);
        },
        LoadLookupItems: static async (sp, ct) =>
        {
            var ids = sp.GetRequiredService<ICoreEntityIdProvider>();
            return await ids.GetAgentLookupItemsAsync(ct);
        },
        DefaultResourceKey: "agent"),
        new("AoTask", "EditTask", "EditTaskAsync", static async (sp, ct) =>
        {
            var db = sp.GetRequiredService<AgentOrchestrationDbContext>();
            return await db.ScheduledJobs.Select(t => t.Id).ToListAsync(ct);
        },
        LoadLookupItems: static async (sp, ct) =>
        {
            var db = sp.GetRequiredService<AgentOrchestrationDbContext>();
            return await db.ScheduledJobs.Select(t => new ValueTuple<Guid, string>(t.Id, t.Name)).ToListAsync(ct);
        },
        DefaultResourceKey: "task"),
        new("AoSkill", "AccessSkill", "AccessSkillAsync", static async (sp, ct) =>
        {
            var db = sp.GetRequiredService<AgentOrchestrationDbContext>();
            return await db.Skills.Select(s => s.Id).ToListAsync(ct);
        },
        LoadLookupItems: static async (sp, ct) =>
        {
            var db = sp.GetRequiredService<AgentOrchestrationDbContext>();
            return await db.Skills.Select(s => new ValueTuple<Guid, string>(s.Id, s.Name)).ToListAsync(ct);
        },
        DefaultResourceKey: "skill"),
        new("AoAgentHeader", "EditAgentHeader", "EditAgentHeaderAsync", static async (sp, ct) =>
        {
            var ids = sp.GetRequiredService<ICoreEntityIdProvider>();
            return await ids.GetAgentIdsAsync(ct);
        },
        LoadLookupItems: static async (sp, ct) =>
        {
            var ids = sp.GetRequiredService<ICoreEntityIdProvider>();
            return await ids.GetAgentLookupItemsAsync(ct);
        }),
        new("AoChannelHeader", "EditChannelHeader", "EditChannelHeaderAsync", static async (sp, ct) =>
        {
            var ids = sp.GetRequiredService<ICoreEntityIdProvider>();
            return await ids.GetChannelIdsAsync(ct);
        },
        LoadLookupItems: static async (sp, ct) =>
        {
            var ids = sp.GetRequiredService<ICoreEntityIdProvider>();
            return await ids.GetChannelLookupItemsAsync(ct);
        }),
    ];

    // ═══════════════════════════════════════════════════════════════
    // CLI Commands
    // ═══════════════════════════════════════════════════════════════

    public IReadOnlyList<ModuleCliCommand> GetCliCommands() =>
    [
        new(
            Name: "schedule",
            Aliases: [],
            Scope: ModuleCliScope.TopLevel,
            Description: "Manage cron scheduled jobs",
            UsageLines:
            [
                "schedule list                             List all scheduled jobs",
                "schedule get <jobId>                      Show a scheduled job",
                "schedule create <taskId> --cron <expr> [--timezone <tz>] [--name <n>]",
                "                                          Create a cron scheduled job",
                "schedule update <jobId> --cron <expr> [--timezone <tz>]",
                "                                          Update cron expression / timezone",
                "schedule pause <jobId>                    Pause a scheduled job",
                "schedule resume <jobId>                   Resume a paused job",
                "schedule delete <jobId>                   Delete a scheduled job",
                "schedule preview <expr> [--timezone <tz>] [--count N]",
                "                                          Preview next occurrences of a cron expression",
            ],
            Handler: HandleScheduleCommandAsync),
        new(
            Name: "aotask",
            Aliases: ["aot"],
            Scope: ModuleCliScope.ResourceType,
            Description: "Agent Orchestration scheduled task management",
            UsageLines:
            [
                "resource aotask add <name> [--next-run <timestamp>] [--repeat-minutes <n>] [--max-retries <n>]",
                "resource aotask get <id>                         Show an AO task",
                "resource aotask list                             List AO tasks",
                "resource aotask update <id> [--name <name>] [--repeat-minutes <n>] [--max-retries <n>]",
                "resource aotask delete <id>                      Delete an AO task",
            ],
            Handler: HandleResourceAoTaskCommandAsync),
        new(
            Name: "aoskill",
            Aliases: ["aos"],
            Scope: ModuleCliScope.ResourceType,
            Description: "Agent Orchestration skill management",
            UsageLines:
            [
                "resource aoskill add <name> --text <skillText> [--description <description>]",
                "resource aoskill get <id>                        Show an AO skill",
                "resource aoskill list                            List AO skills",
                "resource aoskill update <id> [--name <name>] [--description <description>] [--text <skillText>]",
                "resource aoskill delete <id>                     Delete an AO skill",
            ],
            Handler: HandleResourceAoSkillCommandAsync),
    ];

    private static async Task HandleScheduleCommandAsync(
        string[] args, IServiceProvider sp, CancellationToken ct)
    {
        // args[0] = "schedule" (top-level), args[1] = sub-command.
        // When forwarded from the legacy "task schedule …" alias the host
        // dispatcher rewrites args so args[0] is still "schedule".
        var ids = sp.GetRequiredService<ICliIdResolver>();
        var svc = sp.GetRequiredService<IScheduledJobService>();

        if (args.Length < 2)
        {
            PrintScheduleUsage();
            return;
        }

        var sub = args[1].ToLowerInvariant();
        switch (sub)
        {
            case "list":
                ids.PrintJson(await svc.ListAsync(ct));
                break;

            case "get" when args.Length >= 3:
            {
                var job = await svc.GetByIdAsync(ids.Resolve(args[2]), ct);
                if (job is not null) ids.PrintJson(job);
                else Console.Error.WriteLine("Not found.");
                break;
            }
            case "get":
                Console.Error.WriteLine("schedule get <jobId>");
                break;

            case "create":
            {
                var flags = ParseFlags(args, 2);
                if (!flags.TryGetValue("cron", out var cronExpr) || string.IsNullOrWhiteSpace(cronExpr))
                {
                    Console.Error.WriteLine("schedule create <taskId> --cron <expr> [--timezone <tz>] [--name <n>]");
                    break;
                }

                Guid? taskId = args.Length >= 3 && Guid.TryParse(args[2], out var tid) ? tid : null;
                flags.TryGetValue("timezone", out var tz);
                flags.TryGetValue("name", out var name);

                try
                {
                    var result = await svc.CreateAsync(new CreateScheduledJobRequest(
                        Name: name ?? cronExpr,
                        TaskDefinitionId: taskId,
                        CronExpression: cronExpr,
                        CronTimezone: tz), ct);
                    ids.PrintJson(result);
                }
                catch (InvalidOperationException ex)
                {
                    Console.Error.WriteLine(ex.Message);
                }
                break;
            }

            case "update" when args.Length >= 3:
            {
                var jobId = ids.Resolve(args[2]);
                var flags = ParseFlags(args, 3);
                flags.TryGetValue("cron", out var cronExpr);
                flags.TryGetValue("timezone", out var tz);

                try
                {
                    var result = await svc.UpdateAsync(jobId, new UpdateScheduledJobRequest(
                        CronExpression: cronExpr,
                        CronTimezone: tz), ct);
                    if (result is not null) ids.PrintJson(result);
                    else Console.Error.WriteLine("Not found.");
                }
                catch (InvalidOperationException ex)
                {
                    Console.Error.WriteLine(ex.Message);
                }
                break;
            }
            case "update":
                Console.Error.WriteLine("schedule update <jobId> --cron <expr> [--timezone <tz>]");
                break;

            case "pause" when args.Length >= 3:
            {
                var result = await svc.PauseAsync(ids.Resolve(args[2]), ct);
                if (result is not null) ids.PrintJson(result);
                else Console.Error.WriteLine("Not found.");
                break;
            }
            case "pause":
                Console.Error.WriteLine("schedule pause <jobId>");
                break;

            case "resume" when args.Length >= 3:
            {
                var result = await svc.ResumeAsync(ids.Resolve(args[2]), ct);
                if (result is not null) ids.PrintJson(result);
                else Console.Error.WriteLine("Not found.");
                break;
            }
            case "resume":
                Console.Error.WriteLine("schedule resume <jobId>");
                break;

            case "delete" when args.Length >= 3:
            {
                var ok = await svc.DeleteAsync(ids.Resolve(args[2]), ct);
                Console.WriteLine(ok ? "Done." : "Not found.");
                break;
            }
            case "delete":
                Console.Error.WriteLine("schedule delete <jobId>");
                break;

            case "preview" when args.Length >= 3:
            {
                var expr = args[2];
                var flags = ParseFlags(args, 3);
                flags.TryGetValue("timezone", out var tz);
                var count = flags.TryGetValue("count", out var cStr) && int.TryParse(cStr, out var c) ? c : 10;
                count = count <= 0 ? 10 : Math.Min(count, 100);
                try
                {
                    ids.PrintJson(svc.PreviewExpression(expr, tz, count));
                }
                catch (InvalidOperationException ex)
                {
                    Console.Error.WriteLine(ex.Message);
                }
                break;
            }
            case "preview":
                Console.Error.WriteLine("schedule preview <expr> [--timezone <tz>] [--count N]");
                break;

            default:
                Console.Error.WriteLine($"Unknown command: schedule {sub}");
                PrintScheduleUsage();
                break;
        }
    }

    private static void PrintScheduleUsage()
    {
        Console.Error.WriteLine("Usage:");
        Console.Error.WriteLine("  schedule list                             List all scheduled jobs");
        Console.Error.WriteLine("  schedule get <jobId>                      Show a scheduled job");
        Console.Error.WriteLine("  schedule create <taskId> --cron <expr> [--timezone <tz>] [--name <n>]");
        Console.Error.WriteLine("  schedule update <jobId> --cron <expr> [--timezone <tz>]");
        Console.Error.WriteLine("  schedule pause <jobId>                    Pause a scheduled job");
        Console.Error.WriteLine("  schedule resume <jobId>                   Resume a paused job");
        Console.Error.WriteLine("  schedule delete <jobId>                   Delete a scheduled job");
        Console.Error.WriteLine("  schedule preview <expr> [--timezone <tz>] [--count N]");
    }

    private static async Task HandleResourceAoTaskCommandAsync(
        string[] args, IServiceProvider sp, CancellationToken ct)
    {
        var ids = sp.GetRequiredService<ICliIdResolver>();
        var db = sp.GetRequiredService<AgentOrchestrationDbContext>();

        if (args.Length < 3)
        {
            PrintAoTaskUsage();
            return;
        }

        var sub = args[2].ToLowerInvariant();
        switch (sub)
        {
            case "add" when args.Length >= 4:
            {
                var flags = ParseFlags(args, 4);
                var task = new Models.ScheduledJobDB
                {
                    Id = Guid.NewGuid(),
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow,
                    Name = args[3],
                    NextRunAt = ParseDateTimeOffset(flags, "next-run") ?? DateTimeOffset.UtcNow,
                    RepeatInterval = ParsePositiveMinutes(flags, "repeat-minutes"),
                    MaxRetries = ParseInt(flags, "max-retries") ?? 3,
                };

                db.ScheduledJobs.Add(task);
                await db.SaveChangesAsync(ct);
                ids.PrintJson(ToAoTaskDto(task));
                break;
            }
            case "add":
                Console.Error.WriteLine("resource aotask add <name> [--next-run <timestamp>] [--repeat-minutes <n>] [--max-retries <n>]");
                break;

            case "get" when args.Length >= 4:
            {
                var task = await db.ScheduledJobs.FirstOrDefaultAsync(t => t.Id == ids.Resolve(args[3]), ct);
                if (task is not null)
                    ids.PrintJson(ToAoTaskDto(task));
                else
                    Console.Error.WriteLine("Not found.");
                break;
            }
            case "get":
                Console.Error.WriteLine("resource aotask get <id>");
                break;

            case "list":
            {
                var tasks = await db.ScheduledJobs.OrderBy(t => t.Name).ToListAsync(ct);
                ids.PrintJson(tasks.Select(ToAoTaskDto).ToList());
                break;
            }

            case "update" when args.Length >= 4:
            {
                var task = await db.ScheduledJobs.FirstOrDefaultAsync(t => t.Id == ids.Resolve(args[3]), ct);
                if (task is null)
                {
                    Console.Error.WriteLine("Not found.");
                    break;
                }

                var flags = ParseFlags(args, 4);
                if (flags.TryGetValue("name", out var name) && !string.IsNullOrWhiteSpace(name))
                    task.Name = name;
                if (flags.TryGetValue("repeat-minutes", out _))
                    task.RepeatInterval = ParsePositiveMinutes(flags, "repeat-minutes");
                if (flags.TryGetValue("max-retries", out _))
                    task.MaxRetries = ParseInt(flags, "max-retries") ?? task.MaxRetries;
                if (flags.TryGetValue("next-run", out _))
                    task.NextRunAt = ParseDateTimeOffset(flags, "next-run") ?? task.NextRunAt;

                task.UpdatedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(ct);
                ids.PrintJson(ToAoTaskDto(task));
                break;
            }
            case "update":
                Console.Error.WriteLine("resource aotask update <id> [--name <name>] [--repeat-minutes <n>] [--max-retries <n>]");
                break;

            case "delete" when args.Length >= 4:
            {
                var task = await db.ScheduledJobs.FirstOrDefaultAsync(t => t.Id == ids.Resolve(args[3]), ct);
                if (task is null)
                {
                    Console.WriteLine("Not found.");
                    break;
                }

                db.ScheduledJobs.Remove(task);
                await db.SaveChangesAsync(ct);
                Console.WriteLine("Done.");
                break;
            }
            case "delete":
                Console.Error.WriteLine("resource aotask delete <id>");
                break;

            default:
                Console.Error.WriteLine($"Unknown command: resource aotask {sub}");
                PrintAoTaskUsage();
                break;
        }
    }

    private static async Task HandleResourceAoSkillCommandAsync(
        string[] args, IServiceProvider sp, CancellationToken ct)
    {
        var ids = sp.GetRequiredService<ICliIdResolver>();
        var db = sp.GetRequiredService<AgentOrchestrationDbContext>();

        if (args.Length < 3)
        {
            PrintAoSkillUsage();
            return;
        }

        var sub = args[2].ToLowerInvariant();
        switch (sub)
        {
            case "add" when args.Length >= 4:
            {
                var flags = ParseFlags(args, 4);
                if (!flags.TryGetValue("text", out var skillText) || string.IsNullOrWhiteSpace(skillText))
                {
                    Console.Error.WriteLine("resource aoskill add requires --text <skillText>.");
                    break;
                }

                flags.TryGetValue("description", out var description);
                var skill = new Models.SkillDB
                {
                    Id = Guid.NewGuid(),
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow,
                    Name = args[3],
                    Description = description,
                    SkillText = skillText,
                };

                db.Skills.Add(skill);
                await db.SaveChangesAsync(ct);
                ids.PrintJson(ToAoSkillDto(skill));
                break;
            }
            case "add":
                Console.Error.WriteLine("resource aoskill add <name> --text <skillText> [--description <description>]");
                break;

            case "get" when args.Length >= 4:
            {
                var skill = await db.Skills.FirstOrDefaultAsync(s => s.Id == ids.Resolve(args[3]), ct);
                if (skill is not null)
                    ids.PrintJson(ToAoSkillDto(skill));
                else
                    Console.Error.WriteLine("Not found.");
                break;
            }
            case "get":
                Console.Error.WriteLine("resource aoskill get <id>");
                break;

            case "list":
            {
                var skills = await db.Skills.OrderBy(s => s.Name).ToListAsync(ct);
                ids.PrintJson(skills.Select(ToAoSkillDto).ToList());
                break;
            }

            case "update" when args.Length >= 4:
            {
                var skill = await db.Skills.FirstOrDefaultAsync(s => s.Id == ids.Resolve(args[3]), ct);
                if (skill is null)
                {
                    Console.Error.WriteLine("Not found.");
                    break;
                }

                var flags = ParseFlags(args, 4);
                if (flags.TryGetValue("name", out var name) && !string.IsNullOrWhiteSpace(name))
                    skill.Name = name;
                if (flags.TryGetValue("description", out var description))
                    skill.Description = description;
                if (flags.TryGetValue("text", out var text) && !string.IsNullOrWhiteSpace(text))
                    skill.SkillText = text;

                skill.UpdatedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(ct);
                ids.PrintJson(ToAoSkillDto(skill));
                break;
            }
            case "update":
                Console.Error.WriteLine("resource aoskill update <id> [--name <name>] [--description <description>] [--text <skillText>]");
                break;

            case "delete" when args.Length >= 4:
            {
                var skill = await db.Skills.FirstOrDefaultAsync(s => s.Id == ids.Resolve(args[3]), ct);
                if (skill is null)
                {
                    Console.WriteLine("Not found.");
                    break;
                }

                db.Skills.Remove(skill);
                await db.SaveChangesAsync(ct);
                Console.WriteLine("Done.");
                break;
            }
            case "delete":
                Console.Error.WriteLine("resource aoskill delete <id>");
                break;

            default:
                Console.Error.WriteLine($"Unknown command: resource aoskill {sub}");
                PrintAoSkillUsage();
                break;
        }
    }

    private static Dictionary<string, string> ParseFlags(string[] args, int start)
    {
        var flags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        for (var i = start; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal))
                continue;

            var key = args[i][2..];
            if (string.IsNullOrWhiteSpace(key))
                continue;

            flags[key] = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal)
                ? args[++i]
                : string.Empty;
        }

        return flags;
    }

    private static int? ParseInt(IReadOnlyDictionary<string, string> flags, string key)
        => flags.TryGetValue(key, out var value) && int.TryParse(value, out var parsed)
            ? parsed
            : null;

    private static TimeSpan? ParsePositiveMinutes(IReadOnlyDictionary<string, string> flags, string key)
    {
        var minutes = ParseInt(flags, key);
        return minutes is > 0 ? TimeSpan.FromMinutes(minutes.Value) : null;
    }

    private static DateTimeOffset? ParseDateTimeOffset(IReadOnlyDictionary<string, string> flags, string key)
        => flags.TryGetValue(key, out var value)
            && DateTimeOffset.TryParse(value, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed)
                ? parsed
                : null;

    private static object ToAoTaskDto(Models.ScheduledJobDB task) => new
    {
        task.Id,
        task.Name,
        task.NextRunAt,
        RepeatIntervalMinutes = task.RepeatInterval is null ? null : (int?)task.RepeatInterval.Value.TotalMinutes,
        task.MaxRetries,
        task.RetryCount,
        task.Status,
        task.LastRunAt,
        task.LastError,
        task.TaskDefinitionId,
        task.CallerAgentId,
        task.AgentContextId,
        task.PermissionSetId,
        task.CronExpression,
        task.CronTimezone,
        task.MissedFirePolicy,
        task.CreatedAt,
        task.UpdatedAt,
    };

    private static object ToAoSkillDto(Models.SkillDB skill) => new
    {
        skill.Id,
        skill.Name,
        skill.Description,
        skill.SkillText,
        skill.CreatedAt,
        skill.UpdatedAt,
    };

    private static void PrintAoTaskUsage()
    {
        Console.Error.WriteLine("Usage:");
        Console.Error.WriteLine("  resource aotask add <name> [--next-run <timestamp>] [--repeat-minutes <n>] [--max-retries <n>]");
        Console.Error.WriteLine("  resource aotask get <id>                         Show an AO task");
        Console.Error.WriteLine("  resource aotask list                             List AO tasks");
        Console.Error.WriteLine("  resource aotask update <id> [--name <name>] [--repeat-minutes <n>] [--max-retries <n>]");
        Console.Error.WriteLine("  resource aotask delete <id>                      Delete an AO task");
    }

    private static void PrintAoSkillUsage()
    {
        Console.Error.WriteLine("Usage:");
        Console.Error.WriteLine("  resource aoskill add <name> --text <skillText> [--description <description>]");
        Console.Error.WriteLine("  resource aoskill get <id>                        Show an AO skill");
        Console.Error.WriteLine("  resource aoskill list                            List AO skills");
        Console.Error.WriteLine("  resource aoskill update <id> [--name <name>] [--description <description>] [--text <skillText>]");
        Console.Error.WriteLine("  resource aoskill delete <id>                     Delete an AO skill");
    }

    // ═══════════════════════════════════════════════════════════════
    // Global Flag Descriptors
    // ═══════════════════════════════════════════════════════════════

    public IReadOnlyList<ModuleGlobalFlagDescriptor> GetGlobalFlagDescriptors() =>
    [
        new("CanCreateSubAgents", "Create Sub-Agents", "Create sub-agents with permissions ≤ the creator's.", "CreateSubAgentAsync"),
        new("CanEditAgentHeader", "Edit Agent Header", "Edit the custom chat header of specific agents.", "CanEditAgentHeaderAsync"),
        new("CanEditChannelHeader", "Edit Channel Header", "Edit the custom chat header of specific channels.", "CanEditChannelHeaderAsync"),
        new(AgentOrchestrationPermissionKeys.CanInvokeTasksAsTool, "Invoke Tasks As Tool",
            "Expose active task definitions in the agent tool list.", "InvokeTaskAsToolAsync"),
    ];

    // ═══════════════════════════════════════════════════════════════
    // Tool Definitions
    // ═══════════════════════════════════════════════════════════════

    public IReadOnlyList<ModuleToolDefinition> GetToolDefinitions()
    {
        var globalNoResource = new ModuleToolPermission(
            IsPerResource: false, Check: null,
            DelegateTo: "CreateSubAgentAsync");

        var perResourceManageAgent = new ModuleToolPermission(
            IsPerResource: true, Check: null,
            DelegateTo: "ManageAgentAsync");

        var perResourceEditTask = new ModuleToolPermission(
            IsPerResource: true, Check: null,
            DelegateTo: "EditTaskAsync");

        var perResourceAccessSkill = new ModuleToolPermission(
            IsPerResource: true, Check: null,
            DelegateTo: "AccessSkillAsync");

        var perResourceEditAgentHeader = new ModuleToolPermission(
            IsPerResource: true, Check: null,
            DelegateTo: "EditAgentHeaderAsync");

        var perResourceEditChannelHeader = new ModuleToolPermission(
            IsPerResource: true, Check: null,
            DelegateTo: "EditChannelHeaderAsync");

        return
        [
            new("create_sub_agent",
                "Create a sub-agent (name, modelId, optional systemPrompt).",
                BuildCreateSubAgentSchema(), globalNoResource),

            new("ao_manage_agent",
                "Update agent name, systemPrompt, or modelId.",
                BuildManageAgentSchema(), perResourceManageAgent,
                Aliases: ["manage_agent"]),

            new("ao_edit_task",
                "Edit task name, interval, or retries.",
                BuildEditTaskSchema(), perResourceEditTask,
                Aliases: ["edit_task"]),

            new("ao_access_skill",
                "Retrieve a skill's instruction text.",
                BuildResourceOnlySchema(), perResourceAccessSkill,
                Aliases: ["access_skill"]),

            new("ao_edit_agent_header",
                "Set or clear the custom chat header for an agent.",
                BuildHeaderSchema(), perResourceEditAgentHeader,
                Aliases: ["edit_agent_header"]),

            new("ao_edit_channel_header",
                "Set or clear the custom chat header for a channel.",
                BuildHeaderSchema(), perResourceEditChannelHeader,
                Aliases: ["edit_channel_header"]),
        ];
    }

    // ═══════════════════════════════════════════════════════════════
    // Tool Execution
    // ═══════════════════════════════════════════════════════════════

    public async Task<string> ExecuteToolAsync(
        string toolName, JsonElement parameters, AgentJobContext job,
        IServiceProvider sp, CancellationToken ct)
    {
        var svc = sp.GetRequiredService<AgentOrchestrationService>();

        return toolName switch
        {
            "create_sub_agent"
                => await svc.CreateSubAgentAsync(parameters, ct),

            "ao_manage_agent" or "manage_agent"
                => await svc.ManageAgentAsync(
                    job.ResourceId ?? throw new InvalidOperationException(
                        "manage_agent requires a ResourceId (target agent)."),
                    parameters, ct),

            "ao_edit_task" or "edit_task"
                => await svc.EditTaskAsync(
                    job.ResourceId ?? throw new InvalidOperationException(
                        "edit_task requires a ResourceId (target task)."),
                    parameters, ct),

            "ao_access_skill" or "access_skill"
                => await svc.AccessSkillAsync(
                    job.ResourceId ?? throw new InvalidOperationException(
                        "access_skill requires a ResourceId (target skill)."),
                    ct),

            "ao_edit_agent_header" or "edit_agent_header"
                => await svc.EditAgentHeaderAsync(
                    job.ResourceId ?? throw new InvalidOperationException(
                        "edit_agent_header requires a ResourceId (target agent)."),
                    parameters, ct),

            "ao_edit_channel_header" or "edit_channel_header"
                => await svc.EditChannelHeaderAsync(
                    job.ResourceId ?? throw new InvalidOperationException(
                        "edit_channel_header requires a ResourceId (target channel)."),
                    parameters, ct),

            _ => throw new InvalidOperationException(
                $"Unknown Agent Orchestration tool: '{toolName}'."),
        };
    }

    // ═══════════════════════════════════════════════════════════════
    // Lifecycle
    // ═══════════════════════════════════════════════════════════════

    private ScheduledJobWorker? _scheduledJobWorker;

    public Task InitializeAsync(IServiceProvider services, CancellationToken ct)
    {
        _scheduledJobWorker = services.GetService<ScheduledJobWorker>();
        _scheduledJobWorker?.Start();
        return Task.CompletedTask;
    }

    public async Task ShutdownAsync()
    {
        if (_scheduledJobWorker is not null)
            await _scheduledJobWorker.StopAsync();
    }

    // ═══════════════════════════════════════════════════════════════
    // Endpoint Mapping
    // ═══════════════════════════════════════════════════════════════

    public void MapEndpoints(object app)
    {
        var endpoints = (Microsoft.AspNetCore.Routing.IEndpointRouteBuilder)app;
        Handlers.ScheduledJobEndpoints.MapScheduledJobEndpoints(endpoints);
    }

    // ═══════════════════════════════════════════════════════════════
    // Schema builders
    // ═══════════════════════════════════════════════════════════════

    private static JsonElement BuildCreateSubAgentSchema()
    {
        using var doc = JsonDocument.Parse("""
            {
                "type": "object",
                "properties": {
                    "name": {
                        "type": "string",
                        "description": "Agent name."
                    },
                    "modelId": {
                        "type": "string",
                        "description": "Model GUID."
                    },
                    "systemPrompt": {
                        "type": "string",
                        "description": "System prompt."
                    }
                },
                "required": ["name", "modelId"]
            }
            """);
        return doc.RootElement.Clone();
    }

    private static JsonElement BuildManageAgentSchema()
    {
        using var doc = JsonDocument.Parse("""
            {
                "type": "object",
                "properties": {
                    "resource_id": {
                        "type": "string",
                        "description": "Agent GUID."
                    },
                    "name": {
                        "type": "string",
                        "description": "New name."
                    },
                    "systemPrompt": {
                        "type": "string",
                        "description": "New system prompt."
                    },
                    "modelId": {
                        "type": "string",
                        "description": "New model GUID."
                    }
                },
                "required": ["resource_id"]
            }
            """);
        return doc.RootElement.Clone();
    }

    private static JsonElement BuildEditTaskSchema()
    {
        using var doc = JsonDocument.Parse("""
            {
                "type": "object",
                "properties": {
                    "resource_id": {
                        "type": "string",
                        "description": "Task GUID."
                    },
                    "name": {
                        "type": "string",
                        "description": "New name."
                    },
                    "repeatIntervalMinutes": {
                        "type": "integer",
                        "description": "Minutes. 0=remove."
                    },
                    "maxRetries": {
                        "type": "integer",
                        "description": "Max retries."
                    }
                },
                "required": ["resource_id"]
            }
            """);
        return doc.RootElement.Clone();
    }

    private static JsonElement BuildResourceOnlySchema()
    {
        using var doc = JsonDocument.Parse("""
            {
                "type": "object",
                "properties": {
                    "resource_id": {
                        "type": "string",
                        "description": "Resource GUID."
                    }
                },
                "required": ["resource_id"]
            }
            """);
        return doc.RootElement.Clone();
    }

    private static JsonElement BuildHeaderSchema()
    {
        using var doc = JsonDocument.Parse("""
            {
                "type": "object",
                "properties": {
                    "resource_id": {
                        "type": "string",
                        "description": "Target agent or channel GUID."
                    },
                    "header": {
                        "type": "string",
                        "description": "Header template text. Empty or null clears the custom header."
                    }
                },
                "required": ["resource_id"]
            }
            """);
        return doc.RootElement.Clone();
    }
}
