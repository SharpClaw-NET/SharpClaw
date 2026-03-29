using SharpClaw.Gateway.Bots;
using SharpClaw.Gateway.Configuration;
using SharpClaw.Gateway.Controllers;
using SharpClaw.Gateway.Infrastructure;
using SharpClaw.Gateway.Security;

var builder = WebApplication.CreateBuilder(args);

// ── Gateway .env (same pattern as Core / Interface) ──────────────
builder.Configuration.AddGatewayEnvironment(
    isDevelopment: builder.Environment.IsDevelopment());

// ── Internal API client ──────────────────────────────────────────
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

// ── Request queue (sequential forwarding to core API) ────────────
builder.Services.Configure<RequestQueueOptions>(
    builder.Configuration.GetSection(RequestQueueOptions.SectionName));

builder.Services.AddSingleton<QueueMetrics>();
builder.Services.AddSingleton<RequestQueueService>();
builder.Services.AddHostedService<RequestQueueProcessor>();
builder.Services.AddScoped<GatewayRequestDispatcher>();
builder.Services.AddHttpContextAccessor();

// ── Bot services ─────────────────────────────────────────────────
builder.Services.AddSingleton<BotReloadSignal>();
builder.Services.AddHttpClient("TelegramBot");
builder.Services.AddHttpClient("DiscordBot");
builder.Services.AddHttpClient("WhatsAppBot");
builder.Services.AddHttpClient("SlackBot");
builder.Services.AddHttpClient("MatrixBot");
builder.Services.AddHttpClient("SignalBot");
builder.Services.AddHttpClient("TeamsBot");

builder.Services.Configure<WhatsAppBotOptions>(
    builder.Configuration.GetSection(WhatsAppBotOptions.SectionName));
builder.Services.AddSingleton<WhatsAppBotState>();

builder.Services.Configure<SlackBotOptions>(
    builder.Configuration.GetSection(SlackBotOptions.SectionName));
builder.Services.AddSingleton<SlackBotState>();

builder.Services.Configure<MatrixBotOptions>(
    builder.Configuration.GetSection(MatrixBotOptions.SectionName));

builder.Services.Configure<SignalBotOptions>(
    builder.Configuration.GetSection(SignalBotOptions.SectionName));

builder.Services.Configure<EmailBotOptions>(
    builder.Configuration.GetSection(EmailBotOptions.SectionName));

builder.Services.Configure<TeamsBotOptions>(
    builder.Configuration.GetSection(TeamsBotOptions.SectionName));
builder.Services.AddSingleton<TeamsBotState>();

builder.Services.AddHostedService<TelegramBotService>();
builder.Services.AddHostedService<DiscordBotService>();
builder.Services.AddHostedService<WhatsAppBotService>();
builder.Services.AddHostedService<SlackBotService>();
builder.Services.AddHostedService<MatrixBotService>();
builder.Services.AddHostedService<SignalBotService>();
builder.Services.AddHostedService<EmailBotService>();
builder.Services.AddHostedService<TeamsBotService>();

// ── Security ─────────────────────────────────────────────────────
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
        doc.Info.Description = "Public REST gateway for the SharpClaw Application API.";
        return Task.CompletedTask;
    });
});

// ── API key diagnostic (visible in Uno process output) ───────────
var configuredApiKey = builder.Configuration[$"{InternalApiOptions.SectionName}:ApiKey"];
if (!string.IsNullOrEmpty(configuredApiKey))
    Console.WriteLine($"[gateway] API key resolved from config: {configuredApiKey.Length} chars, prefix={configuredApiKey[..Math.Min(6, configuredApiKey.Length)]}..");
else
    Console.WriteLine("[gateway] ⚠ No API key found in configuration — will fall back to file read.");

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
        context.Response.Headers["X-RateLimit-Limit"] =
            RateLimiterConfiguration.ResolveRateLimit(path).ToString();

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

// 1. Endpoint gate — reject requests to disabled endpoint groups
app.UseMiddleware<EndpointGateMiddleware>();

// 2. IP ban check — reject banned IPs before any other processing
app.UseMiddleware<IpBanMiddleware>();

// 3. Anti-spam — body size, content-type validation
app.UseMiddleware<AntiSpamMiddleware>();

// 4. Rate limiting
app.UseRateLimiter();

// 5. WebSocket support for transcription streaming
app.UseWebSockets();

app.UseAuthorization();

app.MapControllers();
app.MapTranscriptionStreamingProxy();
app.MapChatStreamProxy();
app.MapWhatsAppWebhookProxy();
app.MapSlackWebhookProxy();
app.MapTeamsWebhookProxy();

app.Run();
