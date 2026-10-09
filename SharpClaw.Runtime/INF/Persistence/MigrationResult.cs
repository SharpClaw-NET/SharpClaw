using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace SharpClaw.Runtime.INF.Persistence;


/// <summary>Result of a migration attempt.</summary>
public record MigrationResult(bool Applied, bool AlreadyInProgress, IReadOnlyList<string> Migrations, string Message)
{
    public static MigrationResult Success(List<string> names) => new(true, false, names, $"Applied {names.Count} migration(s).");
    public static MigrationResult NoPending() => new(false, false, [], "No pending migrations.");
    public static MigrationResult AlreadyRunning() => new(false, true, [], "A migration is already in progress.");
}
