using SharpClaw.Contracts.Entities;

namespace SharpClaw.Application.Infrastructure.Models.Jobs;

/// <summary>
/// A single timestamped log entry within an <see cref="AgentJobDB"/>.
/// </summary>
public class AgentJobLogEntryDB : BaseEntity
{
    public Guid AgentJobId { get; set; }
    public AgentJobDB AgentJob { get; set; } = null!;

    public required string Message { get; set; }

    /// <summary>"Info", "Warning", or "Error".</summary>
    public string Level { get; set; } = "Info";
}
