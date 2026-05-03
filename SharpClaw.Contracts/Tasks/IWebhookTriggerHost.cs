namespace SharpClaw.Contracts.Tasks;

/// <summary>
/// Host-facing abstraction for the webhook trigger source implementation
/// that lives in a module. Lets the API project bridge incoming HTTP
/// requests to the module-owned source without taking a compile-time
/// dependency on the module assembly.
/// </summary>
public interface IWebhookTriggerHost
{
    /// <summary>
    /// Supplies the host-provided minimal-API route registrar. Called once
    /// after the <c>WebApplication</c> is built; subsequent
    /// <c>StartAsync</c> calls in the trigger source forward each active
    /// binding's route through the registrar.
    /// </summary>
    void SetRouteRegistrar(IWebhookRouteRegistrar registrar);

    /// <summary>
    /// Returns the HTTP status code (202 = fire, 401 = bad signature, 404 = not found)
    /// for an incoming webhook request.
    /// </summary>
    Task<int> HandleRequestAsync(
        string routePath,
        string body,
        string headersJson,
        CancellationToken ct = default);
}
