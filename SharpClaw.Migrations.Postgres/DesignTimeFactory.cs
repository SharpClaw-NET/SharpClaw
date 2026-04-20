using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using SharpClaw.Infrastructure.Persistence;

namespace SharpClaw.Migrations.Postgres;

public class DesignTimeFactory : IDesignTimeDbContextFactory<SharpClawDbContext>
{
    public SharpClawDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<SharpClawDbContext>()
            .UseNpgsql("Host=localhost;Database=SharpClaw_Migrations;Username=sharpclaw;Password=sharpclaw")
            .Options;
        return new SharpClawDbContext(options);
    }
}
