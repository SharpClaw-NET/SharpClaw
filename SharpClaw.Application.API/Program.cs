using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using LLama.Native;
using Mk8.Shell.Models;
using Serilog;
using SharpClaw.Application.API.Api;
using SharpClaw.Application.API.Cli;
using SharpClaw.Application.API.Handlers;
using SharpClaw.Application.API.Routing;
using SharpClaw.Application.Core.Clients;
using SharpClaw.Application.Core.LocalInference;
using SharpClaw.Application.Core.Modules;
using SharpClaw.Application.Services;
using SharpClaw.Application.Infrastructure.Logging;
using SharpClaw.Contracts.Persistence;
using SharpClaw.Application.Services.Auth;
using SharpClaw.Contracts.Modules;
using SharpClaw.Contracts.Persistence;
using SharpClaw.Infrastructure;
using SharpClaw.Infrastructure.Configuration;
using SharpClaw.Utils.Security;

var dataDir = Environment.GetEnvironmentVariable("SHARPCLAW_DATA_DIR");
var baseDir = !string.IsNullOrEmpty(dataDir)
    ? dataDir
    : Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location)!;

var logsPath = Path.Combine(
    !string.IsNullOrEmpty(dataDir) ? Path.GetDirectoryName(dataDir)! : baseDir,
    "Logs", "sharpclaw-.log");

var consoleLevelSwitch = new Serilog.Core.LoggingLevelSwitch(Serilog.Events.LogEventLevel.Information);

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug()
    .WriteTo.Console(levelSwitch: consoleLevelSwitch)
    .WriteTo.File(logsPath, rollingInterval: RollingInterval.Day)
    .Enrich.FromLogContext()
    .CreateLogger();

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
            System.Diagnostics.Debug.WriteLine($"[llama.cpp] {message?.TrimEnd()}", "SharpClaw.CLI");
    });
Log.Information(
    "LLamaSharp native backend configured (preference: CUDA > Vulkan > CPU, AutoFallback=true).");

try
{
    var builder = WebApplication.CreateBuilder(args);

    // Ensure the API always binds to the expected port, regardless of
    // whether a launch profile is active.  ASPNETCORE_URLS env var
    // (set by BackendProcessManager) takes precedence if present.
    if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ASPNETCORE_URLS")))
        builder.WebHost.UseUrls("http://127.0.0.1:48923");

    // Configuration: environment files
    builder.Configuration.AddLocalEnvironment(builder.Environment.IsDevelopment());

    builder.Host.UseSerilog();

    // Module log capture — feeds per-module ring buffers for the /modules/{id}/logs API.
    var moduleLogService = new ModuleLogService();
    builder.Services.AddSingleton(moduleLogService);
    builder.Services.AddSingleton<Microsoft.Extensions.Logging.ILoggerProvider>(
        new ModuleLogSinkProvider(moduleLogService));

    // Encryption key — resolved early so Infrastructure can use it for JSON file encryption.
    var encryptionKeyBase64 = builder.Configuration["Encryption:Key"]
        ?? PersistentKeyStore.GetOrCreate("encryption-key");
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
        opts.EncryptAtRest = builder.Configuration
            .GetValue("Encryption:EncryptDatabase", defaultValue: true);
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
            ?? PersistentKeyStore.GetOrCreate("jwt-secret")
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
    builder.Services.AddSingleton<IProviderApiClient, OpenAiApiClient>();
    builder.Services.AddSingleton<IProviderApiClient, AnthropicApiClient>();
    builder.Services.AddSingleton<IProviderApiClient, OpenRouterApiClient>();
    builder.Services.AddSingleton<IProviderApiClient, GoogleVertexAIApiClient>();
    builder.Services.AddSingleton<IProviderApiClient, GoogleVertexAIOpenAiApiClient>();
    builder.Services.AddSingleton<IProviderApiClient, GoogleGeminiApiClient>();
    builder.Services.AddSingleton<IProviderApiClient, GoogleGeminiOpenAiApiClient>();
    builder.Services.AddSingleton<IProviderApiClient, ZAIApiClient>();
    builder.Services.AddSingleton<IProviderApiClient, VercelAIGatewayApiClient>();
    builder.Services.AddSingleton<IProviderApiClient, XAIApiClient>();
    builder.Services.AddSingleton<IProviderApiClient, GroqApiClient>();
    builder.Services.AddSingleton<IProviderApiClient, CerebrasApiClient>();
    builder.Services.AddSingleton<IProviderApiClient, MistralApiClient>();
    builder.Services.AddSingleton<IProviderApiClient, GitHubCopilotApiClient>();
    builder.Services.AddSingleton<IProviderApiClient, MinimaxApiClient>();
    builder.Services.AddSingleton<IProviderApiClient, OllamaApiClient>();
    builder.Services.AddSingleton<ProviderApiClientFactory>();

    builder.Services.AddScoped<ProviderService>();
    builder.Services.AddScoped<ProviderCostService>();
    builder.Services.AddScoped<ModelService>();
    builder.Services.AddScoped<AgentService>();
    builder.Services.AddScoped<ChannelService>();
    builder.Services.AddScoped<ThreadService>();
    builder.Services.AddScoped<ContextService>();
    builder.Services.AddScoped<AgentActionService>();
    builder.Services.AddScoped<AgentJobService>();
    builder.Services.AddScoped<HeaderTagProcessor>();
    builder.Services.AddScoped<ChatService>();
    builder.Services.AddSingleton<ThreadActivitySignal>();
    builder.Services.AddScoped<RoleService>();
    builder.Services.AddScoped<TaskService>();
    builder.Services.AddScoped<EnvFileService>();
    builder.Services.AddScoped<TaskOrchestrator>();
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
        bundledModule.ConfigureServices(builder.Services);

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
    builder.Services.AddSingleton<IProviderApiClient>(sp => sp.GetRequiredService<LocalInferenceApiClient>());
    builder.Services.AddSingleton<HuggingFaceUrlResolver>();
    builder.Services.AddSingleton<ModelDownloadManager>();
    builder.Services.AddScoped<LocalModelService>();

    // Background tasks
    builder.Services.AddHostedService<ScheduledTaskService>();

    // Seeding
    builder.Services.AddHostedService<SeedingService>();

    // API key
    var apiKeyProvider = new ApiKeyProvider();
    builder.Services.AddSingleton(apiKeyProvider);

    // CLI short-ID resolver (used by module CLI handlers)
    builder.Services.AddSingleton<ICliIdResolver, CliIdResolver>();

    var app = builder.Build();

    // Configure JSON serialisation for minimal API responses to match
    // the conventions used throughout the solution: camelCase + string enums.
    app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>()
        .Value.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());

    // Initialize infrastructure (loads persisted data into InMemory DB)
    await app.Services.InitializeInfrastructureAsync();

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
        registeredBundledCount++;

        var manifest = moduleLoader.GetManifest(bundledModule.Id);
        var version = manifest?.Version ?? "unknown";
        Log.Information("Module '{ModuleId}' ({DisplayName}) registered [bundled, v{Version}]",
            bundledModule.Id, bundledModule.DisplayName, version);

        if (manifest is not null)
            registry.CacheManifest(bundledModule.Id, manifest);
    }

    Log.Information("Bundled modules: {Registered} registered, {Disabled} disabled, {Total} discovered",
        registeredBundledCount, disabledBundledCount, allBundled.Count);

    // Initialize enabled modules in dependency order (providers before consumers).
    var initOrder = registry.GetInitializationOrder(out var excludedModules);

    // Unregister modules excluded during dependency resolution (missing deps, cycles).
    foreach (var (moduleId, reason) in excludedModules)
    {
        Log.Warning("Module '{ModuleId}' excluded from initialization: {Reason}", moduleId, reason);
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
    app.UseMiddleware<ExceptionHandlingMiddleware>();
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

    app.Lifetime.ApplicationStopping.Register(apiKeyProvider.Cleanup);

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

    var urls = string.Join(", ", app.Urls);
    Log.Information("SharpClaw API listening on {Urls}", urls);
    Log.Information("API key written to: {KeyFilePath}", apiKeyProvider.KeyFilePath);

    await app.StartAsync();

    // Suppress console logging during interactive mode (file logging continues).
    // CLI command/response logs go to System.Diagnostics.Debug (VS Output > Debug pane).
    consoleLevelSwitch.MinimumLevel = Serilog.Events.LogEventLevel.Fatal;
    await CliDispatcher.RunInteractiveAsync(app.Services, app.Lifetime.ApplicationStopping);
    consoleLevelSwitch.MinimumLevel = Serilog.Events.LogEventLevel.Information;

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
