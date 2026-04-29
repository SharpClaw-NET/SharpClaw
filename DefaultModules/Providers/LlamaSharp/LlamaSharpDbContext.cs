using Microsoft.EntityFrameworkCore;
using SharpClaw.Modules.Providers.LlamaSharp.Models;

namespace SharpClaw.Modules.Providers.LlamaSharp;

/// <summary>
/// EF DbContext for LlamaSharp-owned entities. Audit fields (Id,
/// CreatedAt, UpdatedAt) are set by the host-injected
/// <c>ModuleJsonSaveChangesInterceptor</c> in JSON mode, which covers all
/// save paths.
/// </summary>
public sealed class LlamaSharpDbContext(DbContextOptions<LlamaSharpDbContext> options)
    : DbContext(options)
{
    public DbSet<LocalModelFileDB> LocalModelFiles => Set<LocalModelFileDB>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.Entity<LocalModelFileDB>(e =>
        {
            e.HasIndex(f => f.ModelId).IsUnique();
            e.Property(f => f.Status).HasConversion<string>();
        });
    }
}
