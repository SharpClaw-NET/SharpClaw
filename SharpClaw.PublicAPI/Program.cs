using SharpClaw.PublicAPI.Controllers;
using SharpClaw.PublicAPI.Infrastructure;
using SharpClaw.PublicAPI.Security;

var builder = WebApplication.CreateBuilder(args);

// ── Internal API client ──────────────────────────────────────────
builder.Services.Configure<InternalApiOptions>(
    builder.Configuration.GetSection(InternalApiOptions.SectionName));

builder.Services.AddHttpClient<InternalApiClient>(client =>
{
    var baseUrl = builder.Configuration[$"{InternalApiOptions.SectionName}:BaseUrl"]
                  ?? "http://127.0.0.1:48923";
    client.BaseAddress = new Uri(baseUrl);
    client.Timeout = TimeSpan.FromSeconds(30);
});

// ── Security ─────────────────────────────────────────────────────
builder.Services.AddSingleton<IpBanService>();
builder.Services.AddSharpClawRateLimiting();

// ── MVC & OpenAPI ────────────────────────────────────────────────
builder.Services.AddControllers()
    .AddJsonOptions(o =>
    {
        o.JsonSerializerOptions.Converters.Add(
            new System.Text.Json.Serialization.JsonStringEnumConverter());
    });
builder.Services.AddOpenApi();

var app = builder.Build();

// ── Middleware pipeline (order matters) ──────────────────────────
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

// 1. IP ban check — reject banned IPs before any other processing
app.UseMiddleware<IpBanMiddleware>();

// 2. Anti-spam — body size, content-type validation
app.UseMiddleware<AntiSpamMiddleware>();

// 3. Rate limiting
app.UseRateLimiter();

// 4. WebSocket support for transcription streaming
app.UseWebSockets();

app.UseAuthorization();

app.MapControllers();
app.MapTranscriptionStreamingProxy();

app.Run();
