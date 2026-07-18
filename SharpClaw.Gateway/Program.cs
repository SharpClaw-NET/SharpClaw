using SharpClaw.Gateway.Contracts;
using SharpClaw.Gateway.Configuration;
using SharpClaw.Gateway.Controllers;
using SharpClaw.Gateway.Infrastructure;
using SharpClaw.Gateway.Modules;
using SharpClaw.Gateway.Security;
using SharpClaw.Shared.Logging;
using SharpClaw.Shared.Instances;
using Serilog;
using Serilog.Events;
using SharpClaw.Gateway.Modules.Routing;
using SharpClaw.Gateway.Modules.Hosting;

var builder = WebApplication.CreateBuilder(args);

var gatewayPaths = new SharpClawInstancePaths(
    SharpClawInstanceKind.Gateway,
    Environment.GetEnvironmentVariable("SHARPCLAW_INSTANCE_ROOT"),
    Environment.GetEnvironmentVariable("SHARPCLAW_SHARED_ROOT"));
gatewayPaths.EnsureDirectories();
gatewayPaths.CleanupStaleDiscoveryEntries(TimeSpan.FromMinutes(2));
using var gatewayInstanceLock = new SharpClawInstanceLock(gatewayPaths);

builder.Configuration.AddGatewayEnvironment(
    isDevelopment: builder.Environment.IsDevelopment());

var gatewayManifest = gatewayPaths.Manifest;
var configuredGatewayUrl = builder.Configuration["ASPNETCORE_URLS"]
    ?? Environment.GetEnvironmentVariable("ASPNETCORE_URLS");
var selectedBackendBaseUrl = builder.Configuration["SharpClawInstance:SelectedBackendBaseUrl"]
    ?? builder.Configuration[$"{InternalApiOptions.SectionName}:BaseUrl"];
var selectedBackendInstanceId = builder.Configuration["SharpClawInstance:SelectedBackendInstanceId"];
var selectedBackendBindingKind = builder.Configuration["SharpClawInstance:SelectedBackendBindingKind"];
var gatewayManifestChanged = false;

if (!string.Equals(gatewayManifest.BaseUrl, configuredGatewayUrl, StringComparison.OrdinalIgnoreCase))
{
    gatewayManifest.BaseUrl = configuredGatewayUrl;
    gatewayManifestChanged = true;
}

if (!string.Equals(gatewayManifest.SelectedBackendBaseUrl, selectedBackendBaseUrl, StringComparison.OrdinalIgnoreCase))
{
    gatewayManifest.SelectedBackendBaseUrl = selectedBackendBaseUrl;
    gatewayManifestChanged = true;
}

if (!string.Equals(gatewayManifest.SelectedBackendInstanceId, selectedBackendInstanceId, StringComparison.Ordinal))
{
    gatewayManifest.SelectedBackendInstanceId = selectedBackendInstanceId;
    gatewayManifestChanged = true;
}

if (!string.Equals(gatewayManifest.SelectedBackendBindingKind, selectedBackendBindingKind, StringComparison.Ordinal))
{
    gatewayManifest.SelectedBackendBindingKind = selectedBackendBindingKind;
    gatewayManifestChanged = true;
}

if (gatewayManifestChanged)
    gatewayPaths.SaveManifest(gatewayManifest);

var loggingOptions = SharpClawLoggingOptions.FromConfiguration(builder.Configuration);
await using var logging = SharpClawLogRuntime.Create(
    "gateway",
    gatewayPaths,
    loggingOptions);
var startupLogger = logging.SerilogLogger;

var publishedGatewayUrl = !string.IsNullOrWhiteSpace(configuredGatewayUrl)
    ? configuredGatewayUrl
    : gatewayManifest.BaseUrl ?? "http://127.0.0.1:48924";
using var gatewayDiscoveryLease = new SharpClawDiscoveryLease(
    gatewayPaths,
    publishedGatewayUrl,
    TimeSpan.FromSeconds(30));
gatewayDiscoveryLease.PublishNow();

AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
{
    if (eventArgs.ExceptionObject is Exception exception)
        startupLogger.Error(exception, "Unhandled AppDomain exception in gateway.");
    else
        startupLogger.Error(
            "Unhandled AppDomain exception payload: {ExceptionObject}",
            eventArgs.ExceptionObject);
};

TaskScheduler.UnobservedTaskException += (_, eventArgs) =>
{
    startupLogger.Error(eventArgs.Exception, "Unobserved task exception in gateway.");
};

builder.Logging.ClearProviders();
builder.Host.UseSerilog(logging.SerilogLogger, dispose: false);
builder.Services.AddSingleton(logging);

builder.Services.Configure<InternalApiOptions>(
    builder.Configuration.GetSection(InternalApiOptions.SectionName));

builder.Services.AddHttpClient<InternalApiClient>(client =>
{
    var section = builder.Configuration.GetSection(InternalApiOptions.SectionName);
    client.BaseAddress = new Uri(section["BaseUrl"] ?? "http://127.0.0.1:48923");
    client.Timeout = int.TryParse(section["TimeoutSeconds"], out var t) && t > 0
        ? TimeSpan.FromSeconds(t)
        : TimeSpan.FromSeconds(300);
});

// ── Gateway endpoint configuration ──────────────────────────────
builder.Services.Configure<GatewayEndpointOptions>(
    builder.Configuration.GetSection(GatewayEndpointOptions.SectionName));

// ── Gateway-side module discovery (Phase 2) ─────────────────────
// Loader runs here so DI can hand the catalog/loader to middleware,
// but MapEndpoints / ConfigureGatewayServices stays deferred to Phase 3.
builder.Services.Configure<GatewayModuleOptions>(
    builder.Configuration.GetSection(GatewayModuleOptions.SectionName));

var gatewayModuleLoader = GatewayModuleLoader.DiscoverBundled(startupLogger);
foreach (var ext in gatewayModuleLoader.All)
{
    startupLogger.Information(
        "Gateway module discovered: {ModuleId} ({DisplayName})",
        ext.ModuleId,
        ext.DisplayName);
}

builder.Services.AddSingleton(gatewayModuleLoader);
builder.Services.AddSingleton<GatewayEndpointGroupCatalog>();
builder.Services.AddSingleton<ModuleEndpointDataSource>();
builder.Services.AddSingleton<GatewayModuleHostManager>();

// ── Gateway-side module service registration (Phase 3) ─────────
// Run ConfigureGatewayServices only for modules explicitly enabled in
// configuration so a disabled module's services don't leak into DI.
var gatewayModuleOptionsSnapshot = builder.Configuration
    .GetSection(GatewayModuleOptions.SectionName)
    .Get<GatewayModuleOptions>() ?? new GatewayModuleOptions();
foreach (var ext in gatewayModuleLoader.All)
{
    if (!gatewayModuleOptionsSnapshot.IsModuleEnabled(ext.ModuleId))
        continue;

    try
    {
        ext.ConfigureGatewayServices(builder.Services);
        startupLogger.Information("Gateway module services configured: {ModuleId}", ext.ModuleId);
    }
    catch (Exception ex)
    {
        startupLogger.Error(ex,
            "Gateway module {ModuleId} threw during ConfigureGatewayServices; module will not be mapped.",
            ext.ModuleId);
    }
}

// ── Request queue (sequential forwarding to core API) ────────────
builder.Services.Configure<RequestQueueOptions>(
    builder.Configuration.GetSection(RequestQueueOptions.SectionName));

builder.Services.AddSingleton<QueueMetrics>();
builder.Services.AddSingleton<RequestQueueService>();
builder.Services.AddHostedService<RequestQueueProcessor>();
builder.Services.AddScoped<GatewayRequestDispatcher>();
builder.Services.AddHttpContextAccessor();

// ── Security
builder.Services.AddSingleton<IpBanService>();
builder.Services.AddSharpClawRateLimiting();

// ── MVC & OpenAPI ────────────────────────────────────────────────
builder.Services.AddControllers(options =>
    {
        options.Filters.Add<ErrorEnvelopeFilter>();
    })
    .AddJsonOptions(o =>
    {
        o.JsonSerializerOptions.Converters.Add(
            new System.Text.Json.Serialization.JsonStringEnumConverter());
    });
builder.Services.AddOpenApi(options =>
{
    options.AddDocumentTransformer((doc, _, _) =>
    {
        doc.Info.Title = "SharpClaw Gateway";
        doc.Info.Version = "v1";
        doc.Info.Description = "Public REST gateway for the SharpClaw Runtime Host.";
        return Task.CompletedTask;
    });
});

// ── API key diagnostic (visible in Uno process output) ───────────
var configuredApiKey = builder.Configuration[$"{InternalApiOptions.SectionName}:ApiKey"];
if (!string.IsNullOrEmpty(configuredApiKey))
    startupLogger.Debug("Internal API key resolved from configuration.");
else
    startupLogger.Debug("Internal API key was not present in configuration; file resolution remains available.");

var app = builder.Build();

// ── Response telemetry headers ───────────────────────────────────
app.Use(async (context, next) =>
{
    // Set RequestId early so error envelopes in downstream middleware can use it
    var requestId = Guid.NewGuid().ToString("N");
    context.Items["RequestId"] = requestId;

    context.Response.OnStarting(() =>
    {
        var queueSvc = context.RequestServices.GetService<RequestQueueService>();
        var meta = context.Items.TryGetValue("QueueMeta", out var m) && m is QueueResponseMeta qm
            ? qm : null;

        // X-Request-Id — correlation ID on every response (prefer queue's if available)
        context.Response.Headers["X-Request-Id"] = meta?.RequestId.ToString("N") ?? requestId;

        // X-RateLimit-Limit — applicable rate limit for this path
        var path = context.Request.Path.Value ?? string.Empty;
        var rateCatalog = context.RequestServices.GetService<GatewayEndpointGroupCatalog>();
        context.Response.Headers["X-RateLimit-Limit"] =
            RateLimiterConfiguration.ResolveRateLimit(path, rateCatalog).ToString();

        // Cache-Control — short cache for reads, no-store for mutations
        if (!context.Response.Headers.ContainsKey("Cache-Control"))
        {
            context.Response.Headers.CacheControl = context.Request.Method == "GET"
                ? "private, max-age=5"
                : "no-store";
        }

        // Queue load indicators — present when the queue is enabled
        if (queueSvc?.Enabled == true)
        {
            context.Response.Headers["X-Queue-Pending"] = queueSvc.PendingCount.ToString();
            var avg = queueSvc.Metrics.AverageProcessingMs;
            if (avg > 0)
                context.Response.Headers["X-Queue-Avg-Ms"] = avg.ToString("F0");
        }

        // Per-request queue metadata — queued mutations only
        if (meta is not null)
        {
            context.Response.Headers["X-Queue-Position"] = meta.Position.ToString();
            context.Response.Headers["X-Queue-Processing-Ms"] = meta.ProcessingMs.ToString("F0");
        }

        // Retry-After on 503 (queue full) — estimated wait in seconds
        if (context.Response.StatusCode == 503 && context.Items.ContainsKey("QueueFull"))
        {
            var avgMs = queueSvc?.Metrics.AverageProcessingMs > 0
                ? queueSvc.Metrics.AverageProcessingMs : 5000;
            var pending = queueSvc?.PendingCount ?? 0;
            context.Response.Headers["Retry-After"] = Math.Max(5,
                (int)Math.Ceiling(pending * avgMs / 1000.0)).ToString();
        }

        return Task.CompletedTask;
    });

    await next();
});

// ── Health probes (short-circuit before security) ────────────────
app.Use(async (context, next) =>
{
    var path = context.Request.Path;

    if (path.StartsWithSegments("/healthz"))
    {
        context.Response.StatusCode = 200;
        await context.Response.WriteAsJsonAsync(new { status = "healthy" });
        return;
    }

    if (path.StartsWithSegments("/readyz"))
    {
        var queueSvc = context.RequestServices.GetRequiredService<RequestQueueService>();
        var coreApiClient = context.RequestServices.GetRequiredService<InternalApiClient>();

        var checks = new Dictionary<string, string>
        {
            ["queue"] = queueSvc.Enabled ? "ok" : "disabled"
        };

        try
        {
            using var probe = new HttpRequestMessage(HttpMethod.Get, "/health");
            using var response = await coreApiClient.SendRawAsync(probe, CancellationToken.None);
            checks["coreApi"] = response.IsSuccessStatusCode ? "ok" : $"status:{(int)response.StatusCode}";
        }
        catch
        {
            checks["coreApi"] = "unreachable";
        }

        var ready = checks.Values.All(v => v is "ok" or "disabled");
        context.Response.StatusCode = ready ? 200 : 503;
        await context.Response.WriteAsJsonAsync(new { status = ready ? "ready" : "not_ready", checks });
        return;
    }

    await next();
});

// ── Middleware pipeline (order matters) ──────────────────────────
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.UseSwaggerUI(options =>
    {
        options.SwaggerEndpoint("/openapi/v1.json", "SharpClaw Gateway v1");
    });
}

app.UseHttpsRedirection();

if (loggingOptions.RequestLoggingEnabled)
    app.UseSerilogRequestLogging();

// 1. Endpoint gate — reject requests to disabled endpoint groups
app.UseMiddleware<EndpointGateMiddleware>();

// 2. IP ban check — reject banned IPs before any other processing
app.UseMiddleware<IpBanMiddleware>();

// 3. Anti-spam — body size, content-type validation
app.UseMiddleware<AntiSpamMiddleware>();

// 4. Rate limiting
app.UseRateLimiter();
((IApplicationBuilder)app).Properties[GatewayModuleEndpointMapping.RateLimiterReadyKey] = true;

app.UseAuthorization();

app.MapControllers();
app.MapChatStreamProxy();

// ── Module-contributed endpoint groups (Phase 3) ────────────────
// Must run AFTER UseRateLimiter so RequireRateLimiting on the route
// groups attaches the limiter middleware in the correct order.
app.MapGatewayModuleEndpoints();

try
{
    app.Run();
}
finally
{
    gatewayPaths.DeleteDiscoveryEntry();
}

await logging.FlushAndSealAsync();
