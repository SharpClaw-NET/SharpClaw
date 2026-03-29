using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
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
using SharpClaw.Application.Services;
using SharpClaw.Application.Services.Auth;
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

    // Infrastructure
    builder.Services.AddInfrastructure(StorageMode.JsonFile, configureJsonFile: opts =>
    {
        if (!string.IsNullOrEmpty(dataDir))
            opts.DataDirectory = dataDir;
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
    var encryptionKeyBase64 = builder.Configuration["Encryption:Key"]
        ?? PersistentKeyStore.GetOrCreate("encryption-key");
    var encryptionOptions = new EncryptionOptions
    {
        Key = Convert.FromBase64String(encryptionKeyBase64)
    };
    builder.Services.AddSingleton(encryptionOptions);

    builder.Services.AddTransient<HttpLoggingDelegatingHandler>();
    builder.Services.AddHttpClient()
        .ConfigureHttpClientDefaults(b => b.AddHttpMessageHandler<HttpLoggingDelegatingHandler>());
    builder.Services.AddSingleton<IProviderApiClient, OpenAiApiClient>();
    builder.Services.AddSingleton<IProviderApiClient, AnthropicApiClient>();
    builder.Services.AddSingleton<IProviderApiClient, OpenRouterApiClient>();
    builder.Services.AddSingleton<IProviderApiClient, GoogleVertexAIApiClient>();
    builder.Services.AddSingleton<IProviderApiClient, GoogleGeminiApiClient>();
    builder.Services.AddSingleton<IProviderApiClient, ZAIApiClient>();
    builder.Services.AddSingleton<IProviderApiClient, VercelAIGatewayApiClient>();
    builder.Services.AddSingleton<IProviderApiClient, XAIApiClient>();
    builder.Services.AddSingleton<IProviderApiClient, GroqApiClient>();
    builder.Services.AddSingleton<IProviderApiClient, CerebrasApiClient>();
    builder.Services.AddSingleton<IProviderApiClient, MistralApiClient>();
    builder.Services.AddSingleton<IProviderApiClient, GitHubCopilotApiClient>();
    builder.Services.AddSingleton<IProviderApiClient, MinimaxApiClient>();
    builder.Services.AddSingleton<ProviderApiClientFactory>();

    // Transcription clients
    builder.Services.AddSingleton<ITranscriptionApiClient, OpenAiTranscriptionApiClient>();
    builder.Services.AddSingleton<ITranscriptionApiClient, GroqTranscriptionApiClient>();
    builder.Services.AddSingleton<WhisperModelManager>();
    builder.Services.AddSingleton<ITranscriptionApiClient, LocalTranscriptionClient>();
    builder.Services.AddSingleton<TranscriptionApiClientFactory>();

    // Audio capture
    builder.Services.AddSingleton<IAudioCaptureProvider, WasapiAudioCaptureProvider>();
    builder.Services.AddSingleton<SharedAudioCaptureManager>();

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
    builder.Services.AddSingleton<LiveTranscriptionOrchestrator>();
    builder.Services.AddScoped<TranscriptionService>();
    builder.Services.AddScoped<ContainerService>();
    builder.Services.AddScoped<DisplayDeviceService>();
    builder.Services.AddScoped<DefaultResourceSetService>();
    builder.Services.AddScoped<ToolAwarenessSetService>();
    builder.Services.AddScoped<EditorSessionService>();
    builder.Services.AddSingleton<EditorBridgeService>();
    builder.Services.AddScoped<TaskService>();
    builder.Services.AddScoped<EnvFileService>();
    builder.Services.AddScoped<TaskOrchestrator>();
    builder.Services.AddScoped<BotIntegrationService>();
    builder.Services.AddScoped<BotMessageSenderService>();

    // Local inference (in-process via LLamaSharp)
    // Configure native library: prefer CUDA > Vulkan > CPU; suppress verbose logs.
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

    var processManager = new LocalInferenceProcessManager();
    if (int.TryParse(builder.Configuration["Local:GpuLayerCount"], out var gpuLayers))
        processManager.DefaultGpuLayerCount = gpuLayers;
    if (uint.TryParse(builder.Configuration["Local:ContextSize"], out var ctxSize))
        processManager.DefaultContextSize = ctxSize;
    if (int.TryParse(builder.Configuration["Local:IdleCooldownMinutes"], out var cooldownMin))
        processManager.IdleCooldown = TimeSpan.FromMinutes(cooldownMin);
    if (bool.TryParse(builder.Configuration["Local:KeepLoaded"], out var keepLoaded))
        processManager.KeepLoaded = keepLoaded;
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

    var app = builder.Build();

    // Configure JSON serialisation for minimal API responses to match
    // the conventions used throughout the solution: camelCase + string enums.
    app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>()
        .Value.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());

    // Initialize infrastructure (loads persisted data into InMemory DB)
    await app.Services.InitializeInfrastructureAsync();

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
    app.UseMiddleware<ApiKeyMiddleware>();
    app.UseMiddleware<JwtSessionMiddleware>();
    app.UseWebSockets();

    // Liveness check — no auth required.
    app.MapGet("/echo", () => Results.Ok(new { status = "ok" }));

    // API key validation — requires valid X-Api-Key header.
    app.MapGet("/ping", () => Results.Ok(new { status = "authenticated" }));

    app.MapHandlers();
    app.MapEditorEndpoints();
    app.MapTranscriptionStreaming();

    app.Lifetime.ApplicationStopping.Register(apiKeyProvider.Cleanup);

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
