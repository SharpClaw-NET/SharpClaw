namespace SharpClaw.Contracts.Exceptions;

/// <summary>
/// Thrown (or used as a signal) when a JWT access token is structurally
/// valid but has expired.  Third-party clients should catch this or check
/// for the <c>"access_token_expired"</c> error code in the 401 JSON body
/// and perform a token refresh via <c>POST /auth/refresh</c>.
/// </summary>
public sealed class AccessTokenExpiredException : Exception
{
    public const string ErrorCode = "access_token_expired";

    public AccessTokenExpiredException()
        : base("The access token has expired. Use your refresh token to obtain a new one.") { }

    public AccessTokenExpiredException(string message) : base(message) { }

    public AccessTokenExpiredException(string message, Exception innerException)
        : base(message, innerException) { }
}
