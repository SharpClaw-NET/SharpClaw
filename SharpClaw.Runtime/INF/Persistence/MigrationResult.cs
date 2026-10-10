using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace SharpClaw.Runtime.INF.Persistence;


/// <summary>Result of a migration attempt.</summary>
public record MigrationResult(bool Applied, bool AlreadyInProgress, IReadOnlyList<string> Migrations, string Message)
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1002", Justification = "The List parameter is an existing published method signature; replacing it would break binary callers.")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "MA0016", Justification = "Keep the existing published List parameter for binary compatibility.")]
    public static MigrationResult Success(List<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        return new(true, false, names, $"Applied {names.Count} migration(s).");
    }
    public static MigrationResult NoPending() => new(false, false, [], "No pending migrations.");
    public static MigrationResult AlreadyRunning() => new(false, true, [], "A migration is already in progress.");
}
