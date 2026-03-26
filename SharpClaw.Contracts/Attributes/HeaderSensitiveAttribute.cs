namespace SharpClaw.Contracts.Attributes;

/// <summary>
/// Marks an entity property as containing sensitive data (secrets, API keys,
/// password hashes, etc.) that must never be expanded into custom chat headers
/// via the tag processor.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class HeaderSensitiveAttribute : Attribute;
