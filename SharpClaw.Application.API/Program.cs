using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using SharpClaw.Application.API;
using SharpClaw.Application.API.Api;
using SharpClaw.Application.API.Cli;
using SharpClaw.Application.API.Handlers;
using SharpClaw.Application.API.Routing;
using SharpClaw.Application.API.Webhooks;
using SharpClaw.Application.Core.Clients;
using SharpClaw.Application.Core.Modules;
using SharpClaw.Application.Infrastructure.Tasks;
using SharpClaw.Application.Core.Services.Triggers;
using SharpClaw.Application.Core.Services;
using SharpClaw.Application.Services;
using SharpClaw.Application.Infrastructure.Logging;
using SharpClaw.Application.Infrastructure.Tasks.Parsing;
using SharpClaw.Application.Infrastructure.Tasks.Registry;
using SharpClaw.Application.Services.Auth;
using SharpClaw.Contracts.Chat;
using SharpClaw.Contracts.Modules;
using SharpClaw.Contracts.Persistence;
using SharpClaw.Contracts.Providers;
using SharpClaw.Infrastructure;
using SharpClaw.Infrastructure.Configuration;
using SharpClaw.Infrastructure.Persistence.JSON;
using SharpClaw.Utils.Logging;
using SharpClaw.Utils.Instances;
using Microsoft.EntityFrameworkCore;
using SharpClaw.Utils.Security;
using Serilog.Events;
using SharpClaw.Contracts.Permissions;
using SharpClaw.Contracts.Tasks;

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
//    PHASE 6  Infrastructure persistence (DbContext, JSON file store, etc.).
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
//             registration + bundled persistence load.
//    PHASE 16 Module initialisation in dependency order with cascade
//             unregistration on failure.
//    PHASE 17 External-module scan (filesystem + .env entries).
//    PHASE 18 ApplicationStarted hook (FlushWorker start).
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
        .Enrich.FromLogContext();

    if (serilogOptions.ConsoleEnabled)
        loggerConfiguration = loggerConfiguration.WriteTo.Console(levelSwitch: consoleLevelSwitch);

    if (serilogOptions.FileEnabled)
        loggerConfiguration = loggerConfiguration.WriteTo.File(
            sessionLogs.LogFilePath,
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
    var storageMode = Enum.TryParse<StorageMode>(
        builder.Configuration["Database:Provider"], ignoreCase: true, out var parsed)
        ? parsed
        : StorageMode.JsonFile;

    var connectionString = storageMode != StorageMode.JsonFile
        ? builder.Configuration[$"ConnectionStrings:{storageMode}"]
        : null;

    builder.Services.AddInfrastructure(storageMode, connectionString, opts =>
    {
        if (!string.IsNullOrEmpty(dataDir))
            opts.DataDirectory = dataDir;
        else
            opts.DataDirectory = backendInstancePaths.DataDirectory;
        opts.EncryptAtRest = builder.Configuration
            .GetValue("Encryption:EncryptDatabase", defaultValue: true);
        opts.FsyncOnWrite = builder.Configuration
            .GetValue("Database:FsyncOnWrite", defaultValue: opts.FsyncOnWrite);
        opts.IndexRescanIntervalMinutes = builder.Configuration
            .GetValue("Database:IndexRescanIntervalMinutes", defaultValue: opts.IndexRescanIntervalMinutes);
        opts.QuarantineMaxAgeDays = builder.Configuration
            .GetValue("Database:QuarantineMaxAgeDays", defaultValue: opts.QuarantineMaxAgeDays);
        opts.EnableChecksums = builder.Configuration
            .GetValue("Database:EnableChecksums", defaultValue: opts.EnableChecksums);
        opts.VerifyChecksumsOnRead = builder.Configuration
            .GetValue("Database:VerifyChecksumsOnRead", defaultValue: opts.VerifyChecksumsOnRead);
        opts.EnableEventLog = builder.Configuration
            .GetValue("Database:EnableEventLog", defaultValue: opts.EnableEventLog);
        opts.EventLogRetentionDays = builder.Configuration
            .GetValue("Database:EventLogRetentionDays", defaultValue: opts.EventLogRetentionDays);
        opts.EnableSnapshots = builder.Configuration
            .GetValue("Database:EnableSnapshots", defaultValue: opts.EnableSnapshots);
        opts.SnapshotIntervalHours = builder.Configuration
            .GetValue("Database:SnapshotIntervalHours", defaultValue: opts.SnapshotIntervalHours);
        opts.SnapshotRetentionCount = builder.Configuration
            .GetValue("Database:SnapshotRetentionCount", defaultValue: opts.SnapshotRetentionCount);
        opts.AsyncFlush = builder.Configuration
            .GetValue("Database:AsyncFlush", defaultValue: opts.AsyncFlush);
    });

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
    var jwtOptions = new JwtOptions
    {
        Secret = builder.Configuration["Jwt:Secret"]
            ?? PersistentKeyStore.GetOrCreate("jwt-secret", backendInstancePaths)
    };
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

    builder.Services.AddScoped<ProviderService>();
    builder.Services.AddScoped<ProviderCostService>();
    builder.Services.AddScoped<ModelService>();
    builder.Services.AddScoped<AgentService>();
    builder.Services.AddScoped<ChannelService>();
    builder.Services.AddScoped<ThreadService>();
    builder.Services.AddScoped<ContextService>();
    builder.Services.AddScoped<DefaultResourceSetService>();
    builder.Services.AddScoped<ToolAwarenessSetService>();
    builder.Services.AddScoped<AgentActionService>();
    builder.Services.AddScoped<AgentJobService>();

    // Host bridges — concrete services that adapt Core/Infrastructure to
    // the abstract IXxx contracts modules consume.  Modules only see the
    // interfaces from SharpClaw.Contracts.Modules.
    builder.Services.AddScoped<IAgentJobController, HostAgentJobController>();
    builder.Services.AddScoped<IAgentManager, HostAgentManager>();
    builder.Services.AddScoped<IAgentJobReader, HostAgentJobReader>();
    builder.Services.AddSingleton<IModelInfoProvider, HostModelInfoProvider>();
    builder.Services.AddScoped<IModelRegistrar, HostModelRegistrar>();
    builder.Services.AddScoped<IChatProcessingBridge, ChatProcessingBridge>();
    builder.Services.AddScoped<IContainerProvisioner, HostContainerProvisioner>();
    builder.Services.AddScoped<IThreadResolver, HostThreadResolver>();
    builder.Services.AddSingleton<IModuleLifecycleManager, HostModuleLifecycleManager>();
    builder.Services.AddSingleton<IModuleInfoProvider, HostModuleInfoProvider>();

    builder.Services.AddScoped<HeaderTagProcessor>();
    builder.Services.AddScoped<ChatService>();
    builder.Services.AddSingleton<ThreadActivitySignal>();
    builder.Services.AddScoped<RoleService>();

    // ──────── PHASE 9 ──── Task runtime + trigger host + host metric probes
    builder.Services.AddScoped<TaskPreflightChecker>();
    builder.Services.AddScoped<TaskTriggerRegistrar>();
    builder.Services.AddScoped<TaskService>();
    builder.Services.AddScoped<ITaskAuthoring>(sp => sp.GetRequiredService<TaskService>());
    builder.Services.AddScoped<ITaskInstanceLauncher, TaskInstanceLauncher>();
    builder.Services.AddScoped<TaskToolProvider>();
    builder.Services.AddScoped<ITaskToolCatalog>(sp => sp.GetRequiredService<TaskToolProvider>());
    builder.Services.AddScoped<IGlobalFlagEvaluator, GlobalFlagEvaluator>();
    builder.Services.AddScoped<EnvFileService>();
    builder.Services.AddScoped<TaskOrchestrator>();
    builder.Services.AddScoped<IHostAgentBridge, HostAgentBridge>();
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
    builder.Services.AddSingleton<ModuleMetricsCollector>();
    builder.Services.AddSingleton<ModuleEventDispatcher>();
    builder.Services.AddSingleton<ISharpClawEventSinkRegistry>(sp => sp.GetRequiredService<ModuleEventDispatcher>());
    builder.Services.AddScoped<ModuleExecutionContext>();
    builder.Services.AddScoped<IModuleConfigStore>(sp =>
    {
        var ctx = sp.GetRequiredService<ModuleExecutionContext>();
        var dbCtx = sp.GetRequiredService<SharpClaw.Infrastructure.Persistence.SharpClawDbContext>();
        return new ModuleConfigStore(dbCtx, ctx.ModuleId ?? "");
    });
    builder.Services.AddSingleton<ModuleHealthCheckService>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<ModuleHealthCheckService>());

    // ──────── PHASE 11 ─── Bundled-module discovery + ConfigureServices ────
    // Discover every bundled module's assembly, give each one a chance to
    // contribute DI services and task-step descriptors, and gather task-script
    // parser extensions.  ⚠ This MUST happen before builder.Build() because
    // the container is sealed at that point — disabled modules still need to
    // register their DbContext factories etc. so other services can resolve
    // optional dependencies on them lazily without throwing.
    var moduleLoader = ModuleLoader.DiscoverBundled();

    foreach (var bundledModule in moduleLoader.GetAllBundled())
    {
        bundledModule.ConfigureServices(builder.Services);
        if (bundledModule is ITaskParserAware parserAware)
            TaskScriptParser.RegisterModule(parserAware.ParserExtension);

        // Discover and register task-step descriptor providers from the
        // module's assembly so the central registry is populated before
        // any task script is parsed.
        var providerTypes = bundledModule.GetType().Assembly.GetTypes()
            .Where(t => !t.IsAbstract
                     && !t.IsInterface
                     && typeof(ITaskStepDescriptorProvider).IsAssignableFrom(t)
                     && t.GetConstructor(Type.EmptyTypes) is not null);
        foreach (var providerType in providerTypes)
        {
            var provider = (ITaskStepDescriptorProvider)Activator.CreateInstance(providerType)!;
            foreach (var descriptor in provider.Descriptors)
                TaskStepRegistry.Default.Register(descriptor);
        }
    }

    builder.Services.AddSingleton(moduleLoader);
    builder.Services.AddScoped<ModuleService>();

    // ──────── PHASE 12 ─── Misc post-module singletons ─────────────────────
    // (The legacy in-process scheduled-job loop has moved to
    //  DefaultModules/AgentOrchestration/ScheduledJobWorker; it is started
    //  from that module's InitializeAsync, not here.)
    builder.Services.AddSingleton<DatabaseInitializationGate>();

    // Seeding hosted service runs admin/role/provider seeding on startup.
    builder.Services.AddHostedService<SeedingService>();

    // Per-instance API key file consumed by ApiKeyMiddleware (PHASE 20).
    var apiKeyProvider = new ApiKeyProvider(backendInstancePaths);
    builder.Services.AddSingleton(apiKeyProvider);

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
        var migrationSvc = app.Services.GetService<SharpClaw.Infrastructure.Persistence.MigrationService>();
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

    // DbContext registry must be populated for ALL discovered bundled modules,
    // not just enabled ones. ConfigureServices already ran for every bundled
    // module (the DI container is immutable post-Build), so any scoped factory
    // delegate registered by a disabled module — e.g. the LlamaSharp module's
    // `sp => factory.CreateDbContext<LlamaSharpDbContext>()` — can still be
    // resolved lazily through optional dependencies on other services. If the
    // module's DbContext type is missing from the runtime registry that
    // resolution throws InvalidOperationException at request time. Registering
    // here is cheap (just type-table inserts) and does not load any JSON data.
    using (var scope = app.Services.CreateScope())
    {
        var moduleSvc = scope.ServiceProvider.GetRequiredService<ModuleService>();
        foreach (var bundledModule in allBundled)
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

        registry.Register(bundledModule);
        registeredBundledCount++;

        var manifest = moduleLoader.GetManifest(bundledModule.Id);
        var version = manifest?.Version ?? "unknown";
        Log.Information("Module '{ModuleId}' ({DisplayName}) registered [bundled, v{Version}]",
            bundledModule.Id, bundledModule.DisplayName, version);

        if (manifest is not null)
            registry.CacheManifest(bundledModule.Id, manifest);
    }

    if (storageMode == StorageMode.JsonFile)
    {
        using var moduleLoadScope = app.Services.CreateScope();
        var moduleSvc = moduleLoadScope.ServiceProvider.GetRequiredService<ModuleService>();
        foreach (var bundledModule in allBundled.Where(m => enabledModuleIds.Contains(m.Id)))
            await moduleSvc.LoadModulePersistenceAsync(bundledModule);
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
        app.Services.GetRequiredService<SharpClaw.Infrastructure.Persistence.Modules.RuntimeModuleDbContextRegistry>()
            .UnregisterModule(moduleId);
        registry.Unregister(moduleId);
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

            registry.Unregister(moduleId);
            app.Services.GetRequiredService<SharpClaw.Infrastructure.Persistence.Modules.RuntimeModuleDbContextRegistry>()
                .UnregisterModule(moduleId);
            failedInitCount++;
            continue;
        }

        try
        {
            await module.InitializeAsync(app.Services, CancellationToken.None);
            Log.Information("Module '{ModuleId}' initialized successfully [bundled]", moduleId);
            initializedCount++;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Module '{ModuleId}' failed to initialize — unregistering", moduleId);

            foreach (var export in module.ExportedContracts)
                failedContracts.Add(export.ContractName);

            registry.Unregister(moduleId);
            app.Services.GetRequiredService<SharpClaw.Infrastructure.Persistence.Modules.RuntimeModuleDbContextRegistry>()
                .UnregisterModule(moduleId);
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
        Log.Warning(ex, "Env-configured external module load failed — continuing");
    }

    // Module startup summary
    var totalLoaded = initializedCount + externalLoadedCount + envExternalLoadedCount;
    Log.Information(
        "Module startup complete: {TotalLoaded} loaded ({BundledInit} bundled, {ExternalLoaded} external, {EnvExternalLoaded} env-external), " +
        "{FailedInit} failed, {Disabled} disabled, {Excluded} excluded",
        totalLoaded, initializedCount, externalLoadedCount, envExternalLoadedCount,
        failedInitCount, disabledBundledCount, excludedModules.Count);

    // ──────── PHASE 18 ─── ApplicationStarted hook ────────────────────────
    // Start the JSON persistence FlushWorker only after the host signals
    // ApplicationStarted, so transient startup writes go through the
    // synchronous fallback rather than racing the worker's pump.
    app.Lifetime.ApplicationStarted.Register(() =>
    {
        var flushWorker = app.Services.GetService<FlushWorker>();
        flushWorker?.Start();
    });

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

    // ──────── PHASE 20 ─── HTTP pipeline (middleware order is load-bearing)
    //
    //   DiagnosticMiddleware (DEBUG only)        — request tracing for dev.
    //   DatabaseInitializationGateMiddleware     — 503s until PHASE 14 done.
    //   ExceptionHandlingMiddleware              — must wrap everything below.
    //   SerilogRequestLogging (optional)         — only when enabled in .env.
    //   UseCors / UseRouting                     — standard ASP.NET Core order.
    //   ApiKeyMiddleware                         — validates X-Api-Key.
    //   JwtSessionMiddleware                     — populates SessionService.
    //   MigrationGateMiddleware (relational)     — pauses traffic mid-migration.
    //   UseWebSockets                            — required for SSE/WS handlers.
#if DEBUG
    app.UseMiddleware<DiagnosticMiddleware>();
#endif
    app.UseMiddleware<DatabaseInitializationGateMiddleware>();
    app.UseMiddleware<ExceptionHandlingMiddleware>();
    if (serilogOptions.Enabled && serilogOptions.RequestLoggingEnabled)
        app.UseSerilogRequestLogging();
    app.UseCors();
    app.UseRouting();
    app.UseMiddleware<ApiKeyMiddleware>();
    app.UseMiddleware<JwtSessionMiddleware>();

    // Migration gate — pauses requests during manual migrations (relational only).
    if (storageMode != StorageMode.JsonFile)
        app.UseMiddleware<MigrationGateMiddleware>();

    app.UseWebSockets();

    // ──────── PHASE 21 ─── Endpoint mapping ───────────────────────────────
    // Liveness check — no auth required.
    app.MapGet("/echo", () => Results.Ok(new { status = "ok" }));

    // API key validation — requires valid X-Api-Key header.
    app.MapGet("/ping", () => Results.Ok(new { status = "authenticated" }));

    // Core attribute-discovered handlers (see SharpClaw.Application.API.Routing).
    app.MapHandlers();

    // Module-registered endpoints: each module maps its own REST routes.
    foreach (var module in registry.GetAllModules())
    {
        try
        {
            module.MapEndpoints(app);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Module '{ModuleId}' failed to map endpoints", module.Id);
        }
    }

    // Webhook trigger routes — registered lazily after ApplicationStarted so
    // that TaskTriggerHostService has loaded its first binding set.  The
    // WebhookRouteRegistry holds an IRouteBuilder reference and rebinds
    // routes whenever a webhook trigger source's binding set changes.
    var webhookSource = app.Services.GetRequiredService<IWebhookTriggerHost>();
    var webhookRegistry = new WebhookRouteRegistry(
        app,
        webhookSource,
        app.Services.GetRequiredService<Microsoft.Extensions.Logging.ILogger<WebhookRouteRegistry>>());
    webhookSource.SetRouteRegistrar(webhookRegistry);

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
