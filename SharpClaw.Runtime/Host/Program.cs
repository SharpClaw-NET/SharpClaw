using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using SharpClaw.Runtime.Host;
using SharpClaw.Runtime.Host.Api;
using SharpClaw.Runtime.Host.Cli;
using SharpClaw.Runtime.Host.Handlers;
using SharpClaw.Runtime.Host.Routing;
using SharpClaw.Runtime.Host.Webhooks;
using SharpClaw.Core.Clients;
using SharpClaw.Runtime.BLL.Modules;
using SharpClaw.Runtime.BLL.Modules.Foreign;
using SharpClaw.Core.Tasks;
using SharpClaw.Core.Tasks.Triggers;
using SharpClaw.Runtime.BLL.Services.Triggers;
using SharpClaw.Runtime.BLL.Services;
using SharpClaw.Runtime.INF.Logging;
using SharpClaw.Core.Tasks.Parsing;
using SharpClaw.Core.Tasks.Registry;
using SharpClaw.Runtime.BLL.Services.Auth;
using SharpClaw.Contracts.Chat;
using SharpClaw.Contracts.Modules;
using SharpClaw.Contracts.Persistence;
using SharpClaw.Contracts.Providers;
using SharpClaw.Runtime.INF;
using SharpClaw.Runtime.INF.Configuration;
using SharpClaw.Runtime.INF.Persistence;
using SharpClaw.Shared.Logging;
using SharpClaw.Shared.Instances;
using Microsoft.EntityFrameworkCore;
using SharpClaw.Shared.Security;
using Serilog.Events;
using SharpClaw.Contracts.Permissions;
using SharpClaw.Contracts.Tasks;
using SharpClaw.Core.Agents;
using SharpClaw.Core.Modules;
using SharpClaw.Core.Modules.Foreign;
using SharpClaw.Contracts.Modules.Foreign;
using SharpClaw.Core.Chat;
using SharpClaw.Core.Conversation;
using SharpClaw.Core.Permissions;
using SharpClaw.Core.Jobs;
using SharpClaw.Core.Providers;
using SharpClaw.Core.Resources;
using SharpClaw.Core.Threads;
using SharpClaw.Core.Tools;
using SharpClaw.Core.Tasks.Administration;
using SharpClaw.Core.Tasks.Preflight;
using SharpClaw.Core.Tasks.Runtime;

// ════════════════════════════════════════════════════════════════════════════
//  SharpClaw API host — composition root
// ════════════════════════════════════════════════════════════════════════════
//
//  This file is the single startup entrypoint for the backend.  It runs in
//  three flavours from the same binary:
//
//    1. Long-running API server (default)         → API mode block
//    2. One-shot CLI command                      → CliDispatcher.TryHandleAsync
//    3. Interactive REPL                          → CliDispatcher.RunInteractiveAsync
//
//  The startup is intentionally chronological — every later phase reads
//  state produced by an earlier one — so resist the temptation to extract
//  helpers that hide the ordering.  The phases are:
//
//    PHASE 1  Instance discovery + filesystem layout         (per-process paths,
//             early config, exception capture, Serilog).
//    PHASE 2  WebApplication builder + URL binding.
//    PHASE 3  Configuration + Serilog wire-up + per-instance singletons
//             (SessionLogWriter, instance paths, instance lock).
//    PHASE 4  Module log capture (ILoggerProvider feeding /modules/{id}/logs).
//    PHASE 5  Encryption key resolution + validation
//             (must run before infrastructure, which uses it for at-rest enc).
//    PHASE 6  Infrastructure persistence (DbContext + configured EF provider).
//    PHASE 7  Cross-cutting middleware-shaped services (CORS, auth/JWT).
//    PHASE 8  Domain services (chat, agents, channels, threads, tasks,
//             permissions, env editor, …) and host-side module bridges.
//    PHASE 9  Task runtime + trigger host + host metric probes.
//    PHASE 10 Module system services (registry, dispatcher, health checks,
//             config store, execution context).
//    PHASE 11 Bundled-module discovery + per-module ConfigureServices.
//             ⚠ All modules — enabled OR disabled — must register their DI
//             services here, because the container is sealed at Build().
//    PHASE 12 Misc post-module singletons (DB init gate, seeding, API key,
//             CLI short-id resolver).
//    PHASE 13 builder.Build() — container is now immutable.
//    PHASE 14 Post-build infrastructure init + relational migration check.
//    PHASE 15 Module enable-state sync from config + per-module persistence
//             registration.
//    PHASE 16 Module initialisation in dependency order with cascade
//             unregistration on failure.
//    PHASE 17 External-module scan (filesystem + .env entries).
//    PHASE 18 Reserved for provider lifecycle hooks.
//    PHASE 19 CLI command dispatch — exits the process if a CLI verb matches.
//    PHASE 20 HTTP pipeline (middleware order is load-bearing — see comments).
//    PHASE 21 Endpoint mapping (handlers, modules, webhooks).
//    PHASE 22 Shutdown registrations (api key cleanup, module shutdown).
//    PHASE 23 StartAsync + discovery publication.
//    PHASE 24 Interactive REPL (suppresses console logging when attached to
//             a real TTY; stays verbose in headless mode).
//
//  Read the banner comments below for the per-phase contract.  When adding
//  a new service, find the phase whose theme matches and slot it in there
//  rather than appending to the bottom of phase 8.
// ════════════════════════════════════════════════════════════════════════════

// ──────── PHASE 1 ──── Instance discovery + filesystem layout ──────────────
// Resolve per-instance data/log directories before anything else so that
// crash diagnostics, the instance lock, and Serilog all agree on paths.

var dataDir = Environment.GetEnvironmentVariable("SHARPCLAW_DATA_DIR");
var instanceRoot = Environment.GetEnvironmentVariable("SHARPCLAW_INSTANCE_ROOT");
if (string.IsNullOrWhiteSpace(instanceRoot) && !string.IsNullOrWhiteSpace(dataDir))
    instanceRoot = Path.GetDirectoryName(Path.GetFullPath(dataDir));

var backendInstancePaths = new SharpClawInstancePaths(
    SharpClawInstanceKind.Backend,
    instanceRoot);
backendInstancePaths.EnsureDirectories();
backendInstancePaths.CleanupStaleDiscoveryEntries(TimeSpan.FromMinutes(2));
using var backendInstanceLock = new SharpClawInstanceLock(backendInstancePaths);

await using var sessionLogs = new SessionLogWriter("core", backendInstancePaths.LogsDirectory);
using var sessionLogCapture = SessionLogCapture.Install(sessionLogs);

// Early configuration is read once, *before* the WebApplication builder
// exists, so that Serilog can be configured against .env values during
// the bootstrap window.  The "real" configuration is rebuilt below on
// builder.Configuration; the two sources are reconciled by reading the
// same .env files.
var earlyConfiguration = new ConfigurationBuilder()
    .AddLocalEnvironment(isDevelopment: false)
    .Build();

var serilogOptions = SerilogEnvironmentOptions.FromConfiguration(earlyConfiguration);

AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
{
    if (eventArgs.ExceptionObject is Exception exception)
        sessionLogs.AppendException(exception, "Unhandled AppDomain exception in core.");
    else
        sessionLogs.AppendException($"Unhandled AppDomain exception payload: {eventArgs.ExceptionObject}");
};

TaskScheduler.UnobservedTaskException += (_, eventArgs) =>
{
    sessionLogs.AppendException(eventArgs.Exception, "Unobserved task exception in core.");
};

var consoleLevelSwitch = new Serilog.Core.LoggingLevelSwitch(LogEventLevel.Information);

// Serilog configuration.  When disabled in .env we still install a
// minimum-level Fatal logger so accidental Log.X calls don't NRE.
if (serilogOptions.Enabled)
{
    var loggerConfiguration = new LoggerConfiguration()
        .MinimumLevel.Is(SerilogEnvironmentOptions.ParseEnum(
            serilogOptions.MinimumLevel,
            LogEventLevel.Information))
        .MinimumLevel.Override("Microsoft", SerilogEnvironmentOptions.ParseEnum(
            serilogOptions.MicrosoftMinimumLevel,
            LogEventLevel.Warning))
        .MinimumLevel.Override("Microsoft.AspNetCore", SerilogEnvironmentOptions.ParseEnum(
            serilogOptions.AspNetCoreMinimumLevel,
            LogEventLevel.Warning))
        .MinimumLevel.Override("Microsoft.EntityFrameworkCore", SerilogEnvironmentOptions.ParseEnum(
            serilogOptions.EntityFrameworkCoreMinimumLevel,
            LogEventLevel.Warning))
        .Enrich.FromLogContext()
        .WriteTo.Sink(new SessionLogSerilogSink(sessionLogs));

    if (serilogOptions.ConsoleEnabled)
        loggerConfiguration = loggerConfiguration.WriteTo.Console(levelSwitch: consoleLevelSwitch);

    if (serilogOptions.FileEnabled)
        loggerConfiguration = loggerConfiguration.WriteTo.File(
            sessionLogs.SerilogFilePath,
            rollingInterval: RollingInterval.Infinite);

    Log.Logger = loggerConfiguration.CreateLogger();
}
else
{
    consoleLevelSwitch.MinimumLevel = LogEventLevel.Fatal;
    Log.Logger = new LoggerConfiguration()
        .MinimumLevel.Fatal()
        .CreateLogger();
}

try
{
    var builder = WebApplication.CreateBuilder(args);

    var backendManifest = backendInstancePaths.Manifest;

    // Ensure the API always binds to the expected port, regardless of
    // whether a launch profile is active.  ASPNETCORE_URLS env var
    // (set by BackendProcessManager) takes precedence if present.
    if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ASPNETCORE_URLS")))
        builder.WebHost.UseUrls(backendManifest.BaseUrl ?? "http://127.0.0.1:48923");

    // Configuration: environment files
    builder.Configuration.AddLocalEnvironment(builder.Environment.IsDevelopment());

    builder.Host.UseSerilog();
    builder.Logging.AddProvider(new SessionLogLoggerProvider(sessionLogs));

    builder.Services.AddSingleton(sessionLogs);
    builder.Services.AddSingleton(backendInstancePaths);
    builder.Services.AddSingleton(backendInstanceLock);

    // Module log capture — feeds per-module ring buffers for the /modules/{id}/logs API.
    var moduleLogService = new ModuleLogService();
    builder.Services.AddSingleton(moduleLogService);
    builder.Services.AddSingleton<Microsoft.Extensions.Logging.ILoggerProvider>(
        new ModuleLogSinkProvider(moduleLogService));

    // Encryption key — resolved early so Infrastructure can use it for JSON file encryption.
    var encryptionKeyBase64 = builder.Configuration["Encryption:Key"]
        ?? PersistentKeyStore.GetOrCreate("encryption-key", backendInstancePaths);
    byte[] encryptionKey;
    try
    {
        encryptionKey = Convert.FromBase64String(encryptionKeyBase64);
    }
    catch (FormatException ex)
    {
        throw new InvalidOperationException(
            "Encryption:Key is not valid Base64. " +
            "Remove the key from .env to auto-generate a new one, or provide a valid 256-bit Base64 key.",
            ex);
    }

    if (encryptionKey.Length != 32)
    {
        throw new InvalidOperationException(
            $"Encryption:Key must be exactly 256 bits (32 bytes) after Base64 decoding. " +
            $"Got {encryptionKey.Length} bytes. " +
            "Remove the key from .env to auto-generate a new one, or provide a valid 256-bit Base64 key.");
    }

    // ──────── PHASE 6 ──── Infrastructure persistence ──────────────────────
    // Infrastructure — resolve provider from .env
    var databaseOptions = DatabaseProviderOptions.FromConfiguration(
        builder.Configuration,
        string.IsNullOrEmpty(dataDir) ? backendInstancePaths.DataDirectory : dataDir);
    var storageMode = databaseOptions.Provider;

    builder.Services.AddInfrastructure(databaseOptions);

    // CORS
    builder.Services.AddCors(options =>
    {
        options.AddDefaultPolicy(policy =>
        {
            policy.AllowAnyOrigin()
                  .AllowAnyMethod()
                  .AllowAnyHeader();
        });
    });

    // Auth
    var configuredJwtSecret = builder.Configuration["Jwt:Secret"];
    var jwtOptions = new JwtOptions
    {
        Secret = string.IsNullOrWhiteSpace(configuredJwtSecret)
            ? PersistentKeyStore.GetOrCreate("jwt-secret", backendInstancePaths)
            : configuredJwtSecret
    };
    var jwtSection = builder.Configuration.GetSection("Jwt");
    jwtOptions.Issuer = jwtSection["Issuer"] ?? jwtOptions.Issuer;
    jwtOptions.Audience = jwtSection["Audience"] ?? jwtOptions.Audience;

    if (TimeSpan.TryParse(jwtSection["AccessTokenLifetime"], out var accessTokenLifetime)
        && accessTokenLifetime > TimeSpan.Zero)
        jwtOptions.AccessTokenLifetime = accessTokenLifetime;

    if (TimeSpan.TryParse(jwtSection["RefreshTokenLifetime"], out var refreshTokenLifetime)
        && refreshTokenLifetime > TimeSpan.Zero)
        jwtOptions.RefreshTokenLifetime = refreshTokenLifetime;

    builder.Services.AddSingleton(jwtOptions);
    builder.Services.AddScoped<TokenService>();
    builder.Services.AddScoped<AuthService>();
    builder.Services.AddScoped<SessionService>();

    // ──────── PHASE 8 ──── Domain services + host module bridges ──────────
    // EncryptionOptions is consumed by ProviderService (decrypts API keys
    // before each provider call) — keep it singleton.
    var encryptionOptions = new EncryptionOptions
    {
        Key = encryptionKey,
        EncryptProviderKeys = builder.Configuration
            .GetValue("Encryption:EncryptProviderKeys", defaultValue: true),
    };
    builder.Services.AddSingleton(encryptionOptions);

    builder.Services.AddTransient<HttpLoggingDelegatingHandler>();
    builder.Services.AddHttpClient()
        .ConfigureHttpClientDefaults(b => b.AddHttpMessageHandler<HttpLoggingDelegatingHandler>());
    // Provider plugins are contributed by modules (e.g. LlamaSharp module).
    // The factory resolves over IEnumerable<IProviderPlugin>.
    builder.Services.AddSingleton<ProviderApiClientFactory>();
    builder.Services.AddSingleton<AgentAdministrationEngine>();
    builder.Services.AddSingleton<AgentRuntimeAdministrationEngine>();
    builder.Services.AddSingleton<PermissionEvaluationEngine>();
    builder.Services.AddSingleton<PermissionDelegateEvaluationEngine>();
    builder.Services.AddSingleton<AgentActionWorkflowEngine>();
    builder.Services.AddSingleton<AdminPermissionSeedEngine>();
    builder.Services.AddSingleton<RolePermissionAdministrationEngine>();
    builder.Services.AddSingleton<RoleAdministrationEngine>();
    builder.Services.AddSingleton<AgentJobAdministrationEngine>();
    builder.Services.AddSingleton<AgentJobAdministrationWorkflowEngine>();
    builder.Services.AddSingleton<AgentJobLifecycleEngine>();
    builder.Services.AddSingleton<AgentJobRuntimeEngine>();
    builder.Services.AddSingleton<AgentJobDefaultResourceResolver>();
    builder.Services.AddSingleton<DefaultResourceEngine>();
    builder.Services.AddSingleton<DefaultResourceAdministrationEngine>();
    builder.Services.AddSingleton<ConversationTopologyEngine>();
    builder.Services.AddSingleton<ConversationAdministrationEngine>();
    builder.Services.AddSingleton<ConversationSteeringEngine>();
    builder.Services.AddSingleton<ProviderCatalogEngine>();
    builder.Services.AddSingleton<ProviderModelAdministrationEngine>();
    builder.Services.AddSingleton<ProviderCostEngine>();
    builder.Services.AddSingleton<ModelCatalogEngine>();
    builder.Services.AddSingleton<ChatCostEngine>();
    builder.Services.AddSingleton<ChatPromptEngine>();
    builder.Services.AddSingleton<ChatRequestPlanningEngine>();
    builder.Services.AddSingleton<ChatHistoryEngine>();
    builder.Services.AddSingleton<ChatQueryWorkflowEngine>();
    builder.Services.AddSingleton<ChatDefaultHeaderEngine>();
    builder.Services.AddSingleton<ChatHeaderWorkflowEngine>();
    builder.Services.AddSingleton<ChatHeaderGrantFormatter>();
    builder.Services.AddSingleton<ChatToolResultEngine>();
    builder.Services.AddSingleton<ChatMessageEngine>();
    builder.Services.AddSingleton<ChatRequestWorkflowEngine>();
    builder.Services.AddSingleton<ChatToolSelectionEngine>();
    builder.Services.AddSingleton<ChatToolWorkflowEngine>();
    builder.Services.AddSingleton<ChatNativeToolCallParser>();
    builder.Services.AddSingleton<ChatNativeJobToolExecutor>();
    builder.Services.AddSingleton<ChatInlineToolExecutor>();
    builder.Services.AddSingleton<ChatNativeToolLoopEngine>();
    builder.Services.AddSingleton<ChatProviderExecutionWorkflowEngine>();
    builder.Services.AddSingleton<ChatStreamingResponseEngine>();
    builder.Services.AddSingleton<ModuleJobToolExecutor>();
    builder.Services.AddSingleton<TaskPreflightEngine>();
    builder.Services.AddSingleton<TaskAdministrationWorkflowEngine>();
    builder.Services.AddSingleton<TaskHostBridgeWorkflowEngine>();
    builder.Services.AddSingleton<TaskTriggerBindingPlanner>();
    builder.Services.AddSingleton<ToolAwarenessSetEngine>();
    builder.Services.AddSingleton<ToolAwarenessAdministrationEngine>();

    builder.Services.AddScoped<EfProviderModelAdministrationHost>();
    builder.Services.AddScoped<EfProviderCostHost>();
    builder.Services.AddScoped<ProviderService>();
    builder.Services.AddScoped<ProviderCostService>();
    builder.Services.AddScoped<ModelService>();
    builder.Services.AddScoped<EfAgentAdministrationHost>();
    builder.Services.AddScoped<AgentService>();
    builder.Services.AddScoped<EfConversationAdministrationHost>();
    builder.Services.AddScoped<ChannelService>();
    builder.Services.AddScoped<ThreadService>();
    builder.Services.AddScoped<ContextService>();
    builder.Services.AddScoped<EfRoleAdministrationHost>();
    builder.Services.AddScoped<DefaultResourceSetService>();
    builder.Services.AddScoped<EfDefaultResourceAdministrationHost>();
    builder.Services.AddScoped<ToolAwarenessSetService>();
    builder.Services.AddScoped<EfToolAwarenessAdministrationHost>();
    builder.Services.AddScoped<EfAgentActionHost>();
    builder.Services.AddScoped<EfChatQueryHost>();
    builder.Services.AddScoped<AgentActionService>();
    builder.Services.AddScoped<EfAgentJobAdministrationHost>();
    builder.Services.AddScoped<AgentJobService>();

    // Host bridges — concrete services that adapt Core/Infrastructure to
    // the abstract IXxx contracts modules consume.  Modules only see the
    // interfaces from SharpClaw.Contracts.Modules.
    builder.Services.AddScoped<IAgentJobController, HostAgentJobController>();
    builder.Services.AddScoped<IAgentManager, HostAgentManager>();
    builder.Services.AddScoped<IAgentJobReader, HostAgentJobReader>();
    builder.Services.AddSingleton<IAgentJobCostTracker, HostAgentJobCostTracker>();
    builder.Services.AddSingleton<IModelInfoProvider, HostModelInfoProvider>();
    builder.Services.AddSingleton<IInProcessModuleSecretReader, HostInProcessModuleSecretReader>();
    builder.Services.AddScoped<IModelRegistrar, HostModelRegistrar>();
    builder.Services.AddSingleton<ChatCache>();
    builder.Services.AddSingleton<ChatRuntimeInvalidationPlanner>();
    builder.Services.AddScoped<IContainerProvisioner, HostContainerProvisioner>();
    builder.Services.AddScoped<IThreadResolver, HostThreadResolver>();
    builder.Services.AddScoped<IHostContextDataReader, HostContextDataReader>();
    builder.Services.AddScoped<IConversationSteering, HostConversationSteering>();
    builder.Services.AddSingleton<IModuleLifecycleManager, HostModuleLifecycleManager>();
    builder.Services.AddSingleton<IModuleInfoProvider, HostModuleInfoProvider>();
    builder.Services.AddSingleton<IModuleStorageContractProvider>(sp => sp.GetRequiredService<ModuleRegistry>());
    builder.Services.AddSingleton<IModuleStorageTelemetry, ModuleStorageTelemetry>();
    builder.Services.AddSingleton<IForeignModuleProtocolContractResolver, HostModuleProtocolContractResolver>();
    builder.Services.AddScoped<IModuleStorageGateway, BundledModuleStorageGateway>();

    builder.Services.AddScoped<ChatHeaderTemplateEngine>();
    builder.Services.AddScoped<ChatHeaderExpansionPlanner>();
    builder.Services.AddScoped<HeaderTagProcessor>();
    builder.Services.AddScoped<ChatService>();
    builder.Services.AddSingleton<ThreadActivitySignal>();
    builder.Services.AddScoped<RoleService>();

    // ──────── PHASE 9 ──── Task runtime + trigger host + host metric probes
    builder.Services.AddScoped<EfTaskPreflightHost>();
    builder.Services.AddScoped<TaskPreflightChecker>();
    builder.Services.AddScoped<TaskTriggerRegistrar>();
    builder.Services.AddScoped<EfTaskAdministrationHost>();
    builder.Services.AddScoped<TaskService>();
    builder.Services.AddScoped<ITaskAuthoring>(sp => sp.GetRequiredService<TaskService>());
    builder.Services.AddScoped<ITaskInstanceLauncher, TaskInstanceLauncher>();
    builder.Services.AddScoped<IGlobalFlagEvaluator, GlobalFlagEvaluator>();
    builder.Services.AddScoped<EnvFileService>();
    builder.Services.AddScoped<TaskStartupPreparationEngine>();
    builder.Services.AddScoped<TaskPlanExecutionEngine>();
    builder.Services.AddScoped<TaskOrchestrator>();
    builder.Services.AddScoped<IHostAgentBridge, HostAgentBridge>();
    builder.Services.AddSingleton<ForeignModuleTaskContextRegistry>();
    builder.Services.AddSingleton<TaskRuntimeRegistry>();
    builder.Services.AddSingleton<TaskRuntimeHost>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<TaskRuntimeHost>());
    // Trigger host service + built-in sources
    builder.Services.AddSingleton<TaskTriggerHostService>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<TaskTriggerHostService>());
    builder.Services.AddSingleton<ITaskTriggerSourceRegistry, TaskTriggerSourceRegistry>();
    // Host-side metric probes consumed by the Metrics module's built-in providers.
    builder.Services.AddSingleton<IHostQueueMetrics, HostQueueMetrics>();

    // ──────── PHASE 10 ─── Module system services ──────────────────────────
    // Registry, dispatcher, metrics, health checks, and per-request execution
    // context.  These are pure host-side plumbing — no module code has loaded
    // yet — and must exist before PHASE 11's per-module ConfigureServices
    // hooks try to register dependencies on them.
    builder.Services.AddSingleton<ModuleRegistry>();
    builder.Services.AddSingleton<ModuleToolExecutionPlanner>();
    builder.Services.AddSingleton<ModuleToolPermissionPlanner>();
    builder.Services.AddSingleton<ModuleToolPermissionExecutor>();
    builder.Services.AddSingleton<ModuleMetricsCollector>();
    builder.Services.AddSingleton<IModuleCapabilityTelemetry, ModuleCapabilityTelemetry>();
    builder.Services.AddSingleton<ModuleEventDispatcher>();
    builder.Services.AddSingleton<ISharpClawEventSinkRegistry>(sp => sp.GetRequiredService<ModuleEventDispatcher>());
    builder.Services.AddScoped<ModuleExecutionContext>();
    builder.Services.AddScoped<IModuleConfigStore>(sp =>
    {
        var ctx = sp.GetRequiredService<ModuleExecutionContext>();
        var dbCtx = sp.GetRequiredService<SharpClaw.Runtime.INF.Persistence.SharpClawDbContext>();
        return new ModuleConfigStore(dbCtx, ctx.ModuleId ?? "");
    });
    builder.Services.AddSingleton<ModuleHealthCheckService>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<ModuleHealthCheckService>());

    // ──────── PHASE 11 ─── Bundled-module discovery + ConfigureServices ────
    // Discover bundled modules from manifests only. Bundled implementation
    // assemblies stay out of parent DI; their services are projected later by
    // sidecar discovery.
    var moduleLoader = ModuleLoader.DiscoverBundled(builder.Configuration);

    builder.Services.AddSingleton(moduleLoader);
    builder.Services.AddScoped<ModuleService>();

    // ──────── PHASE 12 ─── Misc post-module singletons ─────────────────────
    // (The scheduled-job loop has moved to
    //  the Agent Orchestration module's ScheduledJobWorker; it is started
    //  from that module's InitializeAsync, not here.)
    builder.Services.AddSingleton<DatabaseInitializationGate>();

    // Seeding hosted service runs admin/role/provider seeding on startup.
    builder.Services.AddHostedService<SeedingService>();

    // Per-instance API key file consumed by ApiKeyMiddleware (PHASE 20).
    // Register lazily so one-shot CLI commands do not rotate a live API
    // server's auth files before exiting.
    builder.Services.AddSingleton<ApiKeyProvider>();

    // Short-ID resolver shared by core CLI verbs and module CLI handlers.
    builder.Services.AddSingleton<ICliIdResolver, CliIdResolver>();

    // ──────── PHASE 13 ─── Build the container (now immutable) ─────────────
    var app = builder.Build();

    // Configure JSON serialisation for minimal API responses to match
    // the conventions used throughout the solution: camelCase + string enums.
    app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>()
        .Value.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());

    // ──────── PHASE 14 ─── Post-build infrastructure init ──────────────────
    // The InMemory DbContext is empty at this point.  InitializeInfrastructureAsync
    // hydrates it from the JSON file store (or warms relational connections),
    // and the gate gets toggled so middleware/CLI/hosted-services can know
    // when it is safe to read.  Anything that depends on persistent data must
    // wait on DatabaseInitializationGate or run after this block.
    var databaseInitializationGate = app.Services.GetRequiredService<DatabaseInitializationGate>();
    try
    {
        await app.Services.InitializeInfrastructureAsync();
        databaseInitializationGate.MarkInitialized();
    }
    catch (Exception ex)
    {
        databaseInitializationGate.MarkFailed(ex);
        throw;
    }

    // §8c: Check for pending EF Core migrations on relational providers and warn.
    if (storageMode != StorageMode.JsonFile)
    {
        var migrationSvc = app.Services.GetService<SharpClaw.Runtime.INF.Persistence.MigrationService>();
        if (migrationSvc is not null)
        {
            var status = await migrationSvc.GetStatusAsync();
            if (status.Pending.Count > 0)
            {
                Log.Warning(
                    "Database has {Count} pending migration(s): {Names}. " +
                    "Run 'db migrate' or POST /admin/db/migrate to apply them.",
                    status.Pending.Count, string.Join(", ", status.Pending));
            }
        }
    }

    // ──────── PHASE 15 ─── Module enable-state sync + persistence ─────────
    // Wire the module loader to the built service provider, load each
    // bundled module's manifest, then reconcile the .env-driven enabled
    // set against the database.  After this block we know which modules
    // are enabled, and every module's DbContext type is registered so
    // optional cross-module dependencies can be resolved lazily.
    moduleLoader.SetRootServices(app.Services);
    moduleLoader.LoadAllManifests();

    // Sync module state from configuration → DB, determine which modules to enable.
    HashSet<string> enabledModuleIds;
    using (var scope = app.Services.CreateScope())
    {
        var moduleSvc = scope.ServiceProvider.GetRequiredService<ModuleService>();
        enabledModuleIds = await moduleSvc.SyncStateFromConfigAsync(app.Configuration);
    }

    // Register only enabled modules with the registry.
    var registry = app.Services.GetRequiredService<ModuleRegistry>();
    var allBundled = moduleLoader.GetAllBundled();
    var registeredBundledCount = 0;
    var disabledBundledCount = 0;

    // Bundled modules no longer contribute parent DbContexts. Any module-owned
    // state must be surfaced through the sidecar runtime host.
    using (var scope = app.Services.CreateScope())
    {
        var moduleSvc = scope.ServiceProvider.GetRequiredService<ModuleService>();
        foreach (var bundledModule in allBundled.Where(m => !moduleLoader.IsManifestOnlyBundledModule(m.Id)))
            moduleSvc.RegisterModulePersistence(bundledModule);
    }

    foreach (var bundledModule in allBundled)
    {
        if (!enabledModuleIds.Contains(bundledModule.Id))
        {
            Log.Information("Module '{ModuleId}' ({DisplayName}) is disabled — skipping registration [bundled]",
                bundledModule.Id, bundledModule.DisplayName);
            disabledBundledCount++;
            continue;
        }

        using var moduleRegistrationScope = app.Services.CreateScope();
        var moduleSvc = moduleRegistrationScope.ServiceProvider.GetRequiredService<ModuleService>();
        var registeredModule = await moduleSvc.RegisterBundledRuntimeAsync(
            bundledModule.Id,
            app.Services,
            CancellationToken.None);
        registeredBundledCount++;

        var manifest = moduleLoader.GetManifest(bundledModule.Id);
        var version = manifest?.Version ?? "unknown";
        var runtimeLabel = registry.GetRuntimeHost(bundledModule.Id) switch
        {
            InProcessModuleHost => "bundled-in-process",
            IForeignModuleRuntimeHost => "bundled-sidecar",
            null => "bundled",
            _ => "bundled-runtime",
        };
        Log.Information("Module '{ModuleId}' ({DisplayName}) registered [{Runtime}, v{Version}]",
            registeredModule.Id, registeredModule.DisplayName, runtimeLabel, version);
    }

    Log.Information("Bundled modules: {Registered} registered, {Disabled} disabled, {Total} discovered",
        registeredBundledCount, disabledBundledCount, allBundled.Count);

    // ──────── PHASE 16 ─── Module initialization in dependency order ──────
    // Topological sort over RequiredContracts/ExportedContracts.  A module
    // whose required contract has no surviving provider is excluded; a
    // module whose InitializeAsync throws gets unregistered AND poisons its
    // exported contracts so dependents cascade-skip too.
    var initOrder = registry.GetInitializationOrder(out var excludedModules);

    // Unregister modules excluded during dependency resolution (missing deps, cycles).
    foreach (var (moduleId, reason) in excludedModules)
    {
        Log.Warning("Module '{ModuleId}' excluded from initialization: {Reason}", moduleId, reason);
        var runtimeHost = registry.GetRuntimeHost(moduleId);
        app.Services.GetRequiredService<SharpClaw.Runtime.INF.Persistence.Modules.RuntimeModuleDbContextRegistry>()
            .UnregisterModule(moduleId);
        registry.Unregister(moduleId);
        if (runtimeHost is not null)
            await runtimeHost.DisposeAsync();
    }

    // Track contracts that became unavailable due to runtime init failures
    // so that downstream dependents can be cascade-skipped.
    var failedContracts = new HashSet<string>(StringComparer.Ordinal);
    var initializedCount = 0;
    var failedInitCount = 0;

    foreach (var moduleId in initOrder)
    {
        var module = registry.GetModule(moduleId);
        if (module is null) continue;

        // Check if any required (non-optional) contract's provider failed at runtime.
        var cascadeMiss = module.RequiredContracts
            .Where(r => !r.Optional && failedContracts.Contains(r.ContractName))
            .Select(r => r.ContractName)
            .ToList();

        if (cascadeMiss.Count > 0)
        {
            Log.Warning(
                "Module '{ModuleId}' skipped — depends on contract(s) whose provider failed: {Contracts}",
                moduleId, string.Join(", ", cascadeMiss));

            // Poison this module's own exports so dependents cascade too.
            foreach (var export in module.ExportedContracts)
                failedContracts.Add(export.ContractName);

            var runtimeHost = registry.GetRuntimeHost(moduleId);
            registry.Unregister(moduleId);
            app.Services.GetRequiredService<SharpClaw.Runtime.INF.Persistence.Modules.RuntimeModuleDbContextRegistry>()
                .UnregisterModule(moduleId);
            if (runtimeHost is not null)
                await runtimeHost.DisposeAsync();
            failedInitCount++;
            continue;
        }

        try
        {
            var runtimeHost = registry.GetRuntimeHost(moduleId);
            await module.InitializeAsync(runtimeHost?.Services ?? app.Services, CancellationToken.None);
            Log.Information("Module '{ModuleId}' initialized successfully [bundled]", moduleId);
            initializedCount++;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Module '{ModuleId}' failed to initialize — unregistering", moduleId);

            foreach (var export in module.ExportedContracts)
                failedContracts.Add(export.ContractName);

            var runtimeHost = registry.GetRuntimeHost(moduleId);
            registry.Unregister(moduleId);
            app.Services.GetRequiredService<SharpClaw.Runtime.INF.Persistence.Modules.RuntimeModuleDbContextRegistry>()
                .UnregisterModule(moduleId);
            if (runtimeHost is not null)
                await runtimeHost.DisposeAsync();
            failedInitCount++;
        }
    }

    // ──────── PHASE 17 ─── External modules (filesystem + .env entries) ───
    // External modules live outside the bundled set: a directory scan picks
    // up drop-in modules and the .env ExternalModules section adds explicit
    // absolute-path entries.  Both are best-effort — failures here log a
    // warning and continue rather than aborting startup.
    var externalLoadedCount = 0;
    try
    {
        using var extScope = app.Services.CreateScope();
        var moduleSvc = extScope.ServiceProvider.GetRequiredService<ModuleService>();
        var externalModules = await moduleSvc.ScanExternalModulesAsync(app.Services);
        externalLoadedCount = externalModules.Count;
        foreach (var ext in externalModules)
            Log.Information("Module '{ModuleId}' ({DisplayName}) loaded [external, v{Version}]",
                ext.ModuleId, ext.DisplayName, ext.Version ?? "unknown");
    }
    catch (Exception ex)
    {
        Log.Warning(ex, "External module scan failed — continuing without external modules");
    }

    // Load external modules defined in .env ExternalModules section
    var envExternalLoadedCount = 0;
    try
    {
        using var envExtScope = app.Services.CreateScope();
        var moduleSvc = envExtScope.ServiceProvider.GetRequiredService<ModuleService>();
        var envModules = await moduleSvc.LoadExternalModulesFromConfigAsync(app.Configuration, app.Services);
        envExternalLoadedCount = envModules.Count;
        foreach (var ext in envModules)
            Log.Information("Module '{ModuleId}' ({DisplayName}) loaded [env-external, v{Version}]",
                ext.ModuleId, ext.DisplayName, ext.Version ?? "unknown");
    }
    catch (Exception ex)
    {
        Log.Error(ex, "Env-configured external module load failed - aborting startup");
        throw;
    }

    // Module startup summary
    var totalLoaded = initializedCount + externalLoadedCount + envExternalLoadedCount;
    Log.Information(
        "Module startup complete: {TotalLoaded} loaded ({BundledInit} bundled, {ExternalLoaded} external, {EnvExternalLoaded} env-external), " +
        "{FailedInit} failed, {Disabled} disabled, {Excluded} excluded",
        totalLoaded, initializedCount, externalLoadedCount, envExternalLoadedCount,
        failedInitCount, disabledBundledCount, excludedModules.Count);

    // PHASE 18 reserved for provider lifecycle hooks.
    // ──────── PHASE 19 ─── CLI command dispatch (one-shot) ────────────────
    // If the process was launched with a recognised CLI verb, handle it
    // and exit; we never enter API mode in this case.  Errors are written
    // to stderr to keep CLI exit codes / stdout clean.
    try
    {
        if (await CliDispatcher.TryHandleAsync(args, app.Services))
            return;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Error: {ex.Message}");
        Log.Error(ex, "CLI command failed");
        return;
    }

    var apiKeyProvider = app.Services.GetRequiredService<ApiKeyProvider>();

    // ──────── PHASE 20 ─── HTTP pipeline (middleware order is load-bearing)
    //
    //   DatabaseInitializationGateMiddleware     — 503s until PHASE 14 done.
    //   ExceptionHandlingMiddleware              — must wrap everything below.
    //   SerilogRequestLogging (optional)         — only when enabled in .env.
    //   UseCors / UseRouting                     — standard ASP.NET Core order.
    //   ApiKeyMiddleware                         — validates X-Api-Key.
    //   JwtSessionMiddleware                     — populates SessionService.
    //   MigrationGateMiddleware (relational)     — pauses traffic mid-migration.
    //   UseWebSockets                            — required for SSE/WS handlers.
    app.UseMiddleware<DatabaseInitializationGateMiddleware>();
    app.UseMiddleware<ExceptionHandlingMiddleware>();
    if (serilogOptions.Enabled && serilogOptions.RequestLoggingEnabled)
        app.UseSerilogRequestLogging();
    app.UseCors();
    app.UseRouting();
    app.UseMiddleware<ApiKeyMiddleware>();
    app.UseMiddleware<JwtSessionMiddleware>();
    app.Use(async (context, next) =>
    {
        var moduleServices = context.GetEndpoint()
            ?.Metadata.GetMetadata<ModuleRequestServicesMetadata>()
            ?.Services;

        if (moduleServices is null)
        {
            await next();
            return;
        }

        var originalServices = context.RequestServices;
        await using var scope = moduleServices.CreateAsyncScope();
        context.RequestServices = scope.ServiceProvider;
        try
        {
            await next();
        }
        finally
        {
            context.RequestServices = originalServices;
        }
    });

    // Migration gate — pauses requests during manual migrations (relational only).
    if (storageMode != StorageMode.JsonFile)
        app.UseMiddleware<MigrationGateMiddleware>();

    app.UseWebSockets();

    // ──────── PHASE 21 ─── Endpoint mapping ───────────────────────────────
    // Liveness check — no auth required.
    app.MapGet("/echo", () => Results.Ok(new { status = "ok" }));

    // API key validation — requires valid X-Api-Key header.
    app.MapGet("/ping", () => Results.Ok(new { status = "authenticated" }));

    // Core attribute-discovered handlers (see SharpClaw.Runtime.Host.Routing).
    app.MapHandlers();

    // Module-registered endpoints: each module maps its own REST routes.
    foreach (var module in registry.GetAllModules())
    {
        if (module is not ISharpClawRuntimeModule runtimeModule)
            continue;

        try
        {
            IEndpointRouteBuilder endpointHost = registry.GetRuntimeHost(module.Id) is InProcessModuleHost inProcessHost
                ? new ModuleEndpointRouteBuilder(app, inProcessHost.Services)
                : app;
            runtimeModule.MapEndpoints(endpointHost);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Module '{ModuleId}' failed to map endpoints", module.Id);
        }
    }
    app.MapForeignModuleEndpoints(registry);

    // Webhook trigger routes — registered lazily after ApplicationStarted so
    // that TaskTriggerHostService has loaded its first binding set.  The
    // WebhookRouteRegistry holds an IRouteBuilder reference and rebinds
    // routes whenever a webhook trigger source's binding set changes.
    if (app.Services.GetService<IWebhookTriggerHost>() is { } webhookSource)
    {
        var webhookRegistry = new WebhookRouteRegistry(
            app,
            webhookSource,
            app.Services.GetRequiredService<Microsoft.Extensions.Logging.ILogger<WebhookRouteRegistry>>());
        webhookSource.SetRouteRegistrar(webhookRegistry);
    }
    else
    {
        Log.Information("No webhook trigger host registered - dynamic webhook routes disabled");
    }

    // ──────── PHASE 22 ─── Shutdown registrations ─────────────────────────
    // Two ApplicationStopping hooks: one for host-side cleanup (api key,
    // discovery entry), one for graceful per-module ShutdownAsync.  Module
    // shutdown is best-effort — exceptions are logged at Warning so a
    // misbehaving module can't block the host from stopping.
    app.Lifetime.ApplicationStopping.Register(() =>
    {
        apiKeyProvider.Cleanup();
        backendInstancePaths.DeleteDiscoveryEntry();
    });

    app.Lifetime.ApplicationStopping.Register(() =>
    {
        foreach (var module in registry.GetAllModules())
        {
            try { module.ShutdownAsync().GetAwaiter().GetResult(); }
            catch (Exception ex)
            {
                Log.Warning(ex, "Module '{ModuleId}' shutdown error", module.Id);
            }
        }

        foreach (var runtimeHost in registry.GetRuntimeHosts())
        {
            try { runtimeHost.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
            catch (Exception ex)
            {
                Log.Warning(ex, "Module runtime host disposal error");
            }
        }
    });

    // ──────── PHASE 23 ─── Start + discovery publication ──────────────────
    // Start the host, then publish the bound URL to the per-instance
    // discovery directory so sibling processes (frontend, gateway, CLI)
    // can find this backend without any prior configuration.
    await app.StartAsync();

    var urls = string.Join(", ", app.Urls);
    var primaryUrl = app.Urls.FirstOrDefault();
    SharpClawDiscoveryLease? discoveryLease = null;
    if (!string.IsNullOrWhiteSpace(primaryUrl))
    {
        backendManifest.BaseUrl = primaryUrl;
        backendManifest.DataDirectory = dataDir ?? backendInstancePaths.DataDirectory;
        backendInstancePaths.SaveManifest(backendManifest);
        discoveryLease = new SharpClawDiscoveryLease(
            backendInstancePaths,
            primaryUrl,
            TimeSpan.FromSeconds(30));
        discoveryLease.PublishNow();
    }

    Log.Information("SharpClaw API listening on {Urls}", urls);
    Log.Information("API key written to: {KeyFilePath}", apiKeyProvider.KeyFilePath);

    // ──────── PHASE 24 ─── Interactive REPL / headless wait ───────────────
    // Interactive-mode console logging discipline:
    // running, we suppress console logging so Serilog output doesn't scroll
    // over the user's prompt. CLI command/response logs still go to
    // System.Diagnostics.Debug (VS Output > Debug pane).
    //
    // In headless mode (stdin redirected or closed), RunInteractiveAsync
    // skips the REPL and just waits on the cancellation token. Console
    // logging must stay visible there — stdout is the only feedback channel
    // for containers, CI runs, systemd units, and detached child processes.
    var forceRepl = string.Equals(
        Environment.GetEnvironmentVariable("SHARPCLAW_FORCE_REPL"), "1",
        StringComparison.Ordinal);
    var interactive = !Console.IsInputRedirected || forceRepl;
    if (interactive)
        consoleLevelSwitch.MinimumLevel = LogEventLevel.Fatal;

    await CliDispatcher.RunInteractiveAsync(app.Services, app.Lifetime.ApplicationStopping);

    if (interactive)
        consoleLevelSwitch.MinimumLevel = LogEventLevel.Information;

    discoveryLease?.Dispose();
    await app.Services.ShutdownInfrastructureAsync();
    await app.StopAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "SharpClaw terminated unexpectedly.");
}
finally
{
    await Log.CloseAndFlushAsync();
}

internal sealed class ModuleEndpointRouteBuilder(
    IEndpointRouteBuilder inner,
    IServiceProvider moduleServices) : IEndpointRouteBuilder
{
    private readonly ModuleEndpointDataSources _dataSources = new(inner.DataSources, moduleServices);

    public IServiceProvider ServiceProvider => moduleServices;

    public ICollection<EndpointDataSource> DataSources => _dataSources;

    public IApplicationBuilder CreateApplicationBuilder()
    {
        var builder = inner.CreateApplicationBuilder();
        builder.ApplicationServices = moduleServices;
        return builder;
    }

    private sealed class ModuleEndpointDataSources(
        ICollection<EndpointDataSource> innerDataSources,
        IServiceProvider moduleServices) : ICollection<EndpointDataSource>
    {
        private readonly List<EndpointDataSource> _moduleDataSources = [];
        private readonly Dictionary<EndpointDataSource, EndpointDataSource> _attachedDataSources = [];

        public int Count => _moduleDataSources.Count;
        public bool IsReadOnly => false;

        public void Add(EndpointDataSource item)
        {
            _moduleDataSources.Add(item);

            var attached = new ModuleEndpointDataSource(item, moduleServices);
            _attachedDataSources[item] = attached;
            innerDataSources.Add(attached);
        }

        public void Clear()
        {
            foreach (var item in _moduleDataSources.ToArray())
            {
                if (_attachedDataSources.Remove(item, out var attached))
                    innerDataSources.Remove(attached);
            }

            _moduleDataSources.Clear();
        }

        public bool Contains(EndpointDataSource item) => _moduleDataSources.Contains(item);

        public void CopyTo(EndpointDataSource[] array, int arrayIndex) =>
            _moduleDataSources.CopyTo(array, arrayIndex);

        public IEnumerator<EndpointDataSource> GetEnumerator() =>
            _moduleDataSources.GetEnumerator();

        public bool Remove(EndpointDataSource item)
        {
            var removed = _moduleDataSources.Remove(item);
            if (removed && _attachedDataSources.Remove(item, out var attached))
                innerDataSources.Remove(attached);

            return removed;
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() =>
            GetEnumerator();
    }

    private sealed class ModuleEndpointDataSource(
        EndpointDataSource inner,
        IServiceProvider moduleServices) : EndpointDataSource
    {
        private readonly ModuleRequestServicesMetadata _metadata = new(moduleServices);

        public override IReadOnlyList<Endpoint> Endpoints =>
            [.. inner.Endpoints.Select(AttachMetadata)];

        public override Microsoft.Extensions.Primitives.IChangeToken GetChangeToken() =>
            inner.GetChangeToken();

        private Endpoint AttachMetadata(Endpoint endpoint)
        {
            if (endpoint.Metadata.GetMetadata<ModuleRequestServicesMetadata>() is not null)
                return endpoint;

            var metadata = endpoint.Metadata.ToList();
            metadata.Add(_metadata);
            var endpointMetadata = new EndpointMetadataCollection(metadata);

            if (endpoint is RouteEndpoint routeEndpoint)
            {
                return new RouteEndpoint(
                    routeEndpoint.RequestDelegate!,
                    routeEndpoint.RoutePattern,
                    routeEndpoint.Order,
                    endpointMetadata,
                    routeEndpoint.DisplayName);
            }

            return new Endpoint(endpoint.RequestDelegate, endpointMetadata, endpoint.DisplayName);
        }
    }
}

internal sealed record ModuleRequestServicesMetadata(IServiceProvider Services);
