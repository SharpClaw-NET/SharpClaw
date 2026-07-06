using System.Net;

namespace SharpClaw.Runtime.BLL.Modules.Foreign;

internal sealed class ForeignModuleProtocolException : Exception
{
    public ForeignModuleProtocolException(
        string message,
        HttpStatusCode? statusCode = null,
        string? responseBody = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        StatusCode = statusCode;
        ResponseBody = responseBody;
    }

    public HttpStatusCode? StatusCode { get; }
    public string? ResponseBody { get; }
}
