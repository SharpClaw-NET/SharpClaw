using Microsoft.EntityFrameworkCore;
using SharpClaw.Modules.Transcription.Models;

namespace SharpClaw.Modules.Transcription;

/// <summary>
/// EF DbContext for Transcription-owned entities.
/// Audit fields (Id, CreatedAt, UpdatedAt) are set by the host-injected
/// <c>ModuleJsonSaveChangesInterceptor</c> in JSON mode, which covers all
/// save paths. The override here is intentionally removed to avoid
/// double-setting those fields.
/// </summary>
public sealed class TranscriptionDbContext(DbContextOptions<TranscriptionDbContext> options)
    : DbContext(options)
{
    public DbSet<TranscriptionSegmentDB> TranscriptionSegments => Set<TranscriptionSegmentDB>();
    public DbSet<TranscriptionJobDB> TranscriptionJobs => Set<TranscriptionJobDB>();
}
