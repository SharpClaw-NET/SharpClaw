namespace SharpClaw.Application.Services;

/// <summary>
/// Scoped service that holds the identity of the current session user.
/// <para>
/// <b>API path:</b> populated by <c>JwtSessionMiddleware</c> which
/// validates the <c>Authorization: Bearer &lt;token&gt;</c> header and
/// extracts the user ID from the JWT <c>sub</c> claim.
/// </para>
/// <para>
/// <b>CLI path:</b> set explicitly by <c>CliDispatcher</c> from the
/// logged-in CLI session before each command is dispatched.
/// </para>
/// </summary>
public sealed class SessionService
{
    /// <summary>The authenticated user's ID, or <c>null</c> when no user session is active.</summary>
    public Guid? UserId { get; set; }
}
