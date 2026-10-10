using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace SharpClaw.Gateway.Infrastructure;

/// <summary>
/// Global exception filter that wraps unhandled controller exceptions
/// in the standard gateway error envelope.
/// </summary>
internal sealed class ErrorEnvelopeFilter(
    ILogger<ErrorEnvelopeFilter> logger) : IExceptionFilter
{
    public void OnException(ExceptionContext context)
    {
        GatewayLog.UnhandledControllerError(logger, context.Exception,
            context.RouteData.Values["controller"],
            context.RouteData.Values["action"]);

        var requestId = context.HttpContext.Items.TryGetValue("RequestId", out var id) && id is string s
            ? s : Guid.NewGuid().ToString("N");

        context.Result = new ObjectResult(new
        {
            error = "An internal error occurred.",
            code = GatewayErrors.InternalError,
            requestId,
        })
        {
            StatusCode = StatusCodes.Status500InternalServerError,
        };

        context.ExceptionHandled = true;
    }
}
