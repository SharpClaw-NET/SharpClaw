using Microsoft.EntityFrameworkCore;
using SharpClaw.Modules.SystemAudio.Models;

namespace SharpClaw.Modules.SystemAudio;

/// <summary>
/// EF DbContext for SystemAudio-owned entities. Audit fields (Id,
/// CreatedAt, UpdatedAt) are set by the host-injected
/// <c>ModuleJsonSaveChangesInterceptor</c> in JSON mode, which covers all
/// save paths.
/// </summary>
public sealed class SystemAudioDbContext(DbContextOptions<SystemAudioDbContext> options)
    : DbContext(options)
{
    public DbSet<InputAudioDB> InputAudios => Set<InputAudioDB>();
}
