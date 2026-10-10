using System.Text;
using System.Net.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging.Abstractions;
using SharpClaw.Runtime.Host.Api;

namespace SharpClaw.Tests.Kernel;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812",
    Justification = "NUnit discovers and constructs this internal fixture through reflection; its tests are executed by the maintained test suite.")]
[TestFixture]
internal sealed class ExceptionHandlingMiddlewareTests
{
    [Test]
    public async Task GenericFailure_ReturnsStableMessageWithoutInternalDetailsAsync()
    {
        var body = new MemoryStream();
        await using var bodyAsyncDisposal = body.ConfigureAwait(false);
        var context = new DefaultHttpContext();
        context.Response.Body = body;
        var middleware = new ExceptionHandlingMiddleware(
            _ => throw new IOException("provider secret and storage detail"),
            NullLogger<ExceptionHandlingMiddleware>.Instance);

        await middleware.InvokeAsync(context).ConfigureAwait(false);

        context.Response.StatusCode.Should().Be(StatusCodes.Status500InternalServerError);
        var response = await ReadBodyAsync(body).ConfigureAwait(false);
        response.Should().Contain("An internal server error occurred.");
        response.Should().NotContain("provider secret and storage detail");
    }

    [Test]
    public Task InvalidOperationFailure_ReturnsStable500WithoutInternalDetailsAsync()
        => AssertGeneralFailureIsRedactedAsync(
            new InvalidOperationException("manifest path and provider registration detail"));

    [Test]
    public Task NotSupportedFailure_ReturnsStable500WithoutInternalDetailsAsync()
        => AssertGeneralFailureIsRedactedAsync(
            new NotSupportedException("unsupported provider capability detail"));

    [Test]
    public Task HttpRequestFailure_ReturnsStable500WithoutInternalDetailsAsync()
        => AssertGeneralFailureIsRedactedAsync(
            new HttpRequestException("upstream address and transport detail"));

    [Test]
    public async Task RequestCancellation_ReturnsClientClosedStatusWithoutServerErrorBodyAsync()
    {
        var body = new MemoryStream();
        await using var bodyAsyncDisposal_ = body.ConfigureAwait(false);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync().ConfigureAwait(false);
        var context = new DefaultHttpContext();
        context.RequestAborted = cancellation.Token;
        context.Response.Body = body;
        var middleware = new ExceptionHandlingMiddleware(
            _ => throw new OperationCanceledException(cancellation.Token),
            NullLogger<ExceptionHandlingMiddleware>.Instance);

        await middleware.InvokeAsync(context).ConfigureAwait(false);

        context.Response.StatusCode.Should().Be(499);
        body.Length.Should().Be(0);
    }

    [Test]
    public async Task FailureAfterResponseStarted_IsRethrownAsync()
    {
        var context = new DefaultHttpContext();
        context.Features.Set<IHttpResponseFeature>(new StartedResponseFeature());
        var middleware = new ExceptionHandlingMiddleware(
            _ => throw new IOException("partial response failure"),
            NullLogger<ExceptionHandlingMiddleware>.Instance);

        var exception = Assert.ThrowsAsync<Exception>(() => middleware.InvokeAsync(context));

        exception.Should().NotBeNull();
        exception!.Message.Should().Be("partial response failure");
    }

    private static async Task<string> ReadBodyAsync(MemoryStream body)
    {
        body.Position = 0;
        using var reader = new StreamReader(body, Encoding.UTF8, leaveOpen: true);
        return await reader.ReadToEndAsync(TestContext.CurrentContext.CancellationToken).ConfigureAwait(false);
    }

    private static async Task AssertGeneralFailureIsRedactedAsync(Exception exception)
    {
        var body = new MemoryStream();
        await using var bodyAsyncDisposal__ = body.ConfigureAwait(false);
        var context = new DefaultHttpContext();
        context.Response.Body = body;
        var middleware = new ExceptionHandlingMiddleware(
            _ => throw exception,
            NullLogger<ExceptionHandlingMiddleware>.Instance);

        await middleware.InvokeAsync(context).ConfigureAwait(false);

        context.Response.StatusCode.Should().Be(StatusCodes.Status500InternalServerError);
        var response = await ReadBodyAsync(body).ConfigureAwait(false);
        response.Should().Contain("An internal server error occurred.");
        response.Should().NotContain(exception.Message);
    }

    private sealed class StartedResponseFeature : IHttpResponseFeature
    {
        public int StatusCode { get; set; } = StatusCodes.Status200OK;

        public string? ReasonPhrase { get; set; }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "HLQ001",
            Justification = "IHttpResponseFeature requires an IHeaderDictionary property; the test double must implement that exact framework signature.")]
        public IHeaderDictionary Headers { get; set; } = new HeaderDictionary();

        public Stream Body { get; set; } = Stream.Null;

        public bool HasStarted => true;

        public void OnStarting(Func<object, Task> callback, object state)
        {
        }

        public void OnCompleted(Func<object, Task> callback, object state)
        {
        }
    }
}
