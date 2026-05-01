using Microsoft.AspNetCore.Http;

namespace SharpClaw.Gateway.Abstractions;

/// <summary>
/// Helpers for translating a <see cref="QueuedResponse"/> into an
/// <see cref="IResult"/> so module-contributed endpoints can return the
/// dispatcher's outcome directly without leaking gateway implementation
/// types into module code.
/// </summary>
public static class QueuedResponseExtensions
{
    /// <summary>
    /// Convert a <see cref="QueuedResponse"/> to an <see cref="IResult"/>.
    /// Successful responses are returned as <c>application/json</c> with the
    /// queue's <see cref="QueuedResponse.JsonBody"/> verbatim; failures
    /// surface a JSON envelope <c>{ error, code, status }</c> mirroring the
    /// gateway error format for consistency.
    /// </summary>
    public static IResult ToResult(this QueuedResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);

        var status = (int)response.StatusCode;

        if (response.IsSuccess)
        {
            return string.IsNullOrEmpty(response.JsonBody)
                ? Results.StatusCode(status)
                : Results.Content(response.JsonBody, "application/json", statusCode: status);
        }

        // Forward the upstream error body as-is when present so callers see
        // the exact API response. Otherwise fall back to a synthetic envelope.
        if (!string.IsNullOrEmpty(response.JsonBody))
        {
            return Results.Content(response.JsonBody, "application/json", statusCode: status);
        }

        return Results.Json(
            new { error = response.Error ?? "Upstream error.", status },
            statusCode: status);
    }
}
