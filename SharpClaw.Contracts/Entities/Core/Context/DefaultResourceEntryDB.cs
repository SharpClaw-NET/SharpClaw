using SharpClaw.Contracts.Entities;

namespace SharpClaw.Contracts.Entities.Core.Context;

/// <summary>
/// A single keyed default-resource entry within a <see cref="DefaultResourceSetDB"/>.
/// The <see cref="ResourceKey"/> is the module-contributed default-resource
/// key (e.g. the value registered via
/// <c>ModuleResourceTypeDescriptor.DefaultResourceKey</c>).  The host does not
/// know the set of valid keys; that is owned entirely by registered modules.
/// </summary>
public class DefaultResourceEntryDB : BaseEntity
{
    public Guid DefaultResourceSetId { get; set; }

    /// <summary>
    /// Module-contributed default-resource key (case-insensitive).
    /// </summary>
    public string ResourceKey { get; set; } = string.Empty;

    /// <summary>
    /// The resource ID configured as the default for this key.
    /// </summary>
    public Guid ResourceId { get; set; }

    public DefaultResourceSetDB? DefaultResourceSet { get; set; }
}
