using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using LLama.Native;
using Mk8.Shell.Models;
using Serilog;
using SharpClaw.Application.API;
using SharpClaw.Application.API.Api;
using SharpClaw.Application.API.Cli;
using SharpClaw.Application.API.Handlers;
using SharpClaw.Application.API.Routing;
using SharpClaw.Application.API.Webhooks;
using SharpClaw.Application.Core.Clients;
using SharpClaw.Application.Core.LocalInference;
using SharpClaw.Application.Core.Modules;
using SharpClaw.Application.Core.Providers;
using SharpClaw.Application.Infrastructure.Tasks;
using SharpClaw.Application.Core.Services.Triggers.Sources;
using SharpClaw.Application.Core.Services;
using SharpClaw.Application.Services;
using SharpClaw.Application.Infrastructure.Logging;
using SharpClaw.Application.Infrastructure.Tasks.Parsing;
using SharpClaw.Application.Services.Auth;
using SharpClaw.Contracts.Modules;
using SharpClaw.Contracts.Persistence;
using SharpClaw.Contracts.Providers;
using SharpClaw.Providers.Common;
using SharpClaw.Infrastructure;
using SharpClaw.Infrastructure.Configuration;
using SharpClaw.Utils.Logging;
using SharpClaw.Utils.Instances;
using Microsoft.EntityFrameworkCore;
using SharpClaw.Utils.Security;
using Serilog.Events;
using SharpClaw.Contracts.Tasks;
using SharpClaw.Application.Core.Services.Triggers;
using SharpClaw.Infrastructure.Persistence.JSON;

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

// L-015: Configure the LLamaSharp native backend before *any* LLama API is touched.
// NativeLibraryConfig is sticky — the first call to a LLama API freezes the backend
// selection, so this must run prior to DI registration of LocalInferenceProcessManager
// (and ahead of any code path that may probe llama.cpp). A startup diagnostic line is
// emitted so operators can confirm which backend (CUDA/Vulkan/CPU) was chosen.
NativeLibraryConfig.All
    .WithCuda(true)
    .WithVulkan(true)
    .WithAutoFallback(true)
    .WithLogCallback((level, message) =>
    {
        // Only surface warnings/errors through debug output; suppress the
        // hundreds of info lines (tensor loads, layer assignments, etc.)
        // that llama.cpp dumps to stderr during model loading.
        if (level >= LLamaLogLevel.Warning)
        {
            System.Diagnostics.Debug.WriteLine($"[llama.cpp] {message?.TrimEnd()}", "SharpClaw.CLI");
            sessionLogs.AppendDebug($"[llama.cpp] {message?.TrimEnd()}");
        }
    });
Log.Information(
    "LLamaSharp native backend configured (preference: CUDA > Vulkan > CPU, AutoFallback=true).");

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

    // Domain services
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
    // Provider plugins are registered via BuiltInProviderPlugins.Build below
    // (after LocalInferenceProcessManager is constructed). The factory itself
    // resolves over IEnumerable<IProviderPlugin>.
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
    builder.Services.AddScoped<IAgentJobController, HostAgentJobController>();
    builder.Services.AddScoped<IAgentManager, HostAgentManager>();
    builder.Services.AddScoped<IAgentJobReader, HostAgentJobReader>();
    builder.Services.AddSingleton<IModelInfoProvider, HostModelInfoProvider>();
    builder.Services.AddScoped<IContextDataReader, HostContextDataReader>();
    builder.Services.AddScoped<IContainerProvisioner, HostContainerProvisioner>();
    builder.Services.AddScoped<IThreadResolver, HostThreadResolver>();
    builder.Services.AddSingleton<IModuleLifecycleManager, HostModuleLifecycleManager>();
    builder.Services.AddSingleton<IModuleInfoProvider, HostModuleInfoProvider>();
    builder.Services.AddScoped<HeaderTagProcessor>();
    builder.Services.AddScoped<ChatService>();
    builder.Services.AddSingleton<ThreadActivitySignal>();
    builder.Services.AddScoped<RoleService>();
    builder.Services.AddScoped<TaskPreflightChecker>();
    builder.Services.AddScoped<TaskTriggerRegistrar>();
    builder.Services.AddScoped<TaskService>();
    builder.Services.AddScoped<ScheduledJobService>();
    builder.Services.AddScoped<TaskToolProvider>();
    builder.Services.AddScoped<EnvFileService>();
    builder.Services.AddScoped<TaskOrchestrator>();
    builder.Services.AddSingleton<TaskRuntimeHost>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<TaskRuntimeHost>());
    // Trigger host service + built-in sources
    builder.Services.AddSingleton<TaskTriggerHostService>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<TaskTriggerHostService>());
    builder.Services.AddSingleton<EventBusTriggerSource>();
    builder.Services.AddSingleton<ITaskTriggerSource>(sp => sp.GetRequiredService<EventBusTriggerSource>());
    builder.Services.AddSingleton<ISharpClawEventSink>(sp => sp.GetRequiredService<EventBusTriggerSource>());
    builder.Services.AddSingleton<ITaskTriggerSource, FileChangedTriggerSource>();
    builder.Services.AddSingleton<ITaskTriggerSource, HostProbeTriggerSource>();
    builder.Services.AddSingleton<ITaskTriggerSource, NetworkTriggerSource>();
    builder.Services.AddSingleton<ITaskTriggerSource, MetricTriggerSource>();
    builder.Services.AddSingleton<ITaskTriggerSource, LifecycleTriggerSource>();
    builder.Services.AddSingleton<WebhookTriggerSource>();
    builder.Services.AddSingleton<ITaskTriggerSource>(sp => sp.GetRequiredService<WebhookTriggerSource>());
    builder.Services.AddSingleton<ITaskMetricProvider, PendingJobCountMetricProvider>();
    builder.Services.AddSingleton<ITaskMetricProvider, PendingTaskCountMetricProvider>();
    builder.Services.AddSingleton<ITaskMetricProvider, SchedulerPendingJobCountMetricProvider>();
    // Module system
    builder.Services.AddSingleton<ModuleRegistry>();
    builder.Services.AddSingleton<ModuleMetricsCollector>();
    builder.Services.AddSingleton<ModuleEventDispatcher>();
    builder.Services.AddScoped<ModuleExecutionContext>();
    builder.Services.AddScoped<IModuleConfigStore>(sp =>
    {
        var ctx = sp.GetRequiredService<ModuleExecutionContext>();
        var dbCtx = sp.GetRequiredService<SharpClaw.Infrastructure.Persistence.SharpClawDbContext>();
        return new ModuleConfigStore(dbCtx, ctx.ModuleId ?? "");
    });
    builder.Services.AddSingleton<ModuleHealthCheckService>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<ModuleHealthCheckService>());

    // Default modules — discovered from loaded assemblies.
    // DI services must be registered for ALL modules before Build (container is immutable after).
    var moduleLoader = ModuleLoader.DiscoverBundled();

    foreach (var bundledModule in moduleLoader.GetAllBundled())
    {
        bundledModule.ConfigureServices(builder.Services);
        if (bundledModule is ITaskParserAware parserAware)
            TaskScriptParser.RegisterModule(parserAware.ParserExtension);
    }

    builder.Services.AddSingleton(moduleLoader);
    builder.Services.AddScoped<ModuleService>();

    // Local inference
    // Native library backend was configured before host build (see L-015 block above).

    var processManager = new LocalInferenceProcessManager();
    if (int.TryParse(builder.Configuration["Local:GpuLayerCount"], out var gpuLayers))
        processManager.DefaultGpuLayerCount = gpuLayers;
    if (uint.TryParse(builder.Configuration["Local:ContextSize"], out var ctxSize))
        processManager.DefaultContextSize = ctxSize;
    if (int.TryParse(builder.Configuration["Local:IdleCooldownMinutes"], out var cooldownMin))
        processManager.IdleCooldown = TimeSpan.FromMinutes(cooldownMin);
    if (bool.TryParse(builder.Configuration["Local:KeepLoaded"], out var keepLoaded))
        processManager.KeepLoaded = keepLoaded;
    if (int.TryParse(builder.Configuration["Local:MaxCachedSessions"], out var maxCachedSessions) && maxCachedSessions > 0)
        processManager.MaxCachedSessions = maxCachedSessions;
    builder.Services.AddSingleton(processManager);
    builder.Services.AddSingleton(sp => new LocalInferenceApiClient(sp.GetRequiredService<LocalInferenceProcessManager>()));

    // Register the seventeen built-in provider plugins. Phases 6-9 carve
    // these into per-protocol modules; until then they live inside Core.
    foreach (var plugin in BuiltInProviderPlugins.Build(processManager))
        builder.Services.AddSingleton(plugin);
    builder.Services.AddSingleton<HuggingFaceUrlResolver>();
    builder.Services.AddSingleton<ModelDownloadManager>();
    builder.Services.AddScoped<LocalModelService>();

    // Background tasks
    builder.Services.AddHostedService<ScheduledTaskService>();

    builder.Services.AddSingleton<DatabaseInitializationGate>();

    // Seeding
    builder.Services.AddHostedService<SeedingService>();

    // API key
    var apiKeyProvider = new ApiKeyProvider(backendInstancePaths);
    builder.Services.AddSingleton(apiKeyProvider);

    // CLI short-ID resolver (used by module CLI handlers)
    builder.Services.AddSingleton<ICliIdResolver, CliIdResolver>();

    var app = builder.Build();

    // Configure JSON serialisation for minimal API responses to match
    // the conventions used throughout the solution: camelCase + string enums.
    app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>()
        .Value.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());

    // Initialize infrastructure (loads persisted data into InMemory DB) before
    // any command, hosted-service, or HTTP handler can execute against it.
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

    // Wire up module loader with built service provider
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
        using (var scope = app.Services.CreateScope())
        {
            var moduleSvc = scope.ServiceProvider.GetRequiredService<ModuleService>();
            moduleSvc.RegisterModulePersistence(bundledModule);
        }
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

    // Initialize enabled modules in dependency order (providers before consumers).
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

    // Scan external-modules directory and hot-load any found modules
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

    // Seed mk8.shell base env on first startup
    Mk8GlobalEnv.Load();

    app.Lifetime.ApplicationStarted.Register(() =>
    {
        var flushWorker = app.Services.GetService<FlushWorker>();
        flushWorker?.Start();
    });

    // CLI mode: handle command and exit
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

    // API mode
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

    // Liveness check — no auth required.
    app.MapGet("/echo", () => Results.Ok(new { status = "ok" }));

    // API key validation — requires valid X-Api-Key header.
    app.MapGet("/ping", () => Results.Ok(new { status = "authenticated" }));

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
    // that TaskTriggerHostService has loaded its first binding set.
    var webhookSource = app.Services.GetRequiredService<WebhookTriggerSource>();
    var webhookRegistry = new WebhookRouteRegistry(
        app,
        webhookSource,
        app.Services.GetRequiredService<Microsoft.Extensions.Logging.ILogger<WebhookRouteRegistry>>());
    webhookSource.SetRouteRegistrar(webhookRegistry);

    app.Lifetime.ApplicationStopping.Register(() =>
    {
        apiKeyProvider.Cleanup();
        backendInstancePaths.DeleteDiscoveryEntry();
    });

    // Module lifecycle: graceful shutdown
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

    // Interactive-mode console logging discipline: when the REPL is actually
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
