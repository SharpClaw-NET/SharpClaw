using SharpClaw.Contracts.Tasks;

namespace SharpClaw.Modules.Http;

/// <summary>
/// Module-owned <see cref="ITaskTriggerAttributeHandler"/> for
/// <c>[OnWebhook]</c>. Behavior preserved verbatim from the legacy core
/// parser switch. The cross-attribute <c>[WebhookSecret]</c>/<c>[OnWebhook]</c>
/// presence check (TASK428) remains in the core parser because it spans
/// multiple attributes on the class.
/// </summary>
internal static class HttpTriggerAttributeHandlers
{
    public static IReadOnlyDictionary<string, ITaskTriggerAttributeHandler> All { get; } =
        new Dictionary<string, ITaskTriggerAttributeHandler>(StringComparer.Ordinal)
        {
            ["OnWebhook"] = new OnWebhookHandler(),
        };

    private sealed class OnWebhookHandler : ITaskTriggerAttributeHandler
    {
        public TaskTriggerDefinition? Handle(TaskTriggerAttributeContext context)
        {
            var p = new Dictionary<string, string?>(StringComparer.Ordinal);
            var route = context.GetStringArg(0);
            if (!string.IsNullOrEmpty(route))
                p[HttpTriggerKeys.WebhookRoute] = route;
            var secret = context.GetNamedStringArg("Secret");
            if (!string.IsNullOrEmpty(secret))
                p[HttpTriggerKeys.WebhookSecretEnvVar] = secret;
            var sigHeader = context.GetNamedStringArg("SignatureHeader");
            if (!string.IsNullOrEmpty(sigHeader))
                p[HttpTriggerKeys.WebhookSignatureHeader] = sigHeader;
            return new TaskTriggerDefinition
            {
                TriggerKey = HttpTriggerKeys.Webhook,
                Parameters = p,
            };
        }
    }
}
