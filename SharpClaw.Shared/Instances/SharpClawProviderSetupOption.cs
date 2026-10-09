namespace SharpClaw.Shared.Instances;


public sealed record SharpClawProviderSetupOption(
    string Key,
    string DisplayName,
    bool RequiresApiKey,
    bool RequiresEndpoint);
