namespace SharpClaw.Modules.ContextTools;

/// <summary>
/// Module-owned global-flag keys for the context-tools module.
/// <para>
/// The string values are the canonical wire identifiers persisted in
/// <c>GlobalFlagDB.FlagKey</c>. They are exposed to host/core code only
/// through the <see cref="SharpClaw.Contracts.Modules.IContextToolsFlagKeys"/>
/// contract so that core never names them inline.
/// </para>
/// </summary>
public static class ContextToolsPermissionKeys
{
    /// <summary>
    /// Grants permission to read conversation history from threads on
    /// channels other than the active one.
    /// </summary>
    public const string CanReadCrossThreadHistory = "CanReadCrossThreadHistory";
}
