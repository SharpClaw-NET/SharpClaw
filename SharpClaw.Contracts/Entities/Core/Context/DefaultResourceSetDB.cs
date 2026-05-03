using SharpClaw.Contracts.Entities;

namespace SharpClaw.Contracts.Entities.Core.Context;

/// <summary>
/// Stores default resource IDs for per-resource action types, indexed by
/// module-contributed resource key.  Attached to a channel or context so
/// that jobs submitted without an explicit resource ID automatically use the
/// configured default.
/// <para>
/// Keys are owned by registered modules via
/// <c>ModuleResourceTypeDescriptor.DefaultResourceKey</c>.  The host does
/// not know the set of valid keys at compile time.
/// </para>
/// <para>
/// This is independent of <see cref="Clearance.PermissionSetDB"/> —
/// permission sets control <em>what you are allowed to do</em>, while this
/// entity controls <em>which resource to use by default</em>.
/// </para>
/// </summary>
public class DefaultResourceSetDB : BaseEntity
{
    /// <summary>
    /// Generic keyed default-resource entries contributed by modules.
    /// </summary>
    public List<DefaultResourceEntryDB> Entries { get; set; } = [];
}

