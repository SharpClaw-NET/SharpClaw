using System.Diagnostics;

namespace SharpClaw.Application.Core.Clients;

/// <summary>
/// A <see cref="DelegatingHandler"/> that logs full HTTP request and
/// response details (method, URI, headers, body, status, timing) via
/// <see cref="Debug.WriteLine(string, string)"/> so output appears in
/// the Visual Studio <b>Output › Debug</b> pane under the
/// <c>SharpClaw.HTTP</c> category.
/// </summary>
public sealed class HttpLoggingDelegatingHandler : DelegatingHandler
{
    private const string Category = "SharpClaw.HTTP";

    [Conditional("DEBUG")]
    private static void Log(string message) => Debug.WriteLine(message, Category);

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var requestId = Guid.NewGuid().ToString("N")[..8];

        // ── Request ──────────────────────────────────────────────
        Log($"[{requestId}] >>> {request.Method} {request.RequestUri}");
        Log($"[{requestId}] >>> Request Headers:\n{request.Headers.ToString().TrimEnd()}");

        if (request.Content is not null)
        {
            Log($"[{requestId}] >>> Content Headers:\n{request.Content.Headers.ToString().TrimEnd()}");

            var contentType = request.Content.Headers.ContentType?.MediaType ?? "";
            if (IsTextContent(contentType))
            {
                var body = await request.Content.ReadAsStringAsync(cancellationToken);
                Log($"[{requestId}] >>> Body:\n{body}");
            }
            else
            {
                Log($"[{requestId}] >>> Body: <binary {contentType}, {request.Content.Headers.ContentLength} bytes>");
            }
        }

        // ── Send + timing ────────────────────────────────────────
        var sw = Stopwatch.StartNew();
        HttpResponseMessage response;
        try
        {
            response = await base.SendAsync(request, cancellationToken);
        }
        catch (Exception ex)
        {
            sw.Stop();
            Log($"[{requestId}] !!! FAILED after {sw.ElapsedMilliseconds}ms: {ex}");
            throw;
        }
        sw.Stop();

        // ── Response ─────────────────────────────────────────────
        Log($"[{requestId}] <<< {(int)response.StatusCode} {response.ReasonPhrase} ({sw.ElapsedMilliseconds}ms)");
        Log($"[{requestId}] <<< Response Headers:\n{response.Headers.ToString().TrimEnd()}");

        if (response.Content is not null)
        {
            Log($"[{requestId}] <<< Content Headers:\n{response.Content.Headers.ToString().TrimEnd()}");

            var responseContentType = response.Content.Headers.ContentType?.MediaType ?? "";
            if (IsTextContent(responseContentType))
            {
                var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
                Log($"[{requestId}] <<< Body:\n{responseBody}");
            }
            else
            {
                Log($"[{requestId}] <<< Body: <binary {responseContentType}, {response.Content.Headers.ContentLength} bytes>");
            }
        }

        return response;
    }

    private static bool IsTextContent(string mediaType) =>
        mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
        || mediaType.Contains("json", StringComparison.OrdinalIgnoreCase)
        || mediaType.Contains("xml", StringComparison.OrdinalIgnoreCase)
        || mediaType.Contains("form-urlencoded", StringComparison.OrdinalIgnoreCase);
}
