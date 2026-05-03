namespace SharpClaw.Modules.Http;

/// <summary>
/// Trigger and parameter keys owned by the HTTP module.
/// String values are persisted verbatim in binding rows and serialized
/// scripts.
/// </summary>
public static class HttpTriggerKeys
{
    /// <summary>Trigger-key value persisted in <c>TaskTriggerBindingDB.Kind</c>.</summary>
    public const string Webhook = "Webhook";

    // Parameter names — must match TaskTriggerDefinition property names.
    public const string WebhookRoute           = "WebhookRoute";
    public const string WebhookSecretEnvVar    = "WebhookSecretEnvVar";
    public const string WebhookSignatureHeader = "WebhookSignatureHeader";
}
