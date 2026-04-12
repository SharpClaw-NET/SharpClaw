namespace SharpClaw.Contracts.Modules;

/// <summary>
/// Describes a UI element that a module contributes to a specific location
/// in client applications. Clients render these elements at the designated
/// <see cref="ContributionPoint"/> based on <see cref="ElementType"/>.
/// <para>
/// Modules return these from <see cref="ISharpClawModule.GetUiContributions"/>
/// during registration. The host exposes them via the
/// <c>GET /modules/ui-contributions</c> endpoint.
/// </para>
/// </summary>
/// <param name="ContributionPoint">
/// Named location in the client UI where this element should appear
/// (e.g. <c>"chat_input_actions"</c>, <c>"settings_sidebar"</c>,
/// <c>"channel_settings"</c>, <c>"dashboard_cards"</c>).
/// </param>
/// <param name="ElementType">
/// Type of UI element to render (e.g. <c>"button"</c>, <c>"toggle"</c>,
/// <c>"panel"</c>).
/// </param>
/// <param name="ElementId">
/// Unique identifier for this contribution, scoped to the module
/// (e.g. <c>"mic_button"</c>).
/// </param>
/// <param name="Icon">
/// Optional emoji or icon glyph for the element
/// (e.g. <c>"\uE720"</c> for a microphone).
/// </param>
/// <param name="Label">
/// Optional human-readable label for the element.
/// </param>
/// <param name="Tooltip">
/// Optional tooltip text.
/// </param>
/// <param name="ActionToolName">
/// Optional tool name to invoke when the element is activated.
/// Used for button-type elements.
/// </param>
/// <param name="RequiredModuleId">
/// Optional module ID that must be enabled for this contribution to appear.
/// Defaults to the owning module's ID when <c>null</c>.
/// </param>
/// <param name="Metadata">
/// Optional key-value metadata for client-specific rendering hints
/// (e.g. <c>{"position": "left"}</c>, <c>{"style": "accent"}</c>).
/// </param>
public sealed record ModuleUiContribution(
    string ContributionPoint,
    string ElementType,
    string ElementId,
    string? Icon = null,
    string? Label = null,
    string? Tooltip = null,
    string? ActionToolName = null,
    string? RequiredModuleId = null,
    IReadOnlyDictionary<string, string>? Metadata = null);
