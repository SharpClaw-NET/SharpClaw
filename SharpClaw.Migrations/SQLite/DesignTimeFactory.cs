using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using SharpClaw.Runtime.INF.Persistence;

namespace SharpClaw.Migrations.SQLite;

public class DesignTimeFactory : IDesignTimeDbContextFactory<SharpClawDbContext>
{
    public SharpClawDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<SharpClawDbContext>()
            .UseSqlite(
                "Data Source=sharpclaw_migrations.db",
                provider => provider.MigrationsAssembly(
                    typeof(DesignTimeFactory).Assembly.GetName().Name))
            .Options;
        return new SharpClawDbContext(options);
    }
}
