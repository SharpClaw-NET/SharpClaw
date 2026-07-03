using SharpClaw.Contracts.Persistence;

namespace SharpClaw.Infrastructure.Persistence.Modules;

public sealed class ModuleDbContextOptions
{
    public StorageMode StorageMode { get; init; }
    public string? ConnectionString { get; init; }
}
