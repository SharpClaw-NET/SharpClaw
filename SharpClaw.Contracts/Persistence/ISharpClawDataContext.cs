using SharpClaw.Contracts.Entities.Core;
using SharpClaw.Contracts.Entities.Core.Access;
using SharpClaw.Contracts.Entities.Core.Clearance;
using SharpClaw.Contracts.Entities.Core.Context;
using SharpClaw.Contracts.Entities.Core.Messages;

namespace SharpClaw.Contracts.Persistence;

/// <summary>
/// Read-only data-access contract that exposes the core entity surface
/// to modules. Modules write their own LINQ queries against the real
/// entity types through this contract; mutations remain the
/// responsibility of host-side adapters.
/// </summary>
/// <remarks>
/// The accessors return <see cref="IQueryable{T}"/> rather than
/// <c>DbSet&lt;T&gt;</c> so module code cannot mutate core state
/// through this surface. The contract grows only when a module
/// presents a real read need.
/// </remarks>
public interface ISharpClawDataContext
{
    IQueryable<AgentDB> Agents { get; }
    IQueryable<ChannelDB> Channels { get; }
    IQueryable<ChannelContextDB> AgentContexts { get; }
    IQueryable<ChatThreadDB> ChatThreads { get; }
    IQueryable<ChatMessageDB> ChatMessages { get; }
    IQueryable<PermissionSetDB> PermissionSets { get; }
    IQueryable<GlobalFlagDB> GlobalFlags { get; }
    IQueryable<RoleDB> Roles { get; }
}
