using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using SharpClaw.Infrastructure.Persistence;

namespace SharpClaw.Migrations.SqlServer;

public class DesignTimeFactory : IDesignTimeDbContextFactory<SharpClawDbContext>
{
    public SharpClawDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<SharpClawDbContext>()
            .UseSqlServer("Server=.;Database=SharpClaw_Migrations;Trusted_Connection=True;TrustServerCertificate=True")
            .Options;
        return new SharpClawDbContext(options);
    }
}
