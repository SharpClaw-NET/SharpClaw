using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace SharpClaw.Runtime.INF.Persistence;


/// <summary>Current migration status snapshot.</summary>
public record MigrationStatusResult(MigrationState State, IReadOnlyList<string> Applied, IReadOnlyList<string> Pending);
