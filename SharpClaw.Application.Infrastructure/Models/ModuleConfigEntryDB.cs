using SharpClaw.Contracts.Entities;

namespace SharpClaw.Application.Infrastructure.Models;

/// <summary>
/// Persistent key-value configuration entry for a module.
/// Stored in the host <c>AppDbContext</c>, not accessible directly by modules.
/// Modules read/write through <see cref="Contracts.Modules.IModuleConfigStore"/>.
/// </summary>
public class ModuleConfigEntryDB : BaseEntity
{
    /// <summary>Module identifier that owns this entry.</summary>
    public required string ModuleId { get; set; }

    /// <summary>Configuration key (max 128 characters).</summary>
    public required string Key { get; set; }

    /// <summary>Configuration value (max 4096 characters).</summary>
    public string? Value { get; set; }
}
