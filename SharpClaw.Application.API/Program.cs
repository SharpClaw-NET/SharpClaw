using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using SharpClaw.Application.API.Api;
using SharpClaw.Application.API.Cli;
using SharpClaw.Application.API.Handlers;
using SharpClaw.Application.API.Routing;
using SharpClaw.Application.Core.Clients;
using SharpClaw.Application.Services;
using SharpClaw.Application.Services.Auth;
using SharpClaw.Contracts.Persistence;
using SharpClaw.Infrastructure;
using SharpClaw.Infrastructure.Configuration;
using SharpClaw.Utils.Security;

var logsPath = Path.Combine(
    Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location)!,
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

    // Configuration: environment files
    builder.Configuration.AddLocalEnvironment(builder.Environment.IsDevelopment());

    builder.Host.UseSerilog();

    // Localhost only
    builder.WebHost.UseUrls("http://127.0.0.1:48923");

    // Infrastructure
    builder.Services.AddInfrastructure(StorageMode.JsonFile);

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
    builder.Services.AddSingleton<ProviderApiClientFactory>();

    // Transcription clients
    builder.Services.AddSingleton<ITranscriptionApiClient, OpenAiTranscriptionApiClient>();
    builder.Services.AddSingleton<ITranscriptionApiClient, GroqTranscriptionApiClient>();
    builder.Services.AddSingleton<TranscriptionApiClientFactory>();

    // Audio capture
    builder.Services.AddSingleton<IAudioCaptureProvider, WasapiAudioCaptureProvider>();

    builder.Services.AddScoped<ProviderService>();
    builder.Services.AddScoped<ModelService>();
    builder.Services.AddScoped<AgentService>();
    builder.Services.AddScoped<ConversationService>();
    builder.Services.AddScoped<ContextService>();
    builder.Services.AddScoped<AgentActionService>();
    builder.Services.AddScoped<AgentJobService>();
    builder.Services.AddScoped<ChatService>();
    builder.Services.AddSingleton<LiveTranscriptionOrchestrator>();
    builder.Services.AddScoped<TranscriptionService>();

    // Background tasks
    builder.Services.AddHostedService<ScheduledTaskService>();

    // Seeding
    builder.Services.AddHostedService<SeedingService>();

    // API key
    var apiKeyProvider = new ApiKeyProvider();
    builder.Services.AddSingleton(apiKeyProvider);

    var app = builder.Build();

    // Initialize infrastructure (loads persisted data into InMemory DB)
    await app.Services.InitializeInfrastructureAsync();

    // CLI mode: handle command and exit
    if (await CliDispatcher.TryHandleAsync(args, app.Services))
        return;

    // API mode
    app.UseSerilogRequestLogging();
    app.UseMiddleware<ApiKeyMiddleware>();
    app.UseMiddleware<JwtSessionMiddleware>();
    app.UseWebSockets();
    app.MapHandlers();
    app.MapTranscriptionStreaming();

    app.Lifetime.ApplicationStopping.Register(apiKeyProvider.Cleanup);

    Log.Information("SharpClaw API listening on http://127.0.0.1:48923");
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
