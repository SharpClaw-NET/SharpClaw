namespace SharpClaw.TestFixtures.ForeignSidecar;

internal static class ForeignSidecarProtocol
{
    public static readonly string[] Capabilities =
    ["endpoints", "jobTools", "inlineTools", "streamingTools", "protocolContracts",
        "registrationContributionDescriptors", "frontendContributions", "lifecycleHooks", "providerPlugins"];
    public static readonly string[] CliAliases = ["smp"];
    public static readonly string[] CliUsage = ["sample ping"];
    public static readonly string[] ReasoningEfforts = ["none", "low", "medium"];
    public static readonly string[] ModelIds = ["sample-model", "sample-vision-model"];
    public static readonly string[] ModelCapabilities = ["chat", "vision"];
}
