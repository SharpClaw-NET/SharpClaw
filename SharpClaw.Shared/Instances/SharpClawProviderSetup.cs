namespace SharpClaw.Shared.Instances;

/// <summary>Non-secret setup information reported by the selected Runtime.</summary>
public sealed record SharpClawProviderSetup(
    bool SetupRequired,
    string? ProviderKey,
    string? Model,
    IReadOnlyList<SharpClawProviderSetupOption> Providers);
