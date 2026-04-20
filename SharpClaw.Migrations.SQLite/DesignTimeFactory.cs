using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using SharpClaw.Infrastructure.Persistence;

namespace SharpClaw.Migrations.SQLite;

public class DesignTimeFactory : IDesignTimeDbContextFactory<SharpClawDbContext>
{
    public SharpClawDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<SharpClawDbContext>()
            .UseSqlite("Data Source=sharpclaw_migrations.db")
            .Options;
        return new SharpClawDbContext(options);
    }
}
