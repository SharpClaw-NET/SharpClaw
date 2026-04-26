#if DEBUG
using System.Diagnostics;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace SharpClaw.Application.API.Api;

/// <summary>
/// Outermost diagnostic middleware. For every 4xx/5xx response it writes the
/// full request context (method, path, content-type, headers, body) and the
/// response status and body directly to <see cref="Debug"/> output. This is
/// visible in the VS Output window (category "SharpClaw.CLI") and is captured
/// by the Serilog file sink in %LOCALAPPDATA% automatically — no log pipeline
/// plumbing needed.
/// </summary>
public sealed class DiagnosticMiddleware(RequestDelegate next)
{
    private const string Category = "SharpClaw.CLI";

    // Anything larger than this is truncated in the log to avoid flooding.
    private const int BodyLogLimit = 4096;

    public async Task InvokeAsync(HttpContext context)
    {
        // Enable buffering so the request body can be read here AND again by
        // downstream middleware / parameter binding.
        context.Request.EnableBuffering();

        // Swap in a buffered response stream so we can inspect the body after
        // the rest of the pipeline has finished writing.
        var originalResponseBody = context.Response.Body;
        await using var responseBuffer = new MemoryStream();
        context.Response.Body = responseBuffer;

        try
        {
            await next(context);
        }
        finally
        {
            var statusCode = context.Response.StatusCode;

            if (statusCode >= 400 && statusCode < 600)
            {
                var requestBody = await ReadRequestBodyAsync(context.Request);
                var responseBody = await ReadResponseBufferAsync(responseBuffer);

                var sb = new StringBuilder();
                sb.AppendLine($"[Diagnostic] {context.Request.Method} {context.Request.Path}{context.Request.QueryString} → HTTP {statusCode}");
                sb.AppendLine($"  Content-Type  : {context.Request.ContentType ?? "(none)"}");
                sb.AppendLine($"  Content-Length: {context.Request.ContentLength?.ToString() ?? "(not set)"}");
                sb.AppendLine("  Request headers:");
                sb.Append(FormatHeaders(context.Request.Headers));
                sb.AppendLine($"  Request body  : {requestBody}");
                sb.AppendLine($"  Response body : {responseBody}");

                Debug.WriteLine(sb.ToString(), Category);
            }

            // Copy the buffered response back to the real stream.
            responseBuffer.Seek(0, SeekOrigin.Begin);
            await responseBuffer.CopyToAsync(originalResponseBody);
            context.Response.Body = originalResponseBody;
        }
    }

    private static async Task<string> ReadRequestBodyAsync(HttpRequest request)
    {
        if (request.ContentLength is 0 || request.Body is null)
            return "(empty)";

        request.Body.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(request.Body, Encoding.UTF8, detectEncodingFromByteOrderMarks: false,
            bufferSize: 1024, leaveOpen: true);
        var body = await reader.ReadToEndAsync();
        request.Body.Seek(0, SeekOrigin.Begin);

        if (body.Length == 0)
            return "(empty)";

        return body.Length > BodyLogLimit
            ? body[..BodyLogLimit] + $"… [truncated, total {body.Length} chars]"
            : body;
    }

    private static async Task<string> ReadResponseBufferAsync(MemoryStream buffer)
    {
        if (buffer.Length == 0)
            return "(empty — nothing written to response stream)";

        buffer.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(buffer, Encoding.UTF8, detectEncodingFromByteOrderMarks: false,
            bufferSize: 1024, leaveOpen: true);
        var body = await reader.ReadToEndAsync();

        return body.Length > BodyLogLimit
            ? body[..BodyLogLimit] + $"… [truncated, total {body.Length} chars]"
            : body;
    }

    private static string FormatHeaders(IHeaderDictionary headers)
    {
        var sb = new StringBuilder();
        foreach (var (key, value) in headers)
        {
            // Avoid leaking auth tokens verbatim; show presence only.
            var display = key.Equals("X-Api-Key", StringComparison.OrdinalIgnoreCase)
                          || key.Equals("Authorization", StringComparison.OrdinalIgnoreCase)
                ? $"[{value.Count} value(s), redacted]"
                : value.ToString();
            sb.AppendLine($"    {key}: {display}");
        }
        return sb.ToString();
    }
}
#endif
