using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SharpClaw.Gateway;
using SharpClaw.Gateway.Infrastructure;
using SharpClaw.Gateway.Security;

namespace SharpClaw.Tests.Gateway;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812", Justification = "NUnit constructs this fixture through reflection.")]
[TestFixture]
internal sealed class GatewayIngressAdmissionTests
{
    [TestCase(65536), TestCase(65537), TestCase(131072)]
    public async Task UnknownLengthBodiesAreAdmittedByActualConsumedBytesAsync(int bytes)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.ContentType = "application/json; charset=utf-8";
        context.Features.Set<IHttpRequestBodyDetectionFeature>(new BodyPresent());
        using var original = new MemoryStream(new byte[bytes]);
        using var response = new MemoryStream();
        context.Request.Body = original; context.Response.Body = response;
        var calls = 0;
        var middleware = new AntiSpamMiddleware(async admitted =>
        {
            calls++;
            using var output = new MemoryStream();
            await admitted.Request.Body.CopyToAsync(output, CancellationToken.None).ConfigureAwait(false);
            output.Length.Should().Be(bytes);
        }, new IpBanService(), NullLogger<AntiSpamMiddleware>.Instance);
        await middleware.InvokeAsync(context).ConfigureAwait(false);
        calls.Should().Be(bytes > 65536 ? 0 : 1);
        if (bytes > 65536) context.Response.StatusCode.Should().Be(StatusCodes.Status413PayloadTooLarge);
        context.Request.Body.Should().BeSameAs(original);
        original.CanRead.Should().BeTrue();
        original.Position.Should().Be(Math.Min(bytes, 65537));
    }

    [Test]
    public async Task ActualMappedRoutesApplyDistinctChatAndGlobalPartitionsAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton<IpBanService>();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddHttpClient<InternalApiClient>();
        builder.Services.AddSharpClawRateLimiting();
        var app = builder.Build();
        await using var appDisposal = app.ConfigureAwait(false);
        app.UseRouting();
        app.Use((HttpContext context, RequestDelegate next) =>
        {
            context.Connection.RemoteIpAddress = IPAddress.Parse(context.Request.Headers["X-Test-IP"].ToString());
            return next(context);
        });
        app.UseRateLimiter();
        app.Use((HttpContext context, RequestDelegate _) => { context.Response.StatusCode = 204; return Task.CompletedTask; });
        app.MapGatewayProxyEndpoints();
        await app.StartAsync(CancellationToken.None).ConfigureAwait(false);
        using var http = new HttpClient { BaseAddress = new Uri(app.Urls.Should().ContainSingle().Which) };
        http.DefaultRequestHeaders.Add("X-Test-IP", "192.0.2.1");
        try
        {
            await AssertQuotaAsync(http, "/api/chat/stateless", 20).ConfigureAwait(false);
            await AssertQuotaAsync(http, "/api/health", 60).ConfigureAwait(false);
            http.DefaultRequestHeaders.Remove("X-Test-IP");
            http.DefaultRequestHeaders.Add("X-Test-IP", "192.0.2.2");
            using var independent = await http.GetAsync(new Uri("/api/chat/stateless", UriKind.Relative), CancellationToken.None).ConfigureAwait(false);
            independent.StatusCode.Should().Be(HttpStatusCode.NoContent);
        }
        finally { await app.StopAsync(CancellationToken.None).ConfigureAwait(false); }
    }

    private static async Task AssertQuotaAsync(HttpClient http, string path, int limit)
    {
        for (var i = 0; i < limit; i++)
        {
            using var accepted = await http.GetAsync(new Uri(path, UriKind.Relative), CancellationToken.None).ConfigureAwait(false);
            accepted.StatusCode.Should().Be(HttpStatusCode.NoContent);
        }
        using var refused = await http.GetAsync(new Uri(path, UriKind.Relative), CancellationToken.None).ConfigureAwait(false);
        refused.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        refused.Headers.GetValues("X-RateLimit-Limit").Should().Equal(limit.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private sealed class BodyPresent : IHttpRequestBodyDetectionFeature { public bool CanHaveBody => true; }
}
