using SharpClaw.Contracts.Persistence;

namespace SharpClaw.Runtime.INF.Persistence.Registrations;

public sealed class RegistrationDbContextOptions
{
    public StorageMode StorageMode { get; init; }
    public string? ConnectionString { get; init; }
}
