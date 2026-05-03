namespace SharpClaw.Contracts.Tasks;

/// <summary>
/// Abstracts the minimal-API route registration surface so that a webhook
/// trigger source implementation can be tested without a real
/// <c>WebApplication</c> and so that module-owned trigger sources do not
/// need a compile-time dependency on ASP.NET Core hosting types.
/// <para>
/// The host (API project) provides the concrete implementation; module
/// trigger sources consume this interface via dependency injection and
/// call <see cref="EnsureRegistered"/> for each active binding.
/// </para>
/// </summary>
public interface IWebhookRouteRegistrar
{
    /// <summary>
    /// Register a POST route at the given path if it has not already been
    /// registered during this application lifetime.
    /// </summary>
    /// <param name="routePath">Absolute path, e.g. <c>/webhooks/tasks/my-hook</c>.</param>
    void EnsureRegistered(string routePath);
}
